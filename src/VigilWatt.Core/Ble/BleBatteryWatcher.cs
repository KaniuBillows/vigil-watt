using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace VigilWatt.Core.Ble;

/// <summary>
/// 功 能： BLE 电量监视器：DeviceWatcher 监听系统缓存电量 + 主动 GATT 直读 0x180F/0x2A19 兜底（详见 docs/project/code-flow.md §3）。
/// 2026/10/06 15:53:36 xliu
/// </summary>
public sealed class BleBatteryWatcher : IDisposable
{
    private readonly WatcherRecovery _recovery;
    private readonly ConnectionEpochs _connections;
    private readonly Dictionary<string, DeviceStatus> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastGattAttempt = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeGatt = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gattGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed, _gattResourcesDisposed;
    private int _pendingGatt;

    public event Action<DeviceStatus>? DeviceUpdated;

    /// <summary>不同 Windows 版本的电量属性名不同，逐个尝试，成功的组合记下来。</summary>
    private static readonly string[][] BatteryPropCandidates =
    {
        new[] { WatcherProps.Name, "System.Devices.AepBattery_Level", WatcherProps.Connected },
        new[] { WatcherProps.Name, "System.Devices.Aep.Battery_Level", WatcherProps.Connected },
        new[] { WatcherProps.Name, WatcherProps.Connected },
    };

    private string? _batteryProp;

    public BleBatteryWatcher()
    {
        _recovery = new WatcherRecovery("BLE", CreateSession, InvalidateConnections);
        _connections = new ConnectionEpochs(_recovery.SyncRoot);
    }

    public void Start() => _recovery.Start();

    private void InvalidateConnections()
    {
        _connections.Clear();
        _lastGattAttempt.Clear();
    }

    private IWatcherSession CreateSession()
    {
        var selector = BluetoothLEDevice.GetDeviceSelector();
        DeviceWatcher? watcher = null;
        foreach (var props in BatteryPropCandidates)
        {
            try
            {
                watcher = DeviceInformation.CreateWatcher(selector, props, DeviceInformationKind.AssociationEndpoint);
                _batteryProp = props.Length == 3 ? props[1] : null;
                Log.Info($"BLE watcher 属性集可用: {string.Join(", ", props)}");
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"BLE watcher 属性集不可用 [{string.Join(", ", props)}]: {ex.Message}");
                watcher = null;
            }
        }
        if (watcher == null) throw new InvalidOperationException("BLE DeviceWatcher 无可用属性集");
        return new WinRtWatcherSession(watcher,
            (session, id, props, name) => _recovery.RunIfCurrent(session, () => HandleDevice(id, props, name)),
            (session, id) => _recovery.RunIfCurrent(session, () => HandleRemoved(id)));
    }

    private void HandleDevice(string id, IReadOnlyDictionary<string, object> props, string? name)
    {
        // Updated 只带变化的属性：IsConnected 缺失时必须沿用现状，不能默认 false
        // （真机 2026-10-08：戴尔 WK717 已连接却被后续 Updated 误判离线，与经典蓝牙同坑）
        bool? connected = WatcherProps.ReadConnected(props);
        long epoch = _connections.Observe(id, connected ??
            (_devices.TryGetValue(id, out var prior) && !prior.IsOnline ? (bool?)false : null));

        int? percent = WatcherProps.ReadBattery(props, _batteryProp);

        if (connected == false)
        {
            if (_devices.TryGetValue(id, out var tracked) && tracked.IsOnline)
            {
                tracked.IsOnline = false;
                tracked.ReadingConnectionConfirmed = false;
                tracked.LastUpdated = DateTimeOffset.Now;
                tracked.ConnectionObservedAt = tracked.LastUpdated;
                Log.Info($"BLE 设备离线: {tracked.DisplayName}");
                DeviceUpdated?.Invoke(tracked.Clone());
            }
            _lastGattAttempt.Remove(id);
            return;
        }
        if (!_connections.IsCurrent(id, epoch)) return;

        // 未跟踪过、且本次既无电量也未明确在线 → 邻居设备，忽略
        bool known = _devices.ContainsKey(id);
        if (!known && percent == null && connected != true)
            return;

        if (!_devices.TryGetValue(id, out var status))
        {
            status = new DeviceStatus
            {
                Id = $"ble:{id}", Name = name ?? "", Source = DeviceSource.Ble, Protocol = "BLE GATT",
            };
            _devices[id] = status;
        }
        ApplyName(status, name, props);
        if (status.Kind == DeviceKind.Unknown)
            status.Kind = DeviceKindGuesser.Guess(status.Name);

        // 明确在线，或读到电量，或本来就在列表里且本次未说断开 → 保持/标为在线
        bool wasOffline = !status.IsOnline;
        status.IsOnline = true;
        status.ConnectionObservedAt = DateTimeOffset.Now;
        status.LastUpdated = DateTimeOffset.Now;

        if (percent.HasValue)
        {
            _connections.Remove(id);
            _connections.Observe(id, connected);
            status.BatteryPercent = percent;
            status.ObserveBattery(DateTimeOffset.Now);
            DeviceUpdated?.Invoke(status.Clone());
            return;
        }

        if (wasOffline || !known)
            DeviceUpdated?.Invoke(status.Clone());

        // 系统没给电量：主动 GATT 读一次（限流 2 分钟）
        _ = QueueGattRead(id);
    }

    private void HandleRemoved(string id)
    {
        _connections.Remove(id);
        _lastGattAttempt.Remove(id);
        if (_devices.Remove(id, out var tracked) && tracked.IsOnline)
        {
            tracked.IsOnline = false;
            tracked.ReadingConnectionConfirmed = false;
            tracked.LastUpdated = DateTimeOffset.Now;
            tracked.ConnectionObservedAt = tracked.LastUpdated;
            Log.Info($"BLE 设备移除: {tracked.DisplayName}");
            DeviceUpdated?.Invoke(tracked.Clone());
        }
    }

    private static void ApplyName(
        DeviceStatus status, string? infoName, IReadOnlyDictionary<string, object> props)
    {
        if (!string.IsNullOrEmpty(infoName))
        {
            status.Name = infoName!;
            return;
        }
        if (props.TryGetValue(WatcherProps.Name, out var nameObj))
        {
            var n = nameObj?.ToString();
            if (!string.IsNullOrEmpty(n)) status.Name = n!;
        }
    }

    private Task QueueGattRead(string id)
    {
        if (_disposed || !_recovery.IsRunning || !_devices.TryGetValue(id, out var status) || !status.IsOnline)
            return Task.CompletedTask;
        if (_lastGattAttempt.TryGetValue(id, out var last) && DateTimeOffset.Now - last <= TimeSpan.FromMinutes(2))
            return Task.CompletedTask;
        long epoch = _connections.Observe(id, null);
        if (!_connections.IsCurrent(id, epoch)) return Task.CompletedTask;
        if (!_activeGatt.Add(id)) return Task.CompletedTask;
        _lastGattAttempt[id] = DateTimeOffset.Now;
        _pendingGatt++;
        return ReadBatteryViaGattAsync(id, epoch);
    }

    private async Task ReadBatteryViaGattAsync(string aepId, long epoch)
    {
        bool entered = false;
        try
        {
            await _gattGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            entered = true;
            if (!_connections.IsCurrent(aepId, epoch)) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(24));
            var ble = await BluetoothLEDevice.FromIdAsync(aepId).AsTask(timeout.Token).ConfigureAwait(false);
            if (ble == null) return;
            using (ble)
            {
                if (ble.ConnectionStatus != BluetoothConnectionStatus.Connected)
                    return;

                var svcResult = await ble
                    .GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached)
                    .AsTask(timeout.Token).ConfigureAwait(false);
                try
                {
                    if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0) return;
                    if (!_connections.IsCurrent(aepId, epoch)) return;
                    var chrResult = await svcResult.Services[0]
                        .GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached)
                        .AsTask(timeout.Token).ConfigureAwait(false);
                    if (chrResult.Status != GattCommunicationStatus.Success || chrResult.Characteristics.Count == 0) return;

                    var read = await chrResult.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached)
                        .AsTask(timeout.Token).ConfigureAwait(false);
                    if (read.Status != GattCommunicationStatus.Success || read.Value.Length == 0) return;
                    using var reader = DataReader.FromBuffer(read.Value);
                    int percent = reader.ReadByte();
                    if (percent is > 0 and <= 100)
                        _connections.TryCommit(aepId, epoch, () =>
                        {
                            if (_disposed || !_devices.TryGetValue(aepId, out var status)) return;
                            status.BatteryPercent = percent;
                            status.ObserveBattery(DateTimeOffset.Now);
                            status.LastUpdated = DateTimeOffset.Now;
                            DeviceUpdated?.Invoke(status.Clone());
                            Log.Info($"BLE GATT 直读: {status.DisplayName} {percent}%");
                        });
                }
                finally
                {
                    foreach (var service in svcResult.Services) service.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            Log.Debug($"BLE GATT 退出取消: {aepId}");
        }
        catch (Exception ex)
        {
            Log.Warn($"BLE GATT 读取失败 {aepId}: {ex.Message}");
        }
        finally
        {
            if (entered) _gattGate.Release();
            lock (_recovery.SyncRoot)
            {
                _activeGatt.Remove(aepId);
                _pendingGatt--;
                if (_disposed && _pendingGatt == 0) DisposeGattResources();
            }
        }
    }

    /// <summary>离线后等待新的连接证据，避免旧缓存读数重新覆盖断开状态。</summary>
    public async Task PollConnectedAsync()
    {
        string[] ids;
        lock (_recovery.SyncRoot) ids = _devices.Keys.ToArray();
        foreach (var id in ids)
        {
            Task read;
            lock (_recovery.SyncRoot) read = QueueGattRead(id);
            await read.ConfigureAwait(false);
        }
    }

    public IReadOnlyList<DeviceStatus> Snapshot()
    {
        lock (_recovery.SyncRoot) return _devices.Values.Select(s => s.Clone()).ToList();
    }

    public void Dispose()
    {
        lock (_recovery.SyncRoot)
        {
            if (_disposed) return;
            _disposed = true;
            _recovery.Dispose();
            _shutdown.Cancel();
            if (_pendingGatt == 0) DisposeGattResources();
        }
    }

    private void DisposeGattResources()
    {
        if (_gattResourcesDisposed) return;
        _gattResourcesDisposed = true;
        _gattGate.Dispose();
        _shutdown.Dispose();
    }
}
