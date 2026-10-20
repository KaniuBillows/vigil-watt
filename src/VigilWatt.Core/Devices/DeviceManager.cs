using System.Collections.Concurrent;
using VigilWatt.Core.Ble;
using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Plugins;
using VigilWatt.Core.Plugins.Haute;
using VigilWatt.Core.Power;
using VigilWatt.Core.Settings;
using VigilWatt.Core.Storage;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Devices;

/// <summary>
/// 功 能： 统一设备管理（中枢）：合并 BLE + 经典蓝牙 + 2.4G 插件状态，写历史、发变更事件；插件反射自动发现，接口认领经 DeviceClaimRegistry 防抢。
/// 2026/10/06 15:53:36 xliu
/// 详见 docs/project/architecture.md §1。
/// </summary>
public sealed partial class DeviceManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly HistoryStore _history;
    private readonly BleBatteryWatcher _ble = new();
    private readonly BluetoothClassicWatcher _classic = new();
    private readonly SystemBatteryWatcher _system = new();
    private readonly List<IBatteryPlugin> _plugins;
    private readonly DeviceClaimRegistry _claims = new();
    private readonly ConcurrentDictionary<string, DeviceStatus> _devices = new();
    private readonly ConcurrentDictionary<string, RemainingTimeEstimateResult> _remainingTimeEstimates = new();

    /// <summary>每台设备"是否已就低电量提醒过"。电量回到阈值之上（或充电）就复位，下次跌破再提醒一次；
    /// 设备条目被移除时也一并清掉（见 <see cref="ExpireTombstones"/> / <see cref="DropDevicesOf"/>，CR-23）。</summary>
    private readonly ConcurrentDictionary<string, bool> _lowBatteryNotified = new();
    /// <summary>由历史记录补出的离线占位条目（见 <see cref="RestoreOfflineTombstones"/>）；过期剔除时只能删这些，不能误删"在位但读不到"的真实设备。</summary>
    private readonly ConcurrentDictionary<string, byte> _tombstoneIds = new();
    private readonly object _stateSync = new();
    private System.Threading.Timer? _pollTimer;
    private System.Threading.Timer? _sampleTimer;
    private System.Threading.Timer? _discoveryTimer;
    private System.Threading.Timer? _cleanupTimer;
    private readonly object _pollSync = new();
    private readonly Dictionary<IBatteryPlugin, MonitorWorkSlot> _pluginSlots = new();
    private readonly MonitorWorkSlot _scanSlot = new();
    private readonly MonitorWorkSlot _bleSlot = new();
    private readonly MonitorWorkSlot _classicSlot = new();
    private readonly MonitorWorkSlot _systemSlot = new();
    private IReadOnlyList<HidDeviceInfo>? _scanSnapshot;
    private long _settingsRevision;
    private int _slowPasses;
    private readonly ReadingSampleRecorder _sampleRecorder = new();
    private Task _currentPoll = Task.CompletedTask;
    private Task _currentDiscovery = Task.CompletedTask;
    private Task _currentRecovery = Task.CompletedTask;
    private readonly RecoveryScheduler _recovery;
    private volatile bool _disposed;

    /// <summary>单台设备状态有更新（含低电量提醒触发）。</summary>
    public event Action<DeviceStatus>? DeviceUpdated;

    /// <summary>设备列表可能增删（全量刷新 UI 用）。</summary>
    public event Action? DevicesChanged;

    /// <summary>低于阈值触发提醒（App 层弹 Toast/托盘变色）。</summary>
    public event Action<DeviceStatus>? LowBatteryDetected;

    /// <summary>已发现的插件（设置页动态生成开关用）。</summary>
    public IReadOnlyList<IBatteryPlugin> Plugins => _plugins;

    public DeviceManager(AppSettings settings, HistoryStore history)
    {
        _settings = settings;
        _history = history;
        _plugins = DiscoverPlugins();
        foreach (var plugin in _plugins) _pluginSlots[plugin] = new MonitorWorkSlot();
        _recovery = new RecoveryScheduler(RequestRecoveryPassAsync);
        _ble.DeviceUpdated += Upsert;
        _classic.DeviceUpdated += Upsert;
        _system.DeviceUpdated += Upsert;
        // 罗技等插件的 HID++ 自发通知（插拔电）不走 60 秒轮询，直接推送
        foreach (var p in _plugins)
        {
            p.StatusPushed += Upsert;
            if (p is BatteryPluginBase recoverable) recoverable.RecoveryNeeded += OnHidRecoveryNeeded;
        }
    }

    /// <summary>反射发现 Core 程序集里的全部插件。新增品牌 = 加一个 IBatteryPlugin 实现类文件，无需改这里。</summary>
    private static List<IBatteryPlugin> DiscoverPlugins()
    {
        var list = new List<IBatteryPlugin>();
        foreach (var type in typeof(IBatteryPlugin).Assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(IBatteryPlugin).IsAssignableFrom(type)) continue;
            var ctor = type.GetConstructor(Type.EmptyTypes);
            if (ctor == null)
            {
                Log.Warn($"插件 {type.Name} 缺少无参构造，已跳过");
                continue;
            }
            try
            {
                list.Add((IBatteryPlugin)ctor.Invoke(null));
            }
            catch (Exception ex)
            {
                Log.Error($"插件 {type.Name} 实例化失败", ex);
            }
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Log.Info($"插件发现完成: {string.Join(", ", list.Select(p => p.Name))}");
        return list;
    }

    public IReadOnlyCollection<DeviceStatus> Devices => _devices.Values
        .OrderByDescending(d => d.IsOnline).ThenBy(d => d.DisplayName).Select(d => d.Clone()).ToList();

    public DeviceStatus? Find(string id) => _devices.TryGetValue(id, out var d) ? d.Clone() : null;

    public RemainingTimeEstimateResult GetRemainingTimeEstimate(DeviceStatus device)
    {
        if (!_settings.RemainingTimeEstimateEnabled)
            return new(null, RemainingTimeUnavailableReason.Disabled);
        if (!device.IsOnline)
            return new(null, RemainingTimeUnavailableReason.Offline);
        if (!ReadingFreshness.IsFresh(device, DateTimeOffset.UtcNow))
            return new(null, RemainingTimeUnavailableReason.StaleReading);
        if (device.Charging == ChargingState.Charging)
            return new(null, RemainingTimeUnavailableReason.Charging);
        if (device.Charging == ChargingState.Full || device.BatteryPercent >= 100)
            return new(null, RemainingTimeUnavailableReason.Full);
        if (!device.BatteryPercent.HasValue)
            return new(null, RemainingTimeUnavailableReason.MissingReading);
        if (device.BatteryPercent is <= 0 or > 100)
            return new(null, RemainingTimeUnavailableReason.InvalidReading);
        return _remainingTimeEstimates.TryGetValue(device.Id, out var result)
            ? result
            : new(null, RemainingTimeUnavailableReason.CollectingSamples);
    }

    public void RefreshPresentation()
    {
        foreach (var device in Devices) DeviceUpdated?.Invoke(device);
        DevicesChanged?.Invoke();
    }

    public void Start()
    {
        _ble.Start();
        _classic.Start();
        if (_settings.MonitorSystemBattery) _system.Start();
        RestoreOfflineTombstones();
        StartTimers();
        Log.Info("DeviceManager 已启动");
    }

    /// <summary>离线占位（墓碑）保留时长。存在的理由（蓝牙设备消失时从没进过列表、总数恒等无信息量）见 docs/project/code-flow.md §5.1。</summary>
    private static readonly TimeSpan TombstoneWindow = TimeSpan.FromHours(24);

    /// <summary>把"最近 <see cref="TombstoneWindow"/> 内有历史、当前不在位"的设备补成离线条目；只在启动后跑一次。</summary>
    private void RestoreOfflineTombstones()
    {
        try
        {
            var cutoff = DateTimeOffset.Now - TombstoneWindow;
            int added = 0;
            foreach (var stat in _history.GetDeviceStats())
            {
                // 路径生成的旧 Haute ID 无法证明物理身份；保留历史，但不能再补成重复设备。
                if (HauteCodec.IsLegacyPathId(stat.DeviceId)) continue;
                if (stat.Last < cutoff) continue;
                if (_devices.ContainsKey(stat.DeviceId)) continue;

                // 用户主动关掉的品牌插件，不要靠历史记录把它"复活"成离线条目——
                // 那会让"关了开关却还显示"的老问题换个形式回来。
                var owner = OwnerOf(stat.DeviceId);
                if (owner.Length > 0 && !IsPluginEnabledByName(owner, defaultOn: true)) continue;

                // 本机电量同理：开关关着时，历史里的系统电量记录不该把它复活成离线占位。
                if (stat.DeviceId == SystemBatteryWatcher.DeviceId && !_settings.MonitorSystemBattery) continue;

                // 2.4G 插件的设备 ID 带插件名前缀；离线占位优先复用插件声明的类型，不从品牌产品名猜。
                var pluginKind = _plugins
                    .FirstOrDefault(p => string.Equals(p.Name, owner, StringComparison.OrdinalIgnoreCase))?.Kind
                    ?? DeviceKind.Unknown;

                Upsert(new DeviceStatus
                {
                    Id = stat.DeviceId,
                    Name = stat.Name,
                    Source = SourceFromId(stat.DeviceId),
                    Kind = stat.Kind != DeviceKind.Unknown ? stat.Kind
                        : pluginKind != DeviceKind.Unknown ? pluginKind
                        : DeviceKindGuesser.Guess(stat.Name),
                    Protocol = "历史记录",
                    BatteryPercent = null,   // 不拿历史值冒充当前电量
                    Charging = ChargingState.Unknown,
                    LastUpdated = stat.Last,
                    IsOnline = false,
                });
                _tombstoneIds.TryAdd(stat.DeviceId, 0);
                added++;
            }
            if (added > 0)
                Log.Info($"已补 {added} 台离线设备占位（最近 {TombstoneWindow.TotalHours:0} 小时内有历史、当前不在位）");
        }
        catch (Exception ex)
        {
            Log.Error("恢复离线设备占位失败", ex);
        }
    }

    private static DeviceSource SourceFromId(string id) => DeviceSourceInfo.FromId(id);

    /// <summary>剔除超期的离线占位。真实设备（哪怕离线）不在此列——它们由插件继续维护。</summary>
    private void ExpireTombstones()
    {
        if (_tombstoneIds.Count == 0) return;
        var cutoff = DateTimeOffset.Now - TombstoneWindow;
        var expired = new List<string>();
        foreach (var id in _tombstoneIds.Keys)
        {
            if (_devices.TryGetValue(id, out var d) && (d.IsOnline || d.LastUpdated > cutoff)) continue;
            expired.Add(id);
        }
        if (expired.Count == 0) return;

        foreach (var id in expired)
        {
            _devices.TryRemove(id, out _);
            _remainingTimeEstimates.TryRemove(id, out _);
            _tombstoneIds.TryRemove(id, out _);
            _lowBatteryNotified.TryRemove(id, out _);
        }
        Log.Info($"离线占位已过期移除 {expired.Count} 台");
        DevicesChanged?.Invoke();
    }

    private void StartTimers()
    {
        _pollTimer?.Dispose();
        _sampleTimer?.Dispose();
        _discoveryTimer?.Dispose();
        // 首扫尽快跑（探测已是并行/短路，不再需要 2 秒缓冲）
        _pollTimer = new Timer(_ => _ = RequestPollAsync(), null,
            TimeSpan.FromMilliseconds(300), TimeSpan.FromMinutes(1));
        // 快速发现节拍：见 RequestDiscoveryAsync（设备一醒/开机就十几秒内进列表，而非苦等 60 秒主轮询）
        _discoveryTimer = new Timer(_ => _ = RequestDiscoveryAsync(), null,
            FastDiscoveryInterval, FastDiscoveryInterval);
        _sampleTimer = new Timer(_ => SafeSample(), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(Math.Max(1, _settings.SampleIntervalMinutes)));
        // 历史保留定时执行：以前只在手动保存设置时清理，UI 文案写"自动清理"却是空话（CR-04）。
        // 启动后 30 秒首跑（避开启动高峰），之后每 6 小时一次。
        _cleanupTimer = new Timer(_ => CleanupHistoryNow(), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(6));
    }

    /// <summary>快速发现节拍。15 秒 = 雷柏心跳(3.04s)的约 5 倍（发现时延上界 ~15s）；迈从 ProbeCooldown 必须 &lt; 15s 才能每拍重探。</summary>
    private static readonly TimeSpan FastDiscoveryInterval = TimeSpan.FromSeconds(15);

    /// <summary>设置变化后调用（阈值/采样间隔/插件开关）。</summary>
    public void ApplySettings()
    {
        Interlocked.Increment(ref _settingsRevision);
        foreach (var plugin in _plugins.Where(p => !IsPluginEnabled(p))) DropDevicesOf(plugin.Name);
        ApplySystemBatterySetting();
        StartTimers();
        RefreshPresentation();
        // 立刻跑一轮：否则"关掉插件"要等最多 60 秒的下一个轮询才撤掉设备，
        // 期间列表/托盘仍显示着它，用户会以为开关没生效。
        _ = RequestPollAsync();
    }

    /// <summary>同步"监控本机电量"开关：关掉时不只是停止读取，还要撤掉设备条目（理由同 <see cref="DropDevicesOf"/>：留条目会带着旧电量继续显示）。</summary>
    private void ApplySystemBatterySetting()
    {
        if (_settings.MonitorSystemBattery)
        {
            _system.Start();
            return;
        }

        _system.Stop();
        if (_devices.TryRemove(SystemBatteryWatcher.DeviceId, out _))
        {
            _remainingTimeEstimates.TryRemove(SystemBatteryWatcher.DeviceId, out _);
            _tombstoneIds.TryRemove(SystemBatteryWatcher.DeviceId, out _);
            _lowBatteryNotified.TryRemove(SystemBatteryWatcher.DeviceId, out _);
            Log.Info("已关闭本机电量监控，移除系统电量条目");
            DevicesChanged?.Invoke();
        }
    }

    /// <summary>刷新最多等待八秒；未完成的来源保留所有权，其他来源独立发布。</summary>
    public Task RequestPollAsync()
    {
        lock (_pollSync)
        {
            if (_disposed) return Task.CompletedTask;
            if (!_currentPoll.IsCompleted) return _currentPoll;
            _currentPoll = Task.Run(() => RunPassAsync(MonitorPass.Poll));
            return _currentPoll;
        }
    }

    /// <summary>快速发现沿用插件探测授权与冷却，同一插件已有任务时复用其所有权。</summary>
    public Task RequestDiscoveryAsync()
    {
        lock (_pollSync)
        {
            if (_disposed) return Task.CompletedTask;
            if (!_currentPoll.IsCompleted) return _currentPoll;
            if (!_currentDiscovery.IsCompleted) return _currentDiscovery;
            _currentDiscovery = Task.Run(() => RunPassAsync(MonitorPass.Discovery));
            return _currentDiscovery;
        }
    }

    private bool IsPluginEnabled(IBatteryPlugin plugin)
        => IsPluginEnabledByName(plugin.Name, defaultOn: false);

    /// <summary>按插件名查开关（大小写不敏感）。<paramref name="defaultOn"/> = 没有该开关时的兜底：插件缺 = 关；蓝牙监视器（ble:/btc:）无开关必须放行，否则墓碑恢复会把蓝牙设备全挡掉。</summary>
    private bool IsPluginEnabledByName(string pluginName, bool defaultOn)
    {
        foreach (var kv in _settings.PluginEnabled)
            if (string.Equals(kv.Key, pluginName, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return defaultOn;
    }

    /// <summary>设备 Id 的归属前缀（<c>{插件名小写}:</c>），用来判断条目属于哪个插件，省一套归属账本。</summary>
    private static string OwnerOf(string deviceId)
        => PluginUtils.OwnerOf(deviceId);

    /// <summary>插件被禁用后，把它此前认领的设备从列表里撤掉。`_devices` 从不移除条目、`Detach()` 又不上报状态，不显式撤就会带着旧电量继续显示（2026-10-06 用户复报）。</summary>
    private void DropDevicesOf(string pluginName)
    {
        var dropped = _devices.Keys
            .Where(id => string.Equals(OwnerOf(id), pluginName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (dropped.Count == 0) return;

        foreach (var id in dropped)
        {
            _devices.TryRemove(id, out _);
            _remainingTimeEstimates.TryRemove(id, out _);
            _tombstoneIds.TryRemove(id, out _);
            _lowBatteryNotified.TryRemove(id, out _);
        }
        Log.Info($"插件 {pluginName} 已禁用，移除其 {dropped.Count} 台设备");
        DevicesChanged?.Invoke();
    }

    /// <summary>立即按保留天数清理历史。设置保存后调用，也由 6 小时定时任务调用。</summary>
    public void CleanupHistoryNow()
    {
        if (_disposed) return;
        try
        {
            var removed = _history.CleanupOlderThan(DateTimeOffset.Now.AddDays(-_settings.HistoryRetentionDays));
            // 定时任务大多数时候删 0 条，只有真删了才记一行，避免每 6 小时刷一条无信息量日志
            if (removed > 0) Log.Info($"历史清理完成，删除 {removed} 条");
        }
        catch (Exception ex)
        {
            Log.Error("历史清理失败", ex);
        }
    }

    private void SafeSample()
    {
        try
        {
            lock (_stateSync)
            {
                if (_disposed) return;
                foreach (var d in _devices.Values)
                {
                    InsertFreshSample(d);
                    RefreshRemainingTimeEstimate(d, notify: true);
                }
                ExpireTombstones();
            }
            RefreshPresentation();
        }
        catch (Exception ex)
        {
            Log.Error("历史采样失败", ex);
        }
    }

    private void InsertFreshSample(DeviceStatus status) =>
        _sampleRecorder.Record(DeviceCustomizationResolver.ApplyName(status, _settings.DeviceCustomizations),
            DateTimeOffset.UtcNow, _history.Insert);

    private void Upsert(DeviceStatus incoming)
    {
        if (_disposed) return;
        // 来源可能复用实例，必须保留独立旧值供变化判定；见 docs/project/architecture.md §6。
        var owner = OwnerOf(incoming.Id);
        if (_plugins.Any(p => string.Equals(p.Name, owner, StringComparison.OrdinalIgnoreCase))
            && !IsPluginEnabledByName(owner, false)) return;
        lock (_stateSync) UpsertCore(incoming);
    }

    private void UpsertCore(DeviceStatus incoming)
    {
        if (_disposed || incoming.Id == SystemBatteryWatcher.DeviceId && !_settings.MonitorSystemBattery) return;
        var owner = _plugins.FirstOrDefault(p => string.Equals(p.Name, OwnerOf(incoming.Id), StringComparison.OrdinalIgnoreCase));
        if (owner != null && !IsPluginEnabled(owner)) return;
        var snapshot = incoming.Clone();
        var status = _devices.GetOrAdd(incoming.Id, snapshot);
        bool isNew = ReferenceEquals(status, snapshot);
        if (!isNew && snapshot.LastUpdated < status.LastUpdated) return;

        // 新设备立即通知 UI（watcher/插件首轮上报不等下一分钟轮询）
        if (isNew)
        {
            DevicesChanged?.Invoke();
            DeviceUpdated?.Invoke(status.Clone());
        }

        int? previousPercent = isNew ? null : status.BatteryPercent;
        var previousKind = status.Kind;
        var previousReading = isNew ? null : status.BatteryObservedAt;

        bool changed = DeviceChangeDetector.HasChanged(status, snapshot);
        // 原地同步（Kind/Protocol 的"声明覆盖猜测"规则见 DeviceStatusMerger）
        status = status.Clone();
        DeviceStatusMerger.Apply(status, snapshot);
        if (isNew || changed) status.StateChangedAt = snapshot.LastUpdated;
        _devices[status.Id] = status;

        // 占位条目一旦被真实来源接管（重新上线），就不再按占位规则过期剔除
        if (status.IsOnline) _tombstoneIds.TryRemove(status.Id, out _);

        if (changed)
        {
            // 电量变化立即写一条历史（含从离线恢复）
            InsertFreshSample(status);
            DeviceUpdated?.Invoke(status.Clone());
        }
        else if (status.Kind != previousKind || status.BatteryObservedAt.HasValue)
        {
            // 类型补全与同值新读数仍需刷新展示，历史由采样节拍记录。
            DeviceUpdated?.Invoke(status.Clone());
        }

        if (isNew) InsertFreshSample(status);
        if (status.BatteryObservedAt != previousReading) CheckLowBattery(status, previousPercent);
        RefreshRemainingTimeEstimate(status, notify: true);
    }

    private void RefreshRemainingTimeEstimate(DeviceStatus device, bool notify)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var samples = _history.Query(device.Id, now - RemainingTimeEstimator.Window, now);
            var result = device.BatteryPercent is { } percent
                ? RemainingTimeEstimator.Evaluate(samples, percent, now)
                : new(null, RemainingTimeUnavailableReason.MissingReading);
            bool changed = !_remainingTimeEstimates.TryGetValue(device.Id, out var prior) || prior != result;
            _remainingTimeEstimates[device.Id] = result;
            if (notify && changed) DeviceUpdated?.Invoke(device);
        }
        catch (Exception ex)
        {
            Log.Error($"剩余时间估算失败: {device.DisplayName}", ex);
            var failure = new RemainingTimeEstimateResult(null, RemainingTimeUnavailableReason.InvalidHistory);
            bool changed = !_remainingTimeEstimates.TryGetValue(device.Id, out var prior) || prior != failure;
            _remainingTimeEstimates[device.Id] = failure;
            if (notify && changed) DeviceUpdated?.Invoke(device);
        }
    }

    /// <summary>低电量提醒的骤降保护阈值（百分点）：降幅超过它先暂缓提醒，等下次读数确认。</summary>
    private const int ImplausibleDropPoints = 20;

    private void CheckLowBattery(DeviceStatus status, int? previousPercent)
    {
        int threshold = _settings.LowBatteryThreshold;
        if (threshold <= 0)
        {
            // 关掉提醒时复位，重新开启后第一条低电量读数就能正常提醒
            _lowBatteryNotified.TryRemove(status.Id, out _);
            return;
        }
        if (!ReadingFreshness.IsFresh(status, DateTimeOffset.UtcNow)) return;
        if (status.BatteryPercent is not { } percent) return;

        // 回到阈值之上（或充电/满电）→ 复位"已提醒"标记，下次跌破再提醒一次。
        // 旧实现记的是"精确百分比且永不复位"，导致 20% 提醒后 19% 又提醒（逐 1% 刷屏），
        // 而充满到 100% 再放电到 20% 反而不提醒（CR-15）。
        if (percent > threshold || DeviceStatusFormatter.IsChargingOrFull(status.Charging))
        {
            _lowBatteryNotified.TryRemove(status.Id, out _);
            return;
        }

        // 骤降保护：键盘休眠/关机瞬间的伪读数（1%/0%）会让电量瞬间"掉"到阈值以下。
        // 真掉电不可能一次掉 20 个点，所以这种降幅先不提醒，等下次读数确认。
        if (DeviceChangeDetector.IsImplausibleDrop(previousPercent, percent, ImplausibleDropPoints))
        {
            Log.Warn($"低电量提醒暂缓: {status.DisplayName} {previousPercent}%→{percent}% 降幅异常，等下次读数确认");
            return;
        }

        // 跨过阈值只提醒一次（TryAdd 兼任"未提醒过"的判定，天然幂等）
        if (_lowBatteryNotified.TryAdd(status.Id, true))
        {
            LowBatteryDetected?.Invoke(status.Clone());
            Log.Info($"低电量提醒: {status.DisplayName} {percent}%");
        }
    }

    public void Dispose()
    {
        lock (_pollSync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        lock (_stateSync) _remainingTimeEstimates.Clear();
        _recovery.Dispose();
        _pollTimer?.Dispose();
        _sampleTimer?.Dispose();
        _discoveryTimer?.Dispose();
        _cleanupTimer?.Dispose();
        foreach (var p in _plugins)
        {
            p.StatusPushed -= Upsert;
            if (p is BatteryPluginBase recoverable) recoverable.RecoveryNeeded -= OnHidRecoveryNeeded;
            try
            {
                if (p is BatteryPluginBase stoppable) stoppable.BeginShutdown();
            }
            catch (Exception ex) { Log.Error($"插件 {p.Name} 停止 I/O 失败", ex); }
        }
        _ble.DeviceUpdated -= Upsert;
        _classic.DeviceUpdated -= Upsert;
        _system.DeviceUpdated -= Upsert;
        foreach (var release in new Action[] { _ble.Dispose, _classic.Dispose, _system.Dispose })
        {
            try { release(); }
            catch (Exception ex) { Log.Error("设备监视器退出收尾失败", ex); }
        }
        _scanSlot.Stop();
        _bleSlot.Stop();
        _classicSlot.Stop();
        _systemSlot.Stop();
        foreach (var plugin in _plugins) _ = DisposePluginAsync(plugin);
    }
}
