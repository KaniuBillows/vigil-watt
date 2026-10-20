using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Plugins;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 以假 I/O 验证同路径句柄恢复、身份连续性与旧任务隔离。
/// 2026/10/20 20:32:32 CodexSandboxOffline
/// </summary>
public class PluginHandleRecoveryTests
{
    private static HidDeviceInfo Info(string path = "same", ushort unit = 1) => new()
    { DevicePath = path, Version = unit, InputReportLength = 8, FeatureReportLength = 8 };

    [Fact]
    public async Task SamePathBadHandleIsClosedAndReprobedWithOneIdentity()
    {
        using var plugin = new TestPlugin();
        var claims = new DeviceClaimRegistry();
        var original = Assert.Single(await plugin.AttachAsync([Info()], claims));
        int signals = 0;
        plugin.RecoveryNeeded += () => signals++;
        plugin.Poll();
        plugin.Io[0].FeatureError = 1167;
        plugin.Poll();
        plugin.Poll();
        Assert.Equal(1, signals);
        var replacement = Assert.Single(await plugin.RecoverAsync([Info("SAME")], claims));
        Assert.Equal(original.Id, replacement.Id);
        Assert.True(replacement.IsOnline);
        Assert.Equal(2, plugin.Io.Count);
        Assert.Equal(1, plugin.Io[0].Disposals);
        Assert.True(claims.IsClaimedBy("same", plugin.Name));
    }

    [Fact]
    public async Task PendingNativeWritePreventsReopenAndKeepsClaimUntilDrain()
    {
        using var plugin = new TestPlugin();
        var claims = new DeviceClaimRegistry();
        await plugin.AttachAsync([Info()], claims);
        var old = plugin.Handles[0];
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        plugin.Io[0].Write = _ => blocked.Task;
        old.WriteOutput([0x04], TimeSpan.Zero, quiet: true);
        plugin.Io[0].FeatureError = 6;
        old.GetFeature(0x04);
        Assert.False(Assert.Single(await plugin.RecoverAsync([Info()], claims)).IsOnline);
        for (int i = 0; i < 100; i++) await plugin.RecoverAsync([Info()], claims);
        Assert.Single(plugin.Io);
        Assert.False(claims.TryClaim("same", "Other"));
        blocked.SetResult();
        await plugin.RetirementCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Assert.Single(await plugin.RecoverAsync([Info()], claims)).IsOnline);
        Assert.Equal(2, plugin.Io.Count);
    }

    [Fact]
    public async Task PendingOldInterfaceAlsoBlocksAliasProbeUntilPhysicalSessionDrains()
    {
        using var plugin = new TestPlugin();
        var claims = new DeviceClaimRegistry();
        await plugin.AttachAsync([Info("old")], claims);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        plugin.Io[0].Write = _ => blocked.Task;
        plugin.Handles[0].WriteOutput([0x04], TimeSpan.Zero, quiet: true);
        Assert.False(Assert.Single(await plugin.AttachAsync([Info("new")], claims)).IsOnline);
        Assert.Single(plugin.Io);
        blocked.SetResult();
        await plugin.RetirementCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Assert.Single(await plugin.AttachAsync([Info("new")], claims)).IsOnline);
        Assert.False(claims.IsClaimedBy("old", plugin.Name));
    }

    [Fact]
    public async Task ReplacementIdentityIsVerifiedInsteadOfOverwritingOldDevice()
    {
        using var plugin = new TestPlugin();
        var claims = new DeviceClaimRegistry();
        var original = Assert.Single(await plugin.AttachAsync([Info()], claims));
        plugin.Io[0].FeatureError = 6;
        plugin.Handles[0].GetFeature(0x04);
        var updates = await plugin.RecoverAsync([Info(unit: 2)], claims);
        Assert.Single(updates, d => d.Id == original.Id && !d.IsOnline);
        Assert.Single(updates, d => d.Id != original.Id && d.IsOnline);
    }

    [Fact]
    public async Task ReopeningCannotResetAnomalyFilterToUnconfirmedProbeReading()
    {
        using var plugin = new TestPlugin();
        var claims = new DeviceClaimRegistry();
        await plugin.AttachAsync([Info()], claims);
        plugin.Io[0].FeatureError = 6;
        plugin.Handles[0].GetFeature(0x04);
        plugin.ProbePercent = 99;
        var replacement = Assert.Single(await plugin.RecoverAsync([Info()], claims));
        Assert.Equal(60, replacement.BatteryPercent);
        Assert.False(replacement.IsOnline);
    }

    [Fact]
    public async Task RecoveryRespectsWriteAuthorizationAndDoesNotRehandshakeHealthyDevice()
    {
        using var plugin = new TestPlugin { WriteProbe = true, AllowWrites = true };
        var claims = new DeviceClaimRegistry();
        await plugin.AttachAsync([Info()], claims);
        for (int i = 0; i < 10; i++) await plugin.RecoverAsync([Info()], claims);
        Assert.Single(plugin.Io);
        Assert.Equal(1, plugin.Io[0].Writes);
        plugin.AllowWrites = false;
        plugin.Io[0].FeatureError = 6;
        plugin.Handles[0].GetFeature(0x04);
        Assert.False(Assert.Single(await plugin.RecoverAsync([Info()], claims)).IsOnline);
        Assert.Single(plugin.Io);
        Assert.Equal(1, plugin.Io[0].Writes);
    }

    [Fact]
    public async Task RecoveryDoesNotDiscoverNewOrdinaryWritePluginEndpoints()
    {
        using var plugin = new TestPlugin { WriteProbe = true, AllowWrites = true };
        Assert.Empty(await plugin.RecoverAsync([Info()], new()));
        Assert.Empty(plugin.Io);
    }

    [Fact]
    public async Task BreakerBackoffMustExpireBeforeRecoveryProbeAndTripCountSurvives()
    {
        using var plugin = new TestPlugin { WriteProbe = true, AllowWrites = true };
        var claims = new DeviceClaimRegistry();
        await plugin.AttachAsync([Info()], claims);
        plugin.Poll();
        plugin.PrimeBreaker = true;
        plugin.Poll();
        plugin.Io[0].FeatureError = 6;
        plugin.Handles[0].GetFeature(0x04);
        await plugin.RecoverAsync([Info()], claims);
        plugin.Time.Advance(119);
        await plugin.RecoverAsync([Info()], claims);
        Assert.Single(plugin.Io);
        plugin.Time.Advance(1);
        Assert.True(Assert.Single(await plugin.RecoverAsync([Info()], claims)).IsOnline);
        plugin.Poll();
        plugin.Poll();
        Assert.Equal(3, plugin.LastBreakerTrips);
    }

    [Fact]
    public async Task OneHundredFailuresReleaseEachOldHandleAndKeepOneRecord()
    {
        using var plugin = new TestPlugin();
        var claims = new DeviceClaimRegistry();
        var first = Assert.Single(await plugin.AttachAsync([Info()], claims));
        for (int i = 0; i < 100; i++)
        {
            plugin.Io[^1].FeatureError = 6;
            plugin.Handles[^1].GetFeature(0x04);
            plugin.Time.Advance(30);
            var recovered = Assert.Single(await plugin.RecoverAsync([Info()], claims));
            Assert.Equal(first.Id, recovered.Id);
            Assert.True(recovered.IsOnline);
        }
        Assert.Equal(101, plugin.Io.Count);
        Assert.All(plugin.Io.Take(100), io => Assert.Equal(1, io.Disposals));
        plugin.Detach();
        await plugin.RetirementCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(plugin.Io, io => Assert.Equal(1, io.Disposals));
        Assert.False(claims.IsClaimedBy("same", plugin.Name));
    }

    [Fact]
    public async Task PassiveReadThatFinishesAfterRemovalCannotPublishOnlineAgain()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var plugin = new TestPlugin
        {
            Passive = true,
            ConfigureIo = io => io.Read = buffer => { buffer[0] = 80; started.TrySetResult(); return read.Task; },
        };
        var claims = new DeviceClaimRegistry();
        await plugin.AttachAsync([Info()], claims);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pushed = new List<DeviceStatus>();
        plugin.StatusPushed += pushed.Add;
        Assert.False(Assert.Single(await plugin.AttachAsync([], claims)).IsOnline);
        Assert.False(claims.TryClaim("same", "Other"));
        read.SetResult(1);
        await plugin.RetirementCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(pushed);
        Assert.False(claims.IsClaimedBy("same", plugin.Name));
    }

    [Fact]
    public async Task PassiveTimeoutDoesNotCauseReopenOrDuplicateRead()
    {
        var read = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var plugin = new TestPlugin { InputPolling = true, ConfigureIo = io => io.Read = _ => read.Task };
        var claims = new DeviceClaimRegistry();
        await plugin.AttachAsync([Info()], claims);
        for (int i = 0; i < 10; i++)
        {
            plugin.Poll();
            Assert.True(Assert.Single(await plugin.RecoverAsync([Info()], claims)).IsOnline);
        }
        Assert.Single(plugin.Io);
        Assert.Equal(1, plugin.Io[0].Reads);
        read.SetResult(1);
        await plugin.Handles[0].Drained.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task FailedProbeRetainsClaimWhileItsCancelledWriteStillHasNotExited()
    {
        var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var plugin = new TestPlugin
        {
            FailProbeWithPendingWrite = true,
            ConfigureIo = io => io.Write = _ => write.Task,
        };
        var claims = new DeviceClaimRegistry();
        Assert.Empty(await plugin.AttachAsync([Info()], claims));
        plugin.Time.Advance(60);
        Assert.Empty(await plugin.AttachAsync([Info()], claims));
        Assert.Single(plugin.Io);
        Assert.False(claims.TryClaim("same", "Other"));
        write.SetResult();
        await plugin.RetirementCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(claims.IsClaimedBy("same", plugin.Name));
    }

    [Fact]
    public async Task ShutdownClosesHandleDuringNativeCallAndRejectsLaterAttach()
    {
        using var plugin = new TestPlugin();
        var claims = new DeviceClaimRegistry();
        await plugin.AttachAsync([Info()], claims);
        plugin.Poll();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        plugin.Io[0].Feature = _ =>
        {
            entered.TrySetResult();
            finish.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            return (true, 0);
        };
        var poll = Task.Run(plugin.Poll);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        plugin.BeginShutdown();
        Assert.Equal(1, plugin.Io[0].Disposals);
        Assert.Empty(await plugin.AttachAsync([Info("new")], claims));
        finish.SetResult();
        Assert.Empty(await poll.WaitAsync(TimeSpan.FromSeconds(10)));
        plugin.Detach();
        await plugin.RetirementCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(plugin.Io);
    }

    private sealed class TestPlugin : BatteryPluginBase, IDisposable
    {
        public override string Name => "RecoveryTest";
        public override string DisplayName => "测试";
        public override string DisplayNameEn => "TEST";
        public override DeviceKind Kind => DeviceKind.Gamepad;
        public RecoveryClock Time { get; } = new(DateTimeOffset.UtcNow);
        protected override TimeProvider Clock => Time;
        protected override TimeSpan ProbeCooldown => TimeSpan.Zero;
        protected override OfflinePolicy Offline { get; } = new() { CountPollFailures = false };
        protected override bool ProbeWrites => WriteProbe;
        protected override bool WritesEnabled => AllowWrites;
        public bool WriteProbe { get; set; }
        public bool AllowWrites { get; set; }
        public bool PrimeBreaker { get; set; }
        public bool Passive { get; init; }
        public bool InputPolling { get; init; }
        public bool FailProbeWithPendingWrite { get; init; }
        public Action<FakeHidIo>? ConfigureIo { get; init; }
        protected override bool SupportsPassiveListen => Passive;
        public int LastBreakerTrips { get; private set; }
        public int ProbePercent { get; set; } = 60;
        public List<FakeHidIo> Io { get; } = [];
        public List<HidDeviceHandle> Handles { get; } = [];
        protected override bool IsCandidate(HidDeviceInfo info) => true;
        protected override string BuildDeviceId(HidDeviceInfo info) => $"test:unit-{info.Version}";
        protected override HidDeviceHandle OpenDevice(HidDeviceInfo info)
        {
            var io = new FakeHidIo();
            ConfigureIo?.Invoke(io);
            var handle = new HidDeviceHandle(info, io);
            Io.Add(io);
            Handles.Add(handle);
            return handle;
        }
        protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
        {
            if (FailProbeWithPendingWrite) { handle.WriteOutput([0x04], TimeSpan.Zero, quiet: true); return null; }
            if (WriteProbe && !handle.WriteOutput([0x04], TimeSpan.FromSeconds(1))) return null;
            return ("test", ProbePercent, ChargingState.Discharging);
        }
        protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice dev)
        {
            if (PrimeBreaker)
            {
                PrimeBreaker = false;
                dev.WritesDisabled = true;
                dev.WritesDisabledAt = Clock.GetUtcNow();
                dev.BreakerTrips = 3;
                dev.ConsecutiveFailures = 3;
                return null;
            }
            LastBreakerTrips = dev.BreakerTrips;
            if (InputPolling) { dev.Handle.ReadInput(TimeSpan.Zero); return null; }
            return dev.Handle.GetFeature(0x04) != null ? (60, ChargingState.Discharging) : null;
        }
        protected override (int? Percent, ChargingState Charging, bool Offline)? TryParsePassiveInput(TrackedDevice dev, byte[] input)
            => (input[0], ChargingState.Discharging, false);
        public void Dispose() => Detach();
    }
}
