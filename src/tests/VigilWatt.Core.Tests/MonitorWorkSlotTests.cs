using VigilWatt.Core.Devices;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>覆盖阻塞来源隔离、限时观察不重入，以及停止后等待原任务收尾。</summary>
public sealed class MonitorWorkSlotTests
{
    [Fact]
    public async Task BlockedOwnerDoesNotHoldHealthyPublicationOrCreateReplacement()
    {
        var blocked = new MonitorWorkSlot();
        var healthy = new MonitorWorkSlot();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0, published = 0;
        var pending = blocked.Run(async () => { Interlocked.Increment(ref calls); entered.SetResult(); await release.Task; });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var completed = healthy.Run(() => { Interlocked.Increment(ref published); return Task.CompletedTask; });
            await completed.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(await MonitorWorkSlot.ObserveAsync([pending, completed], TimeSpan.FromMilliseconds(10)));
            for (int i = 0; i < 100; i++)
                Assert.Same(pending, blocked.Run(() => throw new InvalidOperationException("duplicate owner")));
            Assert.Equal(1, calls);
            Assert.Equal(1, published);
        }
        finally { release.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
        await blocked.Run(() => { calls++; return Task.CompletedTask; });
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task StopKeepsOldTaskUntilItActuallyDrains()
    {
        var slot = new MonitorWorkSlot();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = slot.Run(() => release.Task);
        var drain = slot.Stop();
        Assert.Same(pending, drain);
        Assert.False(drain.IsCompleted);
        await slot.Run(() => throw new InvalidOperationException("stopped"));
        release.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(10));
        await slot.Run(() => throw new InvalidOperationException("cannot restart stopped owner"));
    }

    [Fact]
    public async Task CompletedFailureDoesNotPermanentlyBlockNextAttempt()
    {
        var slot = new MonitorWorkSlot();
        await Assert.ThrowsAsync<IOException>(() => slot.Run(() => throw new IOException("injected")));
        int published = 0;
        Assert.True(await MonitorWorkSlot.ObserveAsync(
            [slot.Run(() => { published++; return Task.CompletedTask; })], TimeSpan.FromSeconds(10)));
        Assert.Equal(1, published);
    }
}
