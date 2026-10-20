using VigilWatt.Core.Ble;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 故障注入验证监视器重建、停止屏障、退避与退出时的事件释放。
/// 2026/10/20 20:04:52 CodexSandboxOffline
/// </summary>
public class WatcherRecoveryTests
{
    [Fact]
    public void FailedCreationRetriesWithoutAnotherStartCall()
    {
        var clock = new ManualClock();
        int attempts = 0;
        var session = new FakeSession();
        using var recovery = new WatcherRecovery("test", () =>
        {
            if (++attempts < 3) throw new IOException("injected creation failure");
            return session;
        }, () => { }, clock);
        recovery.Start();
        Assert.False(recovery.IsRunning);
        clock.Advance(1);
        Assert.Equal(2, attempts);
        clock.Advance(2);
        Assert.Equal(2, attempts);
        clock.Advance(1);
        Assert.Equal(3, attempts);
        Assert.True(recovery.IsRunning);
        Assert.Equal(0, clock.PendingTimers);
    }

    [Fact]
    public void FailedStartWaitsForOldSessionToFinishStopping()
    {
        var clock = new ManualClock();
        var old = new FakeSession { FailStart = true, AutoStop = false };
        var replacement = new FakeSession();
        int attempts = 0;
        using var recovery = new WatcherRecovery("test", () => ++attempts == 1 ? old : replacement, () => { }, clock);
        recovery.Start();
        Assert.Equal(1, old.StopCount);
        clock.Advance(1);
        clock.Advance(3);
        Assert.Equal(1, attempts);
        Assert.Equal(1, old.StopCount);
        Assert.False(old.Disposed);
        old.CompleteStop();
        Assert.True(old.Disposed);
        clock.Advance(10);
        Assert.Equal(2, attempts);
        Assert.True(recovery.IsRunning);
        Assert.Equal(0, old.SubscriberCount);
    }

    [Fact]
    public void RepeatedAbortsBackOffAndDoNotAccumulateSubscriptions()
    {
        var clock = new ManualClock();
        var created = new List<FakeSession>();
        int invalidations = 0;
        using var recovery = new WatcherRecovery("test", () =>
        {
            var session = new FakeSession();
            created.Add(session);
            return session;
        }, () => invalidations++, clock);
        recovery.Start();
        for (int i = 0; i < 100; i++)
        {
            var old = created[^1];
            old.CompleteStop();
            Assert.False(recovery.IsRunning);
            int delay = i switch { 0 => 1, 1 => 3, 2 => 10, _ => 30 };
            clock.Advance(delay - 0.1);
            Assert.Equal(i + 1, created.Count);
            clock.Advance(0.1);
            Assert.True(recovery.IsRunning);
            Assert.True(old.Disposed);
            Assert.Equal(0, old.SubscriberCount);
            Assert.Single(created, s => !s.Disposed);
        }
        Assert.Equal(100, invalidations);
    }

    [Fact]
    public void DisposeCancelsPendingRetryAndPreventsRestart()
    {
        var clock = new ManualClock();
        int attempts = 0;
        var recovery = new WatcherRecovery("test", () =>
        {
            attempts++;
            throw new IOException("injected");
        }, () => { }, clock);
        recovery.Start();
        recovery.Dispose();
        recovery.Start();
        clock.Advance(1000);
        Assert.Equal(1, attempts);
        Assert.False(recovery.IsRunning);
        Assert.Equal(0, clock.PendingTimers);
    }

    [Fact]
    public void DisposeRejectsCallbacksBeforeAsynchronousStopCompletes()
    {
        var clock = new ManualClock();
        var session = new FakeSession { AutoStop = false };
        int callbacks = 0, attempts = 0;
        var recovery = new WatcherRecovery("test", () => { attempts++; return session; }, () => { }, clock);
        recovery.Start();
        recovery.RunIfCurrent(session, () => callbacks++);
        recovery.Dispose();
        recovery.RunIfCurrent(session, () => callbacks++);
        Assert.Equal(1, callbacks);
        session.CompleteStop();
        clock.Advance(1000);
        Assert.Equal(1, attempts);
        Assert.True(session.Disposed);
        Assert.Equal(0, session.SubscriberCount);
        Assert.Equal(0, clock.PendingTimers);
    }

    [Fact]
    public void SynchronousStoppedDuringStartCannotMarkSessionRunning()
    {
        var clock = new ManualClock();
        var session = new FakeSession { StopDuringStart = true };
        using var recovery = new WatcherRecovery("test", () => session, () => { }, clock);
        recovery.Start();
        Assert.False(recovery.IsRunning);
        Assert.True(session.Disposed);
        Assert.Equal(1, clock.PendingTimers);
    }

    [Fact]
    public void OldSessionCannotPublishIntoReplacementAndRepeatedStartIsIdempotent()
    {
        var clock = new ManualClock();
        var old = new FakeSession();
        var current = new FakeSession();
        int attempts = 0, callbacks = 0;
        using var recovery = new WatcherRecovery("test", () => ++attempts == 1 ? old : current, () => { }, clock);
        Parallel.For(0, 100, _ => recovery.Start());
        Assert.Equal(1, attempts);
        old.CompleteStop();
        clock.Advance(1);
        recovery.RunIfCurrent(old, () => callbacks++);
        recovery.RunIfCurrent(current, () => callbacks++);
        Assert.Equal(1, callbacks);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void HealthyRunResetsBackoffAfterRepeatedFailure()
    {
        var clock = new ManualClock();
        var created = new List<FakeSession>();
        using var recovery = new WatcherRecovery("test", () =>
        {
            var session = new FakeSession();
            created.Add(session);
            return session;
        }, () => { }, clock);
        recovery.Start();
        created[^1].CompleteStop();
        clock.Advance(1);
        created[^1].CompleteStop();
        clock.Advance(3);
        clock.Advance(120);
        created[^1].CompleteStop();
        clock.Advance(1);
        Assert.Equal(4, created.Count);
    }

    [Fact]
    public void SessionReplacementInvalidatesPendingReadAndPreservesNewConnection()
    {
        var clock = new ManualClock();
        var old = new FakeSession();
        var current = new FakeSession();
        ConnectionEpochs? epochs = null;
        int attempts = 0, published = 0;
        using var recovery = new WatcherRecovery("test", () => ++attempts == 1 ? old : current,
            () => epochs!.Clear(), clock);
        epochs = new ConnectionEpochs(recovery.SyncRoot);
        recovery.Start();
        long token = 0;
        recovery.RunIfCurrent(old, () => token = epochs.Observe("device", true));
        old.CompleteStop();
        clock.Advance(1);
        long fresh = 0;
        recovery.RunIfCurrent(current, () => fresh = epochs.Observe("device", true));
        Assert.False(epochs.TryCommit("device", token, () => published--));
        Assert.True(epochs.TryCommit("device", fresh, () => published++));
        recovery.Dispose();
        Assert.False(epochs.TryCommit("device", fresh, () => published--));
        Assert.Equal(1, published);
    }

    [Fact]
    public void CallbackFailureDoesNotDisableSubsequentEvents()
    {
        var session = new FakeSession();
        using var recovery = new WatcherRecovery("test", () => session, () => { }, new ManualClock());
        recovery.Start();
        recovery.RunIfCurrent(session, () => throw new IOException("injected callback failure"));
        bool received = false;
        recovery.RunIfCurrent(session, () => received = true);
        Assert.True(received);
        Assert.True(recovery.IsRunning);
    }

    private sealed class FakeSession : IWatcherSession
    {
        public event Action<IWatcherSession>? Stopped;
        public bool IsTerminal { get; private set; } = true;
        public bool FailStart { get; init; }
        public bool StopDuringStart { get; init; }
        public bool AutoStop { get; init; } = true;
        public bool Disposed { get; private set; }
        public int StopCount { get; private set; }
        public int SubscriberCount => Stopped?.GetInvocationList().Length ?? 0;
        public void Start()
        {
            IsTerminal = false;
            if (FailStart) throw new IOException("injected start failure");
            if (StopDuringStart) CompleteStop();
        }
        public void Stop() { StopCount++; if (AutoStop) CompleteStop(); }
        public void CompleteStop() { IsTerminal = true; Stopped?.Invoke(this); }
        public void Dispose() => Disposed = true;
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private readonly List<ManualTimer> _timers = new();
        public int PendingTimers => _timers.Count(t => t.Due.HasValue);
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(double seconds)
        {
            var target = _now.AddSeconds(seconds);
            while (_timers.Where(t => t.Due.HasValue && t.Due.Value <= target).OrderBy(t => t.Due).FirstOrDefault() is { } next)
            {
                _now = next.Due!.Value;
                next.Fire();
            }
            _now = target;
        }
        private sealed class ManualTimer : ITimer
        {
            private readonly ManualClock _clock;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            public DateTimeOffset? Due { get; private set; }
            public ManualTimer(ManualClock clock, TimerCallback callback, object? state)
            { _clock = clock; _callback = callback; _state = state; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { Due = dueTime == Timeout.InfiniteTimeSpan ? null : _clock._now + dueTime; return true; }
            public void Fire() { Due = null; _callback(_state); }
            public void Dispose() => Due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
