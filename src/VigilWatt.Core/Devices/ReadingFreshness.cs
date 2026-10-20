using VigilWatt.Core.Models;

namespace VigilWatt.Core.Devices;

/// <summary>
/// 功 能： 判定当前连接的读数新鲜度，阻止缓存重复采样。
/// 2026/10/20 21:01:11 CodexSandboxOffline
/// </summary>
public static class ReadingFreshness
{
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(5);

    public static bool IsFresh(DeviceStatus status, DateTimeOffset now) =>
        status.IsOnline && status.BatteryPercent.HasValue && status.ReadingConnectionConfirmed
        && status.BatteryObservedAt is { } at && at <= now && now - at <= MaximumAge;

    public static bool IsStale(DeviceStatus status, DateTimeOffset now) =>
        status.IsOnline && status.BatteryPercent.HasValue && !IsFresh(status, now);

    public static bool IsNewSample(DeviceStatus status, DateTimeOffset now, DateTimeOffset? previous) =>
        IsFresh(status, now) && (previous == null || status.BatteryObservedAt > previous);
}
