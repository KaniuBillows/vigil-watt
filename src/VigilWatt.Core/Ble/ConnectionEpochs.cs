namespace VigilWatt.Core.Ble;

/// <summary>
/// 功 能： 用连接代次阻止移除、断连及监视器重建前的异步结果回写。
/// 2026/10/20 20:03:00 CodexSandboxOffline
/// </summary>
public sealed class ConnectionEpochs
{
    private readonly object _sync;
    private readonly Dictionary<string, (long Epoch, bool? Connected)> _entries = new(StringComparer.OrdinalIgnoreCase);
    private long _next;

    public ConnectionEpochs(object syncRoot) => _sync = syncRoot;

    public long Observe(string id, bool? connected)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(id, out var entry)) entry = (++_next, connected);
            else if (connected.HasValue && connected != entry.Connected) entry = (++_next, connected);
            _entries[id] = entry;
            return entry.Epoch;
        }
    }

    public bool IsCurrent(string id, long epoch)
    {
        lock (_sync)
            return _entries.TryGetValue(id, out var entry) && entry.Epoch == epoch && entry.Connected != false;
    }

    public bool TryCommit(string id, long epoch, Action commit)
    {
        lock (_sync)
        {
            if (!IsCurrent(id, epoch)) return false;
            commit();
            return true;
        }
    }

    public void Remove(string id) { lock (_sync) _entries.Remove(id); }
    public void Clear() { lock (_sync) _entries.Clear(); }
}
