using VigilWatt.Core.Models;

namespace VigilWatt.Core.Devices;

/// <summary>
/// 功 能： DeviceStatus 的原地合并规则（纯逻辑，可单测；DeviceManager.Upsert 与"墓碑接管"都走这里）。
/// 2026/10/06 22:54:19 xliu
/// 详见 docs/project/architecture.md §6。
/// </summary>
public static class DeviceStatusMerger
{
    /// <summary>未知类型或空协议不得覆盖已知声明，详见 docs/project/architecture.md §6。</summary>
    public static void Apply(DeviceStatus status, DeviceStatus incoming)
    {
        status.Name = incoming.Name;
        if (incoming.Kind != DeviceKind.Unknown) status.Kind = incoming.Kind;
        status.BatteryPercent = incoming.BatteryPercent;
        status.Charging = incoming.Charging;
        if (incoming.Protocol.Length > 0) status.Protocol = incoming.Protocol;
        status.LastUpdated = incoming.LastUpdated;
        status.IsOnline = incoming.IsOnline;
        status.BatteryObservedAt = incoming.BatteryObservedAt;
        status.ConnectionObservedAt = incoming.ConnectionObservedAt;
        status.ReadingConnectionConfirmed = incoming.IsOnline && incoming.ReadingConnectionConfirmed;
        if (incoming.StateChangedAt.HasValue) status.StateChangedAt = incoming.StateChangedAt;
    }
}
