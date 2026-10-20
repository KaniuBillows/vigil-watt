namespace VigilWatt.Core.Ble;

/// <summary>
/// 功 能： 每端点只保留一份在途连接确认及最新待确认信息。
/// 2026/10/20 20:12:00 CodexSandboxOffline
/// </summary>
public sealed class ConnectionVerificationQueue
{
    public sealed record Request(IReadOnlyDictionary<string, object> Props, string? Name, long Epoch);
    private sealed class Entry
    {
        public required long RunningEpoch { get; set; }
        public Request? Latest { get; set; }
    }

    private readonly object _sync;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public ConnectionVerificationQueue(object syncRoot) => _sync = syncRoot;

    public Request? Enqueue(string id, IReadOnlyDictionary<string, object> props, string? name, long epoch)
    {
        lock (_sync)
        {
            _entries.TryGetValue(id, out var entry);
            var prior = entry?.Latest;
            var merged = prior?.Epoch == epoch ? new Dictionary<string, object>(prior.Props)
                : new Dictionary<string, object>();
            foreach (var pair in props) merged[pair.Key] = pair.Value;
            var request = new Request(merged, name ?? prior?.Name, epoch);
            if (entry != null)
            {
                entry.Latest = request;
                return null;
            }
            _entries[id] = new Entry { RunningEpoch = epoch, Latest = request };
            return request;
        }
    }

    public Request? Latest(string id, long epoch)
    {
        lock (_sync)
            return _entries.TryGetValue(id, out var entry) && entry.Latest?.Epoch == epoch ? entry.Latest : null;
    }

    public Request? Complete(string id, long epoch, Func<long, bool> canStart)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.RunningEpoch != epoch) return null;
            var next = entry.Latest;
            if (next != null && next.Epoch != epoch && canStart(next.Epoch))
            {
                entry.RunningEpoch = next.Epoch;
                return next;
            }
            _entries.Remove(id);
            return null;
        }
    }

    public void Invalidate(string id)
    {
        lock (_sync)
            if (_entries.TryGetValue(id, out var entry)) entry.Latest = null;
    }

    public void Clear()
    {
        lock (_sync)
            foreach (var entry in _entries.Values) entry.Latest = null;
    }
}
