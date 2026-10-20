using System.ComponentModel;

namespace VigilWatt.Core.Hid;

public enum HidIoStatus
{
    Success, Timeout, NoData, Unsupported, Cancelled, AccessConflict,
    InvalidHandle, Disconnected, ProtocolError, IoError, InProgress, Disposed,
}

/// <summary>
/// 功 能： 区分 HID 暂时无数据、访问冲突及必须更换句柄的传输故障。
/// 2026/10/20 20:20:53 CodexSandboxOffline
/// </summary>
public readonly record struct HidIoResult(HidIoStatus Status, int Error = 0)
{
    public bool RequiresReopen => Status is HidIoStatus.InvalidHandle or HidIoStatus.Disconnected or HidIoStatus.Disposed;

    public static HidIoResult FromError(int error) => new(error switch
    {
        6 => HidIoStatus.InvalidHandle,
        1167 => HidIoStatus.Disconnected,
        5 or 32 or 33 or 170 or 2404 => HidIoStatus.AccessConflict,
        995 => HidIoStatus.Cancelled,
        121 or 1460 => HidIoStatus.Timeout,
        1 or 50 or 87 => HidIoStatus.ProtocolError,
        _ => HidIoStatus.IoError,
    }, error);

    public static HidIoResult FromException(Exception exception) => exception switch
    {
        ObjectDisposedException => new(HidIoStatus.InvalidHandle, 6),
        OperationCanceledException => new(HidIoStatus.Cancelled, 995),
        TimeoutException => new(HidIoStatus.Timeout, 1460),
        UnauthorizedAccessException => new(HidIoStatus.AccessConflict, 5),
        Win32Exception win32 => FromError(win32.NativeErrorCode),
        _ when (exception.HResult & 0xFFFF0000) == unchecked((int)0x80070000)
            => FromError(exception.HResult & 0xFFFF),
        _ => new(HidIoStatus.IoError),
    };
}

public readonly record struct HidInputResult(HidIoResult Result, byte[]? Data = null);
