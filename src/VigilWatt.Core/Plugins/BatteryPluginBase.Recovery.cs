using VigilWatt.Core.Hid;
using System.Collections.Concurrent;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins;

/// <summary>
/// 功 能： 为插件提供失效句柄隔离、资源收尾与有界重探。
/// 2026/10/20 20:22:50 CodexSandboxOffline
/// </summary>
public abstract partial class BatteryPluginBase
{
    private sealed record RetiredInterface(Task Completion, TrackedDevice? Previous);
    private readonly Dictionary<string, RetiredInterface> _retired = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrackedDevice> _recoveryCarry = new(StringComparer.OrdinalIgnoreCase);
    private volatile RecoveryAttemptBudget? _recoveryBudget;
    private readonly ConcurrentDictionary<HidDeviceHandle, byte> _liveHandles = new();
    private volatile bool _shutdownRequested;
    protected virtual TimeProvider Clock => TimeProvider.System;
    public event Action? RecoveryNeeded;
    internal Task RetirementCompletion => Task.WhenAll(_retired.Values.Select(item => item.Completion));

    internal void BeginShutdown()
    {
        _shutdownRequested = true;
        foreach (var handle in _liveHandles.Keys) handle.Dispose();
    }

    private static DeviceStatus Snapshot(TrackedDevice dev)
    {
        lock (dev.StateGate) return dev.Status.Clone();
    }

    private void FlagHandleRecovery(TrackedDevice dev)
    {
        lock (dev.StateGate)
        {
            if (_shutdownRequested || dev.Retired || dev.NeedsReprobe) return;
            dev.NeedsReprobe = true;
            _recoveryBudget?.RecordFault(dev.Info.DevicePath);
            MarkOffline(dev, $"句柄不可用（{dev.Handle.LastResult.Status}），等待重探");
            RecoveryNeeded?.Invoke();
        }
    }

    private DeviceStatus Retire(string path, TrackedDevice dev, bool preserveRecovery)
    {
        DeviceStatus snapshot;
        lock (dev.StateGate)
        {
            dev.Retired = true;
            _recoveryBudget?.RecordFault(path);
            dev.Status.IsOnline = false;
            dev.Status.ReadingConnectionConfirmed = false;
            dev.Status.LastUpdated = Clock.GetUtcNow();
            dev.Status.ConnectionObservedAt = dev.Status.LastUpdated;
            snapshot = dev.Status.Clone();
        }
        try { dev.ListenCts?.Cancel(); }
        catch (Exception ex) { Log.Warn($"{DisplayName} 监听取消失败: {ex.Message}"); }
        RetireHandle(path, dev.Handle, dev.ListenTask, dev.ListenCts, preserveRecovery ? dev : null);
        return snapshot;
    }

    private void RetireHandle(string path, HidDeviceHandle handle, Task? listener = null,
        CancellationTokenSource? cancellation = null, TrackedDevice? previous = null)
    {
        handle.Dispose();
        var completion = DrainRetiredAsync(path, handle, listener, cancellation, _claims);
        _retired[path] = new(completion, previous);
    }

    private async Task DrainRetiredAsync(string path, HidDeviceHandle handle, Task? listener,
        CancellationTokenSource? cancellation, DeviceClaimRegistry claims)
    {
        await handle.Drained.ConfigureAwait(false);
        _liveHandles.TryRemove(handle, out _);
        if (listener != null)
        {
            try { await listener.ConfigureAwait(false); }
            catch (OperationCanceledException) { Log.Debug($"{DisplayName} 旧监听已取消"); }
            catch (Exception ex) { Log.Warn($"{DisplayName} 旧监听收尾: {ex.Message}"); }
        }
        cancellation?.Dispose();
        claims.Release(path, Name);
    }

    private void SweepRetired()
    {
        foreach (var (path, retired) in _retired.ToArray())
        {
            if (!retired.Completion.IsCompleted) continue;
            if (retired.Completion.IsFaulted) { Log.Error($"{DisplayName} 旧句柄收尾失败", retired.Completion.Exception); continue; }
            _retired.Remove(path);
            if (retired.Previous is { } previous) _recoveryCarry[path] = previous;
        }
    }

    private bool CanRecover(string path, HidDeviceInfo info)
    {
        if (_retired.Count > 0) return false;
        if (!_recoveryCarry.TryGetValue(path, out var previous)) return true;
        if (ProbeWrites && previous.WritesDisabled &&
            !WriteBackoff.IsExpired(previous.BreakerTrips, previous.WritesDisabledAt, Clock.GetUtcNow())) return false;
        _recoveryBudget ??= new(Clock);
        var interval = ResolveProbeCooldown(info);
        if (ProbeWrites && !SupportsDiscoveryProbe && interval < TimeSpan.FromMinutes(1)) interval = TimeSpan.FromMinutes(1);
        return _recoveryBudget.TryBegin(path, interval);
    }

    public IReadOnlyList<DeviceStatus> CheckReadOnlyHealth()
        => SupportsReadOnlyHealthCheck && !ProbeWrites && !WritesEnabled ? Poll() : Array.Empty<DeviceStatus>();
}
