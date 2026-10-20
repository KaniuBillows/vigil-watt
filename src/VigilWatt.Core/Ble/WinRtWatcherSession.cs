using Windows.Devices.Enumeration;

namespace VigilWatt.Core.Ble;

/// <summary>
/// 功 能： 将 WinRT 监视器事件适配到可替换的恢复会话。
/// 2026/10/20 20:03:00 CodexSandboxOffline
/// </summary>
internal sealed class WinRtWatcherSession : IWatcherSession
{
    private readonly DeviceWatcher _watcher;
    private readonly Action<IWatcherSession, string, IReadOnlyDictionary<string, object>, string?> _updated;
    private readonly Action<IWatcherSession, string> _removed;

    public event Action<IWatcherSession>? Stopped;
    public bool IsTerminal => _watcher.Status is DeviceWatcherStatus.Created
        or DeviceWatcherStatus.Stopped or DeviceWatcherStatus.Aborted;

    public WinRtWatcherSession(DeviceWatcher watcher,
        Action<IWatcherSession, string, IReadOnlyDictionary<string, object>, string?> updated,
        Action<IWatcherSession, string> removed)
    {
        _watcher = watcher;
        _updated = updated;
        _removed = removed;
        watcher.Added += OnAdded;
        watcher.Updated += OnUpdated;
        watcher.Removed += OnRemoved;
        watcher.Stopped += OnStopped;
    }

    private void OnAdded(DeviceWatcher sender, DeviceInformation info) => _updated(this, info.Id, info.Properties, info.Name);
    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate info) => _updated(this, info.Id, info.Properties, null);
    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate info) => _removed(this, info.Id);
    private void OnStopped(DeviceWatcher sender, object args) => Stopped?.Invoke(this);
    public void Start() => _watcher.Start();
    public void Stop() => _watcher.Stop();

    public void Dispose()
    {
        _watcher.Added -= OnAdded;
        _watcher.Updated -= OnUpdated;
        _watcher.Removed -= OnRemoved;
        _watcher.Stopped -= OnStopped;
    }
}
