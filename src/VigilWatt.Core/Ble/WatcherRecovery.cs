using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Ble;

public interface IWatcherSession : IDisposable
{
    event Action<IWatcherSession>? Stopped;
    bool IsTerminal { get; }
    void Start();
    void Stop();
}

/// <summary>
/// 功 能： 串行管理蓝牙监视器的启动、停止与退避重建。
/// 2026/10/20 20:03:00 CodexSandboxOffline
/// </summary>
public sealed class WatcherRecovery : IDisposable
{
    private readonly string _name;
    private readonly Func<IWatcherSession> _create;
    private readonly Action _invalidate;
    private readonly TimeProvider _time;
    private IWatcherSession? _session;
    private ITimer? _retry;
    private int _failures, _callbackFailures;
    private bool _requested, _disposed, _running, _stopRequested;
    private DateTimeOffset _startedAt;

    public object SyncRoot { get; } = new();
    public bool IsRunning { get { lock (SyncRoot) return _running && !_disposed; } }

    public WatcherRecovery(string name, Func<IWatcherSession> create, Action invalidate,
        TimeProvider? timeProvider = null)
    {
        _name = name;
        _create = create;
        _invalidate = invalidate;
        _time = timeProvider ?? TimeProvider.System;
    }

    public void Start()
    {
        lock (SyncRoot)
        {
            if (_disposed || _requested) return;
            _requested = true;
            TryStart();
        }
    }

    public void RunIfCurrent(IWatcherSession session, Action action)
    {
        lock (SyncRoot)
        {
            if (_disposed || _stopRequested || !ReferenceEquals(session, _session)) return;
            try { action(); }
            catch (Exception ex) { Log.Error($"{_name} watcher 回调失败 #{++_callbackFailures}", ex); }
        }
    }

    private void TryStart()
    {
        if (_disposed || !_requested || _running) return;
        if (_session != null)
        {
            StopOrRelease();
            return;
        }

        try
        {
            var session = _create();
            _session = session;
            _stopRequested = false;
            session.Stopped += OnStopped;
            session.Start();
            if (!ReferenceEquals(_session, session)) return;
            if (session.IsTerminal) throw new InvalidOperationException("监视器启动后未进入运行态");
            _running = true;
            _startedAt = _time.GetUtcNow();
            Log.Info($"{_name} watcher 已启动");
        }
        catch (Exception ex)
        {
            Log.Error($"{_name} watcher 启动失败", ex);
            _running = false;
            _invalidate();
            StopOrRelease();
        }
    }

    private void StopOrRelease()
    {
        bool terminal = _session == null;
        if (_session != null)
        {
            try { terminal = _session.IsTerminal; }
            catch (Exception ex) { Log.Error($"{_name} watcher 状态读取失败", ex); }
        }
        if (terminal)
        {
            Release();
            ScheduleRetry();
            return;
        }

        // Stop 返回时仍可能有回调；收到 Stopped 前不允许新旧监视器重叠。
        if (!_stopRequested)
        {
            _stopRequested = true;
            try { _session!.Stop(); }
            catch (Exception ex) { Log.Error($"{_name} watcher 停止失败", ex); }
        }
        if (_session != null) ScheduleRetry();
    }

    private void OnStopped(IWatcherSession session)
    {
        lock (SyncRoot)
        {
            if (!ReferenceEquals(session, _session)) return;
            if (_running && _time.GetUtcNow() - _startedAt >= TimeSpan.FromMinutes(2)) _failures = 0;
            _running = false;
            _invalidate();
            Release();
            ScheduleRetry();
        }
    }

    private void Release()
    {
        if (_session == null) return;
        var session = _session;
        _session = null;
        session.Stopped -= OnStopped;
        try { session.Dispose(); }
        catch (Exception ex) { Log.Error($"{_name} watcher 解绑失败", ex); }
        _stopRequested = false;
    }

    private void ScheduleRetry()
    {
        if (_disposed || !_requested || _retry != null) return;
        _failures = Math.Min(_failures, int.MaxValue - 1) + 1;
        int seconds = _failures switch { 1 => 1, 2 => 3, 3 => 10, _ => 30 };
        Log.Warn($"{_name} watcher 恢复 #{_failures}，{seconds} 秒后重试");
        _retry = _time.CreateTimer(_ =>
        {
            lock (SyncRoot)
            {
                _retry?.Dispose();
                _retry = null;
                TryStart();
            }
        }, null, TimeSpan.FromSeconds(seconds), Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        lock (SyncRoot)
        {
            if (_disposed) return;
            _disposed = true;
            _requested = false;
            _running = false;
            _retry?.Dispose();
            _retry = null;
            _invalidate();
            StopOrRelease();
        }
    }
}
