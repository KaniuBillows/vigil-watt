using VigilWatt.App.Services;
using VigilWatt.Core.Devices;
using VigilWatt.Core.Plugins;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 验证恢复信号筛选、事件合并、有界调度与句柄恢复退避。
/// 2026/10/20 20:32:33 CodexSandboxOffline
/// </summary>
public class RecoverySchedulerTests
{
    [Theory]
    [InlineData(0x0218, 0x12, false, RecoveryReason.SystemResume)]
    [InlineData(0x0218, 0x07, false, RecoveryReason.SystemResume)]
    [InlineData(0x0218, 0x04, false, RecoveryReason.None)]
    [InlineData(0x02B1, 0x07, false, RecoveryReason.None)]
    [InlineData(0x02B1, 0x08, false, RecoveryReason.SessionUnlock)]
    [InlineData(0x0219, 0x8000, true, RecoveryReason.HidChanged)]
    [InlineData(0x0219, 0x8004, true, RecoveryReason.HidChanged)]
    [InlineData(0x0219, 0x8000, false, RecoveryReason.None)]
    [InlineData(0x0219, 0x07, true, RecoveryReason.None)]
    public void LockAndUnrelatedNotificationsNeverTriggerRecovery(int message, long value, bool hid, RecoveryReason reason)
        => Assert.Equal(reason, RecoverySignal.Classify((uint)message, value, hid));

    [Fact]
    public void EventBurstMergesReasonsWithoutPostponingOrMultiplyingStages()
    {
        var time = new RecoveryClock();
        var checks = new List<RecoveryReason>();
        using var scheduler = new RecoveryScheduler(reason => { checks.Add(reason); return Task.CompletedTask; }, time);
        for (int i = 0; i < 100; i++)
        {
            scheduler.Request(RecoveryReason.SystemResume);
            scheduler.Request(RecoveryReason.SessionUnlock);
            scheduler.Request(RecoveryReason.HidChanged);
        }
        time.Advance(0.9);
        Assert.Empty(checks);
        time.Advance(0.1);
        Assert.Single(checks);
        time.Advance(2);
        Assert.Equal(2, checks.Count);
        time.Advance(7);
        time.Advance(20);
        time.Advance(100);
        Assert.Equal(4, checks.Count);
        Assert.All(checks, reason => Assert.Equal(RecoveryReason.SystemResume | RecoveryReason.SessionUnlock | RecoveryReason.HidChanged, reason));
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public async Task SlowPassDoesNotOverlapAndSkipsExpiredCatchUpStages()
    {
        var time = new RecoveryClock();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int checks = 0;
        using var scheduler = new RecoveryScheduler(_ =>
        {
            if (++checks == 1) return blocked.Task;
            finished.TrySetResult();
            return Task.CompletedTask;
        }, time);
        scheduler.Request(RecoveryReason.SystemResume);
        time.Advance(1);
        time.Advance(10);
        scheduler.Request(RecoveryReason.SessionUnlock);
        Assert.Equal(1, checks);
        Assert.Equal(0, time.PendingTimers);
        var scheduled = time.NextTimer;
        blocked.SetResult();
        await scheduled.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(19);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, checks);
        time.Advance(100);
        Assert.Equal(2, checks);
    }

    [Fact]
    public void FailedPassIsLoggedAndLaterStagesStillRun()
    {
        var time = new RecoveryClock();
        int checks = 0;
        using var scheduler = new RecoveryScheduler(_ => ++checks == 1
            ? Task.FromException(new IOException("injected")) : Task.CompletedTask, time);
        scheduler.Request(RecoveryReason.HidFault);
        time.Advance(30);
        Assert.Equal(4, checks);
    }

    [Fact]
    public void DisposeRemovesTimerAndRejectsFurtherSignals()
    {
        var time = new RecoveryClock();
        int checks = 0;
        var scheduler = new RecoveryScheduler(_ => { checks++; return Task.CompletedTask; }, time);
        scheduler.Request(RecoveryReason.SystemResume);
        scheduler.Dispose();
        scheduler.Request(RecoveryReason.SessionUnlock);
        time.Advance(100);
        Assert.Equal(0, checks);
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public void IndependentLaterResumeStartsNewFiniteBurst()
    {
        var time = new RecoveryClock();
        int checks = 0;
        using var scheduler = new RecoveryScheduler(_ => { checks++; return Task.CompletedTask; }, time);
        scheduler.Request(RecoveryReason.SystemResume);
        time.Advance(60);
        scheduler.Request(RecoveryReason.SystemResume);
        time.Advance(30);
        Assert.Equal(8, checks);
    }

    [Fact]
    public void RecoveryBudgetBacksOffAndHonorsLongerProtocolCooldown()
    {
        var time = new RecoveryClock();
        var budget = new RecoveryAttemptBudget(time);
        Assert.True(budget.TryBegin("same", TimeSpan.Zero));
        foreach (int delay in new[] { 1, 3, 10, 30, 30 })
        {
            Assert.False(budget.TryBegin("SAME", TimeSpan.Zero));
            time.Advance(delay);
            Assert.True(budget.TryBegin("same", TimeSpan.Zero));
        }
        Assert.True(budget.TryBegin("other", TimeSpan.FromSeconds(60)));
        time.Advance(30);
        Assert.False(budget.TryBegin("other", TimeSpan.FromSeconds(60)));
        time.Advance(30);
        Assert.True(budget.TryBegin("other", TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void ResumeAfterLongSleepReplacesOldWindowAndRejectsQueuedOldTimer()
    {
        var time = new RecoveryClock();
        int checks = 0;
        using var scheduler = new RecoveryScheduler(_ => { checks++; return Task.CompletedTask; }, time);
        scheduler.Request(RecoveryReason.HidChanged);
        var oldCallback = time.CaptureNextCallback();
        time.Jump(3600);
        scheduler.Request(RecoveryReason.SystemResume);
        oldCallback();
        Assert.Equal(0, checks);
        time.Advance(30);
        Assert.Equal(4, checks);
    }

    [Fact]
    public async Task ResumeDuringLongRunningPassQueuesOnlyOneFreshBurst()
    {
        var time = new RecoveryClock();
        int checks = 0;
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new RecoveryScheduler(_ => ++checks == 1 ? blocked.Task : Task.CompletedTask, time);
        scheduler.Request(RecoveryReason.HidChanged);
        time.Advance(60);
        for (int i = 0; i < 100; i++) scheduler.Request(RecoveryReason.SystemResume);
        Assert.Equal(1, checks);
        var scheduled = time.NextTimer;
        blocked.SetResult();
        await scheduled.WaitAsync(TimeSpan.FromSeconds(10));
        time.Advance(30);
        Assert.Equal(5, checks);
        Assert.Equal(0, time.PendingTimers);
    }

    [Fact]
    public void FaultWaitingForDrainDoesNotCountAsHealthyTime()
    {
        var time = new RecoveryClock();
        var budget = new RecoveryAttemptBudget(time);
        budget.TryBegin("id", TimeSpan.Zero);
        budget.MarkHealthy("id");
        time.Advance(1);
        budget.RecordFault("id");
        time.Advance(120);
        Assert.True(budget.TryBegin("id", TimeSpan.Zero));
        time.Advance(1);
        Assert.False(budget.TryBegin("id", TimeSpan.Zero));
    }

    [Fact]
    public void ConfirmedHealthyWindowResetsBackoffAndRemovedPathsCanBePruned()
    {
        var time = new RecoveryClock();
        var budget = new RecoveryAttemptBudget(time);
        budget.TryBegin("id", TimeSpan.Zero);
        time.Advance(1);
        budget.TryBegin("id", TimeSpan.Zero);
        budget.MarkHealthy("id");
        time.Advance(120);
        budget.RecordFault("id");
        Assert.True(budget.TryBegin("id", TimeSpan.Zero));
        time.Advance(1);
        Assert.True(budget.TryBegin("id", TimeSpan.Zero));
        budget.Prune(new HashSet<string>());
        Assert.True(budget.TryBegin("id", TimeSpan.Zero));
    }

}
