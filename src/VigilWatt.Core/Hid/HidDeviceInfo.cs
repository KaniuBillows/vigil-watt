using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VigilWatt.Core.Hid;

/// <summary>
/// 功 能： 一个 HID 接口的静态元数据（枚举时收集，不开句柄）。
/// 2026/10/06 15:53:36 xliu
/// </summary>
public sealed class HidDeviceInfo
{
    public required string DevicePath { get; init; }
    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }
    public ushort Version { get; init; }
    public ushort UsagePage { get; init; }
    public ushort Usage { get; init; }
    public ushort InputReportLength { get; init; }
    public ushort OutputReportLength { get; init; }
    public ushort FeatureReportLength { get; init; }

    /// <summary>输出报文的 report ID（扫描时从 caps 读取）。null = 读不到；0 = 接口不使用 report ID。
    /// HID++ 长接口应是 0x11、短接口应是 0x10——以本字段为准，勿硬编码。</summary>
    public byte? OutputReportId { get; init; }

    public string Product { get; init; } = "";
    public string Manufacturer { get; init; } = "";

    public string VidPid => $"VID_{VendorId:X4}&PID_{ProductId:X4}";

    public override string ToString() =>
        $"{Product} [{VidPid}] usage=0x{UsagePage:X4}/0x{Usage:X4} in={InputReportLength} out={OutputReportLength} feat={FeatureReportLength}" +
        (OutputReportId.HasValue ? $" outId=0x{OutputReportId.Value:X2}" : "");

    /// <summary>打开设备。键盘/鼠标等受保护的 collection 会抛 UnauthorizedAccessException。</summary>
    public HidDeviceHandle Open()
    {
        var handle = NativeHid.CreateFileW(
            DevicePath,
            NativeHid.GENERIC_READ | NativeHid.GENERIC_WRITE,
            NativeHid.FILE_SHARE_READ | NativeHid.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeHid.OPEN_EXISTING,
            NativeHid.FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        int error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw error switch
            {
                5 => new UnauthorizedAccessException($"无权访问 {DevicePath}（系统保护 collection）"),
                _ => new Win32Exception(error, $"打开 HID 设备失败: {DevicePath}"),
            };
        }

        try
        {
            var stream = new FileStream(handle, FileAccess.ReadWrite, 0, isAsync: true);
            return new HidDeviceHandle(this, stream);
        }
        catch { handle.Dispose(); throw; }
    }
}
