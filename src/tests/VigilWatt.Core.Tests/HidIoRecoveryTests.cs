using System.ComponentModel;
using VigilWatt.Core.Hid;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 验证 HID 错误分类、超时复用和关闭后的在途操作隔离。
/// 2026/10/20 20:31:04 CodexSandboxOffline
/// </summary>
public class HidIoRecoveryTests
{
    private static HidDeviceInfo Info(ushort input = 8) => new() { DevicePath = "fake", InputReportLength = input, FeatureReportLength = 8 };

    [Theory]
    [InlineData(6, HidIoStatus.InvalidHandle, true)]
    [InlineData(1167, HidIoStatus.Disconnected, true)]
    [InlineData(5, HidIoStatus.AccessConflict, false)]
    [InlineData(32, HidIoStatus.AccessConflict, false)]
    [InlineData(87, HidIoStatus.ProtocolError, false)]
    [InlineData(1, HidIoStatus.ProtocolError, false)]
    [InlineData(995, HidIoStatus.Cancelled, false)]
    [InlineData(121, HidIoStatus.Timeout, false)]
    [InlineData(31, HidIoStatus.IoError, false)]
    public async Task OnlyExplicitTransportFailuresInvalidateHandle(int error, HidIoStatus expected, bool reopen)
    {
        var io = new FakeHidIo { Read = _ => Task.FromException<int>(new Win32Exception(error)) };
        using var handle = new HidDeviceHandle(Info(), io);
        var result = await handle.ReadInputResultAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(expected, result.Result.Status);
        Assert.Equal(error, result.Result.Error);
        Assert.Equal(reopen, handle.NeedsRecovery);
    }

    [Fact]
    public async Task OrdinaryTimeoutKeepsOnePendingReadAndLaterReturnsItsPacket()
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new FakeHidIo { Read = buffer => { buffer[0] = 70; return completion.Task; } };
        using var handle = new HidDeviceHandle(Info(), io);
        for (int i = 0; i < 100; i++)
            Assert.Equal(HidIoStatus.Timeout, (await handle.ReadInputResultAsync(TimeSpan.Zero)).Result.Status);
        Assert.Equal(1, io.Reads);
        Assert.False(handle.NeedsRecovery);
        Assert.False(handle.Drained.IsCompleted);
        completion.SetResult(1);
        var packet = await handle.ReadInputResultAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new byte[] { 70 }, packet.Data);
        Assert.True(handle.Drained.IsCompleted);
    }

    [Fact]
    public async Task LateReadFailureIsObservedAndLatchedEvenAfterWaitTimedOut()
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new FakeHidIo { Read = _ => completion.Task };
        using var handle = new HidDeviceHandle(Info(), io);
        await handle.ReadInputResultAsync(TimeSpan.Zero);
        completion.SetException(new Win32Exception(1167));
        await handle.Drained.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(handle.NeedsRecovery);
        Assert.Null(handle.GetFeature(0x04));
        Assert.Equal(0, io.Features);
        Assert.Equal(HidIoStatus.Disconnected, handle.LastResult.Status);
    }

    [Fact]
    public async Task TimedOutWriteCannotBeDuplicatedAndCloseWaitsForNativeCompletion()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new FakeHidIo { Write = _ => completion.Task };
        var handle = new HidDeviceHandle(Info(), io);
        Assert.False(handle.WriteOutput([0x04], TimeSpan.Zero, quiet: true));
        Assert.False(handle.WriteOutput([0x05], TimeSpan.Zero, quiet: true));
        Assert.Equal(1, io.Writes);
        handle.Dispose();
        Assert.False(handle.Drained.IsCompleted);
        Assert.False(handle.WriteOutput([0x06], TimeSpan.Zero));
        completion.SetResult();
        await handle.Drained.WaitAsync(TimeSpan.FromSeconds(10));
        handle.Dispose();
        Assert.Equal(1, io.Disposals);
        Assert.Equal(1, io.Writes);
    }

    [Fact]
    public void FeatureFailureCapturesActualErrorAndDoesNotTreatProtocolMismatchAsBrokenHandle()
    {
        var io = new FakeHidIo { FeatureError = 87 };
        using var handle = new HidDeviceHandle(Info(), io);
        Assert.Null(handle.GetFeature(0x04, out int error));
        Assert.Equal(87, error);
        Assert.Equal(HidIoStatus.ProtocolError, handle.LastResult.Status);
        Assert.False(handle.NeedsRecovery);
        io.FeatureError = 0;
        Assert.NotNull(handle.GetFeature(0x04));
        Assert.Equal(HidIoStatus.Success, handle.LastResult.Status);
    }

    [Fact]
    public async Task ZeroLengthInputIsUnsupportedWithoutStartingIo()
    {
        var io = new FakeHidIo();
        using var handle = new HidDeviceHandle(Info(0), io);
        Assert.Equal(HidIoStatus.Unsupported, (await handle.ReadInputResultAsync(TimeSpan.Zero)).Result.Status);
        Assert.False(handle.NeedsRecovery);
        Assert.Equal(0, io.Reads);
    }

    [Fact]
    public async Task EndOfStreamMeansLostHidEndpoint()
    {
        using var handle = new HidDeviceHandle(Info(), new FakeHidIo { Read = _ => Task.FromResult(0) });
        Assert.Equal(HidIoStatus.Disconnected, (await handle.ReadInputResultAsync(TimeSpan.FromSeconds(1))).Result.Status);
        Assert.True(handle.NeedsRecovery);
    }

    [Fact]
    public async Task NativeFeatureCallMustFinishBeforeDisposedHandleIsDrained()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var io = new FakeHidIo { Feature = _ =>
        {
            entered.TrySetResult();
            finish.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            return (true, 0);
        } };
        var handle = new HidDeviceHandle(Info(), io);
        var feature = Task.Run(() => handle.GetFeature(0x04));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        handle.Dispose();
        Assert.False(handle.Drained.IsCompleted);
        finish.TrySetResult();
        Assert.Null(await feature.WaitAsync(TimeSpan.FromSeconds(10)));
        await handle.Drained.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task FailedCloseKeepsHandleQuarantined()
    {
        var io = new FakeHidIo { Close = () => throw new IOException("injected close failure") };
        var handle = new HidDeviceHandle(Info(), io);
        handle.Dispose();
        Assert.False(handle.Drained.IsCompleted);
        Assert.Equal(HidIoStatus.Disposed, (await handle.ReadInputResultAsync(TimeSpan.Zero)).Result.Status);
    }
}
