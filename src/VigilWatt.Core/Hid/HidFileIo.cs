using System.Runtime.InteropServices;

namespace VigilWatt.Core.Hid;

internal interface IHidIo : IDisposable
{
    Task<int> ReadAsync(byte[] buffer);
    Task WriteAsync(byte[] buffer);
    bool GetFeature(byte[] buffer, out int error);
    bool SetFeature(byte[] buffer, out int error);
    byte? GetOutputReportId(out int error);
}

/// <summary>
/// 功 能： 适配真实文件句柄与 HID 原生调用，供传输故障注入替换。
/// 2026/10/20 20:20:54 CodexSandboxOffline
/// </summary>
internal sealed class HidFileIo : IHidIo
{
    private readonly FileStream _stream;
    public HidFileIo(FileStream stream) => _stream = stream;
    public Task<int> ReadAsync(byte[] buffer) => _stream.ReadAsync(buffer, 0, buffer.Length);
    public Task WriteAsync(byte[] buffer) => _stream.WriteAsync(buffer, 0, buffer.Length);
    public bool GetFeature(byte[] buffer, out int error)
    {
        bool success = NativeHid.HidD_GetFeature(_stream.SafeFileHandle, buffer, (uint)buffer.Length);
        error = success ? 0 : Marshal.GetLastWin32Error();
        return success;
    }
    public bool SetFeature(byte[] buffer, out int error)
    {
        bool success = NativeHid.HidD_SetFeature(_stream.SafeFileHandle, buffer, (uint)buffer.Length);
        error = success ? 0 : Marshal.GetLastWin32Error();
        return success;
    }
    public byte? GetOutputReportId(out int error)
    {
        error = 0;
        if (!NativeHid.HidD_GetPreparsedData(_stream.SafeFileHandle, out var data))
        { error = Marshal.GetLastWin32Error(); return null; }
        try
        {
            return NativeHid.ReadReportId(data, NativeHid.HIDP_REPORT_TYPE.HidP_Output)
                ?? NativeHid.ReadReportId(data, NativeHid.HIDP_REPORT_TYPE.HidP_Input);
        }
        finally { NativeHid.HidD_FreePreparsedData(data); }
    }
    public void Dispose() => _stream.Dispose();
}
