using VigilWatt.Core.Models;

namespace VigilWatt.Core.Devices;

/// <summary>
/// 功 能： 按成功读数时间去重采样，写入失败时保留重试机会。
/// 2026/10/20 21:05:16 CodexSandboxOffline
/// </summary>
public sealed class ReadingSampleRecorder
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _recorded = new();

    public bool Record(DeviceStatus status, DateTimeOffset now, Action<DeviceStatus> insert)
    {
        lock (_sync)
        {
            DateTimeOffset? previous = _recorded.TryGetValue(status.Id, out var at) ? at : null;
            if (!ReadingFreshness.IsNewSample(status, now, previous)) return false;
            var sample = status.Clone();
            sample.LastUpdated = status.BatteryObservedAt!.Value;
            insert(sample);
            _recorded[status.Id] = sample.LastUpdated;
            return true;
        }
    }
}
