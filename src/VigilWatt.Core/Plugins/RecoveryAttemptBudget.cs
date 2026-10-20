namespace VigilWatt.Core.Plugins;

/// <summary>
/// 功 能： 限制同一路径的句柄重开频率，健康运行后才重置恢复退避。
/// 2026/10/20 20:22:49 CodexSandboxOffline
/// </summary>
public sealed class RecoveryAttemptBudget
{
    private sealed record Attempt(DateTimeOffset At, int Count, DateTimeOffset? HealthySince);
    private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    public RecoveryAttemptBudget(TimeProvider time) => _time = time;

    public bool TryBegin(string path, TimeSpan minimumInterval)
    {
        lock (_sync) return TryBeginCore(path, minimumInterval);
    }

    private bool TryBeginCore(string path, TimeSpan minimumInterval)
    {
        var now = _time.GetUtcNow();
        int count = 0;
        if (_attempts.TryGetValue(path, out var prior))
        {
            count = prior.Count;
            int seconds = count switch { 1 => 1, 2 => 3, 3 => 10, _ => 30 };
            var interval = TimeSpan.FromSeconds(seconds);
            if (interval < minimumInterval) interval = minimumInterval;
            if (now - prior.At < interval) return false;
        }
        _attempts[path] = new(now, Math.Min(count + 1, 4), null);
        return true;
    }

    public void MarkHealthy(string path)
    {
        lock (_sync)
            if (_attempts.TryGetValue(path, out var prior) && prior.HealthySince == null)
                _attempts[path] = prior with { HealthySince = _time.GetUtcNow() };
    }

    public void RecordFault(string path)
    {
        lock (_sync)
        {
            if (!_attempts.TryGetValue(path, out var prior) || prior.HealthySince == null) return;
            bool healthy = _time.GetUtcNow() - prior.HealthySince.Value >= TimeSpan.FromMinutes(2);
            _attempts[path] = prior with { HealthySince = null, Count = healthy ? 0 : prior.Count };
        }
    }

    public void Prune(IReadOnlySet<string> presentPaths)
    {
        lock (_sync)
            foreach (var path in _attempts.Keys.Where(path => !presentPaths.Contains(path)).ToArray()) _attempts.Remove(path);
    }
}
