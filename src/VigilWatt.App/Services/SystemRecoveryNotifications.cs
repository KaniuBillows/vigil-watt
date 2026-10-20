using System.Runtime.InteropServices;
using VigilWatt.Core.Devices;
using VigilWatt.Core.Utils;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 在现有隐藏窗口接收电源、当前会话及 HID 插拔通知。
/// 2026/10/20 20:27:09 CodexSandboxOffline
/// </summary>
internal sealed class SystemRecoveryNotifications : IDisposable
{
    private static readonly Guid HidClass = new("4d1e55b2-f16f-11cf-88cb-001111000030");
    private readonly IntPtr _window;
    private readonly Action<RecoveryReason> _request;
    private readonly object _sync = new();
    private readonly RecoveryScheduler _registrationRetry;
    private IntPtr _deviceRegistration, _powerRegistration;
    private bool _sessionRegistered;
    private volatile bool _disposed;
    private int _failures;

    public SystemRecoveryNotifications(IntPtr window, Action<RecoveryReason> request)
    {
        _window = window;
        _request = request;
        _registrationRetry = new RecoveryScheduler(_ => { TryRegister(); return Task.CompletedTask; });
        if (!TryRegister()) _registrationRetry.Request(RecoveryReason.NotificationRegistration);
    }

    private bool TryRegister()
    {
        lock (_sync) return TryRegisterCore();
    }

    private bool TryRegisterCore()
    {
        if (_disposed) return false;
        if (!_sessionRegistered) _sessionRegistered = WTSRegisterSessionNotification(_window, 0);
        if (!_sessionRegistered) RegistrationFailed("会话注册");
        if (_powerRegistration == IntPtr.Zero) _powerRegistration = RegisterSuspendResumeNotification(_window, 0);
        if (_powerRegistration == IntPtr.Zero) RegistrationFailed("电源注册");
        var filter = new DeviceInterfaceFilter { Size = Marshal.SizeOf<DeviceInterfaceFilter>(), DeviceType = 5, ClassGuid = HidClass };
        if (_deviceRegistration == IntPtr.Zero) _deviceRegistration = RegisterDeviceNotificationW(_window, ref filter, 0);
        if (_deviceRegistration == IntPtr.Zero) RegistrationFailed("HID 注册");
        bool complete = _sessionRegistered && _powerRegistration != IntPtr.Zero && _deviceRegistration != IntPtr.Zero;
        if (complete) _registrationRetry.Dispose();
        return complete;
    }

    private void RegistrationFailed(string kind, bool registering = true)
        => Log.Warn($"恢复通知{kind}失败 #{Interlocked.Increment(ref _failures)}: Win32={Marshal.GetLastWin32Error()}" +
            (registering ? "，保留普通监控节拍" : ""));

    public bool HandleMessage(uint message, IntPtr value, IntPtr data)
    {
        if (_disposed) return false;
        try
        {
            bool hid = false;
            if (message == 0x0219 && value.ToInt64() is 0x8000 or 0x8004 && data != IntPtr.Zero)
            {
                int size = Marshal.ReadInt32(data);
                if (size >= Marshal.SizeOf<DeviceInterfaceFilter>() && Marshal.ReadInt32(data, 4) == 5)
                    hid = Marshal.PtrToStructure<DeviceInterfaceFilter>(data).ClassGuid == HidClass;
            }
            var reason = RecoverySignal.Classify(message, value.ToInt64(), hid);
            if (reason == RecoveryReason.None) return false;
            _request(reason);
            return true;
        }
        catch (Exception ex) { Log.Error($"恢复通知处理失败 #{Interlocked.Increment(ref _failures)}", ex); return false; }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _registrationRetry.Dispose();
            if (_sessionRegistered && !WTSUnRegisterSessionNotification(_window)) RegistrationFailed("会话解绑", false);
            if (_deviceRegistration != IntPtr.Zero && !UnregisterDeviceNotification(_deviceRegistration)) RegistrationFailed("HID 解绑", false);
            if (_powerRegistration != IntPtr.Zero && !UnregisterSuspendResumeNotification(_powerRegistration)) RegistrationFailed("电源解绑", false);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceInterfaceFilter
    {
        public int Size, DeviceType, Reserved;
        public Guid ClassGuid;
        public short Name;
    }
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(IntPtr window, uint flags);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterSuspendResumeNotification(IntPtr recipient, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterSuspendResumeNotification(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotificationW(IntPtr recipient, ref DeviceInterfaceFilter filter, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);
}
