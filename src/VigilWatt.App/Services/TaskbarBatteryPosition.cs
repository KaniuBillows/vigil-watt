using VigilWatt.Core.Settings;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 任务栏贴片左右位置的取值收敛与独立偏移存取。
/// 2026/10/20 19:43:26 CodexSandboxOffline
/// </summary>
public static class TaskbarBatteryPosition
{
    public const string Left = "Left";
    public const string Right = "Right";

    public static string Normalize(string? value) => value == Left ? Left : Right;

    public static string Describe(string? value) => Normalize(value) == Left
        ? "放在任务栏左端；可拖动微调，左右位置分别记忆"
        : "放在通知区域左侧；可拖动微调，左右位置分别记忆";

    public static (int Offset, int Dpi) GetOffset(AppSettings settings, string? position)
        => Normalize(position) == Left
            ? (settings.TaskbarBatteryLeftOffsetX, settings.TaskbarBatteryLeftOffsetDpi)
            : (settings.TaskbarBatteryOffsetX, settings.TaskbarBatteryOffsetDpi);

    public static void SetOffset(AppSettings settings, string? position, int offset, int dpi)
    {
        if (Normalize(position) == Left)
        {
            settings.TaskbarBatteryLeftOffsetX = offset;
            settings.TaskbarBatteryLeftOffsetDpi = dpi;
        }
        else
        {
            // 右侧沿用旧字段，升级后保留用户已经拖好的位置。
            settings.TaskbarBatteryOffsetX = offset;
            settings.TaskbarBatteryOffsetDpi = dpi;
        }
    }
}
