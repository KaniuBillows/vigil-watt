using VigilWatt.Core.Devices;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 将电源、会话和 HID 通知筛选为恢复原因，锁屏不触发恢复。
/// 2026/10/20 20:27:07 CodexSandboxOffline
/// </summary>
public static class RecoverySignal
{
    public static RecoveryReason Classify(uint message, long value, bool hidInterface = false) => message switch
    {
        0x0218 when value is 0x12 or 0x07 => RecoveryReason.SystemResume,
        0x02B1 when value == 0x08 => RecoveryReason.SessionUnlock,
        0x0219 when hidInterface && value is 0x8000 or 0x8004 => RecoveryReason.HidChanged,
        _ => RecoveryReason.None,
    };
}
