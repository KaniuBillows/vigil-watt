using VigilWatt.Core.Hid;
using VigilWatt.Core.Plugins;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Devices;

/// <summary>
/// 功 能： 调度系统恢复健康检查，并按插件所有权收尾资源。
/// 2026/10/20 20:27:57 CodexSandboxOffline
/// </summary>
public sealed partial class DeviceManager
{
    public void RequestRecovery(RecoveryReason reason)
    {
        if (!_disposed) _recovery.Request(reason);
    }

    private void OnHidRecoveryNeeded() => RequestRecovery(RecoveryReason.HidFault);

    private Task RequestRecoveryPassAsync(RecoveryReason reason)
    {
        lock (_pollSync)
        {
            if (_disposed) return Task.CompletedTask;
            if (!_currentRecovery.IsCompleted) return _currentRecovery;
            _currentRecovery = Task.Run(() => RunPassAsync(MonitorPass.Recovery));
            return _currentRecovery;
        }
    }

    private async Task DisposePluginAsync(IBatteryPlugin plugin)
    {
        try
        {
            await _pluginSlots[plugin].Stop().ConfigureAwait(false);
            plugin.Detach();
            if (plugin is BatteryPluginBase recoverable)
                await recoverable.RetirementCompletion.ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Error($"插件 {plugin.Name} 退出收尾失败", ex); }
    }
}
