using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Devices;

[Flags]
public enum RecoveryReason { None = 0, SystemResume = 1, SessionUnlock = 2, HidChanged = 4, HidFault = 8, NotificationRegistration = 16 }

/// <summary>
/// 功 能： 合并恢复信号，按有限阶段串行执行健康检查并跳过过时阶段。
/// 2026/10/20 20:27:06 CodexSandboxOffline
/// </summary>
public sealed class RecoveryScheduler : IDisposable
{
    private static readonly TimeSpan[] Stages = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
    private readonly object _sync = new();
    private readonly Func<RecoveryReason, Task> _check;
    private readonly TimeProvider _time;
    private ITimer? _timer;
    private RecoveryReason _reasons;
    private long _startedAt;
    private long _generation;
    private int _stage, _failures;
    private bool _active, _disposed, _running, _restartRequested;

    public RecoveryScheduler(Func<RecoveryReason, Task> check, TimeProvider? timeProvider = null)
    { _check = check; _time = timeProvider ?? TimeProvider.System; }

    public void Request(RecoveryReason reason)
    {
        if (reason == RecoveryReason.None) return;
        lock (_sync)
        {
            if (_disposed) return;
            _reasons |= reason;
            if (_active)
            {
                if (_time.GetElapsedTime(_startedAt) < Stages[^1]) return;
                if (_running) { _restartRequested = true; return; }
                _timer?.Dispose();
                _timer = null;
            }
            _active = true;
            _generation++;
            _startedAt = _time.GetTimestamp();
            _stage = 0;
            Schedule();
        }
    }

    private void Schedule()
    {
        if (_disposed) return;
        var elapsed = _time.GetElapsedTime(_startedAt);
        while (_stage < Stages.Length && Stages[_stage] <= elapsed) _stage++;
        if (_stage == Stages.Length) { _active = false; _reasons = RecoveryReason.None; return; }
        long generation = _generation;
        int stage = _stage;
        _timer = _time.CreateTimer(_ => _ = RunAsync(generation, stage), null, Stages[_stage] - elapsed, Timeout.InfiniteTimeSpan);
    }

    private async Task RunAsync(long generation, int expectedStage)
    {
        RecoveryReason reasons;
        int stage;
        lock (_sync)
        {
            if (_disposed || _running || generation != _generation || expectedStage != _stage) return;
            _timer?.Dispose();
            _timer = null;
            _running = true;
            reasons = _reasons;
            stage = ++_stage;
        }
        try
        {
            Log.Info($"恢复健康检查 {stage}/{Stages.Length}: {reasons}");
            await _check(reasons).ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Error($"恢复健康检查失败 #{Interlocked.Increment(ref _failures)}", ex); }
        finally
        {
            lock (_sync)
            {
                _running = false;
                if (_restartRequested && !_disposed)
                {
                    _restartRequested = false;
                    _active = false;
                    Request(_reasons);
                }
                else Schedule();
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
