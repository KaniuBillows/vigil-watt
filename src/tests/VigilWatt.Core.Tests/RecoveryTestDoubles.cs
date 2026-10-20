using VigilWatt.Core.Hid;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 为恢复回归提供不访问硬件的 I/O 和可推进时钟。
/// 2026/10/20 20:31:02 CodexSandboxOffline
/// </summary>
internal sealed class FakeHidIo : IHidIo
{
    public int Reads, Writes, Features, Disposals;
    public int FeatureError { get; set; }
    public Func<byte[], Task<int>>? Read { get; set; }
    public Func<byte[], Task>? Write { get; set; }
    public Func<byte[], (bool Success, int Error)>? Feature { get; set; }
    public Action? Close { get; set; }
    public Task<int> ReadAsync(byte[] buffer)
    {
        Interlocked.Increment(ref Reads);
        if (Read != null) return Read(buffer);
        buffer[0] = 60;
        return Task.FromResult(1);
    }
    public Task WriteAsync(byte[] buffer) { Interlocked.Increment(ref Writes); return Write?.Invoke(buffer) ?? Task.CompletedTask; }
    public bool GetFeature(byte[] buffer, out int error)
    {
        Features++;
        var result = Feature?.Invoke(buffer) ?? (FeatureError == 0, FeatureError);
        error = result.Item2;
        return result.Item1;
    }
    public bool SetFeature(byte[] buffer, out int error) { Writes++; error = FeatureError; return error == 0; }
    public byte? GetOutputReportId(out int error) { error = 0; return 0x04; }
    public void Dispose() { Disposals++; Close?.Invoke(); }
}

internal sealed class RecoveryClock : TimeProvider
{
    private readonly object _sync = new();
    private DateTimeOffset _now;
    private readonly List<ManualTimer> _timers = [];
    private TaskCompletionSource _nextTimer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public RecoveryClock(DateTimeOffset? initial = null) => _now = initial ?? DateTimeOffset.UnixEpoch;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_sync) return _now.UtcTicks; }
    public override DateTimeOffset GetUtcNow() { lock (_sync) return _now; }
    public int PendingTimers { get { lock (_sync) return _timers.Count(t => t.Due.HasValue); } }
    public Task NextTimer { get { lock (_sync) return _nextTimer.Task; } }
    public void Jump(double seconds) { lock (_sync) _now = _now.AddSeconds(seconds); }
    public Action CaptureNextCallback()
    {
        lock (_sync) return _timers.First(timer => timer.Due.HasValue).Fire;
    }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_sync)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            _nextTimer.TrySetResult();
            _nextTimer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return timer;
        }
    }
    public void Advance(double seconds)
    {
        DateTimeOffset target;
        lock (_sync) target = _now.AddSeconds(seconds);
        while (true)
        {
            ManualTimer? next;
            lock (_sync)
            {
                next = _timers.Where(t => t.Due.HasValue && t.Due.Value <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next == null) { _now = target; return; }
                _now = next.Due!.Value;
                next.Due = null;
            }
            next.Fire();
        }
    }
    private sealed class ManualTimer : ITimer
    {
        private readonly RecoveryClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        public DateTimeOffset? Due { get; set; }
        public ManualTimer(RecoveryClock clock, TimerCallback callback, object? state)
        { _clock = clock; _callback = callback; _state = state; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        { lock (_clock._sync) Due = dueTime == Timeout.InfiniteTimeSpan ? null : _clock._now + dueTime; return true; }
        public void Fire() => _callback(_state);
        public void Dispose() { lock (_clock._sync) Due = null; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
