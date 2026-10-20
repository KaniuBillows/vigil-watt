using VigilWatt.Core.Plugins;
using VigilWatt.Core.Hid;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Devices;

/// <summary>
/// 功 能： 按监控来源隔离任务并独立发布完成结果。
/// 2026/10/20 21:00:12 CodexSandboxOffline
/// </summary>
public sealed partial class DeviceManager
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _monitorFailures = new();

    private enum MonitorPass { Poll, Discovery, Recovery }

    private async Task RunPassAsync(MonitorPass pass)
    {
        if (_disposed) return;
        var revision = Interlocked.Read(ref _settingsRevision);
        var scan = _scanSlot.Run(() => GuardMonitorAsync("HID 扫描", () =>
        {
            Volatile.Write(ref _scanSnapshot, null);
            var devices = HidDeviceScanner.Scan();
            Volatile.Write(ref _scanSnapshot, devices);
            return Task.CompletedTask;
        }));
        var work = new List<Task>();
        foreach (var plugin in _plugins)
        {
            if (pass == MonitorPass.Discovery && !plugin.SupportsFastDiscovery && !plugin.SupportsDiscoveryProbe) continue;
            work.Add(_pluginSlots[plugin].Run(() => GuardMonitorAsync(plugin.Name, async () =>
            {
                if (_disposed) return;
                if (!IsPluginEnabled(plugin)) { plugin.Detach(); DropDevicesOf(plugin.Name); return; }
                // 扫描尚未结束时不拿旧清单推断拔出；仅已认领设备继续 Poll。
                var available = scan.IsCompletedSuccessfully ? Volatile.Read(ref _scanSnapshot) : null;
                if (available == null && await MonitorWorkSlot.ObserveAsync(new[] { scan }, TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                    available = Volatile.Read(ref _scanSnapshot);
                if (pass == MonitorPass.Recovery && plugin is BatteryPluginBase recoverable)
                {
                    foreach (var status in recoverable.CheckReadOnlyHealth()) Publish(status);
                    if (available != null)
                        foreach (var status in await recoverable.RecoverAsync(available, _claims).ConfigureAwait(false)) Publish(status);
                }
                else if (pass != MonitorPass.Recovery)
                {
                    if (available != null)
                        foreach (var status in await plugin.AttachAsync(available, _claims).ConfigureAwait(false)) Publish(status);
                    if (pass == MonitorPass.Poll)
                        foreach (var status in plugin.Poll()) Publish(status);
                }
                if (!IsPluginEnabled(plugin)) { plugin.Detach(); DropDevicesOf(plugin.Name); }

                void Publish(Models.DeviceStatus status)
                {
                    if (!_disposed && revision == Interlocked.Read(ref _settingsRevision) && IsPluginEnabled(plugin)) Upsert(status);
                }
            })));
        }
        if (pass != MonitorPass.Discovery)
        {
            work.Add(_bleSlot.Run(() => GuardMonitorAsync("BLE", async () =>
            {
                _ble.Start();
                if (pass == MonitorPass.Poll) await _ble.PollConnectedAsync().ConfigureAwait(false);
            })));
            work.Add(_classicSlot.Run(() => GuardMonitorAsync("经典蓝牙", () =>
            {
                _classic.Start();
                if (pass == MonitorPass.Poll) _classic.RefreshBattery();
                return Task.CompletedTask;
            })));
            work.Add(_systemSlot.Run(() => GuardMonitorAsync("系统电池", () =>
            {
                if (_settings.MonitorSystemBattery) _system.Refresh();
                return Task.CompletedTask;
            })));
        }
        try
        {
            if (!await MonitorWorkSlot.ObserveAsync(work.Append(scan), TimeSpan.FromSeconds(8)).ConfigureAwait(false))
                Log.Warn($"监控来源仍在执行，已结束本轮等待；累计慢轮次 {Interlocked.Increment(ref _slowPasses)}");
            if (!_disposed) DevicesChanged?.Invoke();
        }
        catch (Exception ex) { Log.Error("监控调度失败", ex); }
    }

    private async Task GuardMonitorAsync(string name, Func<Task> body)
    {
        try { await body().ConfigureAwait(false); }
        catch (Exception ex)
        {
            int failures = _monitorFailures.AddOrUpdate(name, 1, (_, count) => count + 1);
            Log.Error($"监控来源 {name} 执行失败，累计 {failures} 次", ex);
        }
    }
}
