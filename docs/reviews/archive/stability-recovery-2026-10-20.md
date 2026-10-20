# 日常稳定性：锁屏、休眠与设备重识别审查

## 1. 基线与边界声明

- 基线：`11ece78ee739ac0a30ffb42de7be47197091f513`，分支 `add-peripheral-battery-support`。
- 本报告记录先行的稳定性审查阶段；该阶段仅静态审查与读取资料，未改产品代码、执行本地测试、重启应用、触发锁屏/睡眠或向硬件发帧。之后用户确认的贴片左右位置改动另见 `taskbar-position-2026-10-20.md`。
- 用户后续要求不考虑罗技支持，罗技独立插件只保留已发现缺口的证据，不进入本轮实施建议。
- 本机 `cmd /c ver` 返回 `10.0.26300.9457`，旧 handoff 中的系统版本不是本轮实测版本。
- 此前 CI 的 Windows / Ubuntu 各 599 项通过，不能据此认定系统睡眠、真实驱动移除或 WinRT 生命周期已验证。测试项目没有链接 DeviceManager 和两个蓝牙 watcher；PluginReconnectTests 使用临时文件句柄和 FakePlugin。
- 下面区分可从代码确认的缺口、可能影响与尚未复现的硬件表现。P1 表示优先处理的监控中断/错误在线风险，P2 表示身份连续性和读数可信度问题，不代表每条已在真机触发。

## 2. 逐条发现

### CR-78 / P1：恢复流程依赖普通定时器

证据：`DeviceManager:Start/StartTimers/RequestDiscoveryAsync`；`App.xaml.cs`；全仓未找到电源恢复、会话锁定/解锁和 HID 插拔通知的注册及处理。

当前有 60 秒轮询，以及仅对声明支持的插件执行的 15 秒发现；这些节拍没有表达“系统刚恢复、驱动尚未准备好”的状态。快速发现也不会重建已知设备的句柄。

建议：统一接收电源恢复、解锁和设备变化信号，合并事件后触发健康检查；恢复后分阶段重扫（例如 1/3/10/30 秒，属于待调参数），每设备去重、遵守现有写入授权与退避。只有锁屏时继续正常监控；解锁本身不应强行清空设备或让所有插件重握手。传统睡眠与 Modern Standby 分开验证。

### CR-79 / P1：路径仍在不等于旧句柄仍可用

证据：`HidDeviceHandle:ReadInputAsync` 将读异常与超时都转成 null；`BatteryPluginBase:AttachAsync` 遇到已有路径且没有 NeedsReprobe 就直接返回；生产代码中 RequestReprobe 的调用只出现在 Haute 的身份变化分支。

影响推断：驱动恢复后保留相同设备路径而使原句柄失效时，反复扫描可能仍复用坏句柄。Haute 已有单独的重开逻辑，其他基类插件没有通用入口。

建议：返回带类别的 I/O 结果，区分暂时无数据、明确离线、句柄失效、访问冲突和协议不匹配；失效时停止该句柄任务、释放认领，有界重开并核实身份。不能把设备睡眠导致的普通超时一律当作坏句柄，也不能用提高写入频率代替恢复。

### CR-72 / P1：单个调用会拖住共享监控轮次

证据：`DeviceManager:RunExclusiveAsync/PollOnceAsync/RequestPollAsync/RequestDiscoveryAsync`；`HidDeviceHandle:GetFeature/WriteOutput`。

整轮一直持有 _passGate，全部 Attach 等 Task.WhenAll 后才发布结果；插件 Poll 逐个同步执行，BLE 补读也在同一轮中等待。GetFeature 直接执行同步本机调用；WriteOutput 的等待超时并未取消底层写任务。一个慢插件至少会延迟其他插件；若某调用不返回，则新的轮询与发现会持续复用未完成任务。

[上游 issue #11](https://github.com/evren-oct/vigil-watt/issues/11) 报告断电后进程存活但监控冻结；它提出的 ReadInput 锁死机制仍是推断。本轮不能据此断言当前 Task.WhenAny 超时一定失效。

建议：按设备/插件隔离健康度与轮次预算，先发布已完成结果；对底层 I/O 明确取消/关闭和旧任务退出协议。不得只在 Task.Run 外加 WaitAsync 后就放开锁、继续复用同一可变插件和句柄：超时不意味着原操作结束。无法安全取消的调用要隔离；只有确实需要时才考虑独立进程边界。

### CR-80 / P1：蓝牙监视器失败后没有重建路径

证据：`BleBatteryWatcher:Start/Dispose`、`BluetoothClassicWatcher:Start/Dispose`。

两个 Start 都在创建 watcher 前设置 _started=true；全部属性集创建失败后直接返回，后续 Start 会立即退出。只订阅 Added/Updated/Removed，没有 Stopped 处理和状态恢复。由此可确认初始化失败后没有重试；系统恢复后是否实际 Aborted 需要真机验证。

建议：启动成功才提交运行状态，监听停止/异常状态并退避重建，区分主动停止与异常停止；解绑旧事件，完成停止后再启动。不能只修改一个布尔位后无条件调用 Start。

### CR-81 / P1：旧异步结果可能覆盖新连接状态

证据：`BleBatteryWatcher:ReadBatteryViaGattAsync/HandleRemoved`、`BluetoothClassicWatcher:VerifyConnectionAndAddAsync/HandleDevice`。

BLE 读数跨多个 await 后直接修改捕获的 status 并置在线，没有确认设备是否已经被移除或进入新连接。经典蓝牙连接确认完成后也直接调用 HandleDevice，没有废弃旧确认的标记。按代码时序，断连事件可以先到、旧读取/旧确认随后成功，导致错误恢复在线；本轮未通过真实 WinRT 时序复现。

建议：每设备维护连接代次或会话令牌，断连、移除、重建和退出时递增/取消；提交结果前校验仍是同一代连接。取消失败也必须丢弃旧结果，不能仅靠 CancellationToken 假定操作已停止。

### CR-82 / P2：连接状态与电量新鲜度混在一起

证据：`DeviceStatus:LastUpdated/IsOnline`、`BatteryPluginBase:TryApplyReading/MarkOffline`、`DeviceManager:SafeSample`、`HistoryStore:Insert`。

LastUpdated 既记录读数，也在标离线时更新；相同电量读数不总是立即向管理器推送。SafeSample 只看在线与电量是否存在，Insert 又以 LastUpdated 为样本时间。当前字段不足以回答“刚读到 60%，还是一小时前的缓存 60%”。

建议：分别记录最后有效电量、连接证据与状态变更时间；恢复时保留历史读数但明确待确认/过期语义，历史只写真实有效样本。对稀疏推送协议设独立策略，不能统一按短时间无报文判离线。界面呈现另出方案，不在本轮直接改 UI。

### CR-83 / P2：物理设备身份仍不普遍稳定

证据：`BatteryPluginBase:BuildDeviceId`、`MchosePlugin:BuildDeviceId`；对照 `HautePlugin:ReadDeviceId`。

多数基类插件仍按接口路径哈希生成 ID；Haute 芯片 ID 的修复没有使其他设备自动获得稳定身份。路径变化会拆分条目、历史和自定义名；同型号设备不能靠名字归并。

建议：按协议优先采用已验证的设备序列号或芯片身份，接收器身份与其下设备身份分开建模；ContainerId 等系统信息只能作为经过验证的辅助，不承诺跨接口/端口/驱动始终稳定。证据不足保守保留分立条目，不能猜测迁移旧历史。

### CR-84 / P1：罗技移除时没有上报离线副本

本条在后续范围收敛时排除，按用户要求暂不修复，已归档为 wontfix；下面保留原发现证据。

证据：`LogitechPlugin:AttachCore` 移除 session 后只修改 gone.Devices 的状态；既没加入 claimed，也没调用 StatusPushed。`DeviceManager:Upsert` 保存 Clone，之后 Poll 不再遍历已删除 session。

这是可从代码确认的状态传播缺口：管理器副本可能保持在线。之前基类重连修复未覆盖独立实现 IBatteryPlugin 的罗技路径。

建议：先发布带时间戳的离线快照，再释放并移除会话；增加罗技专用的假 I/O 生命周期回归。本轮不据此扩大到所有罗技固件都不能重连。

## 3. 需要修正或遗漏的部分

- CR-72 不应继续用“风险高于收益”降低优先级；其故障影响面覆盖全部监控，但修复必须先有可复现的故障注入。
- 目前的重连测试证明了受测基类路径的状态和资源释放，不覆盖整个 DeviceManager、真实句柄取消和蓝牙 watcher 失败恢复。
- 系统锁屏、系统睡眠/休眠、外设自行睡眠、接收器拔插是四种不同场景；不能用一次 Detach/Attach 回归代替四类验收。
- 复用已有熔断、已知协议与芯片身份模式；不做新命令扫描，不关闭 Windows 电源管理来掩盖应用恢复问题。

## 4. 未能证实的部分与验证计划

先将调度/恢复决策从 WinRT 和真实 HID 分离，注入时钟与 I/O，在现有 CI 中添加：

| 场景 | 必须验证的结果 |
|---|---|
| 相同路径、原句柄失效、新句柄可用 | 有界恢复，同身份一条记录，旧句柄/认领释放 |
| 一台设备超时或调用不返回 | 其他设备继续更新，手动刷新不永久等候，无并发复用坏会话 |
| 蓝牙 watcher 首次创建失败、运行后中止 | 按退避重建；主动停止不被自动重启 |
| 断连后旧 GATT/连接确认结果晚到 | 不能覆盖离线或新连接状态 |
| 睡眠恢复与解锁事件连续到达 | 恢复请求合并，有限重扫；没有重复监听、写入风暴或新条目 |
| 接收器移除（本轮支持范围内） | 管理器获得离线快照；重连恢复同身份状态 |
| 同型号双设备、USB 端口变化 | 不按名称误合并；身份和历史按证据关联 |
| 反复 100 次模拟断连/重连 | 后台任务、句柄、认领数回到基线，退出后不再推送 |

然后由用户安排真机验收：只锁屏 30 分钟、整机睡眠/休眠后恢复、Modern Standby（设备支持时）、外设睡眠 3/30 分钟后唤醒、接收器拔插/换口、蓝牙开关恢复。每类记录恢复耗时、最后成功读数、恢复原因与次数；具体时间指标先按协议定义，不能把所有设备都承诺为固定 3 秒。

普通 GitHub hosted runner 验证故障注入与调度逻辑，不能替代上述真机、电源模式和驱动生命周期验证。本轮没有执行这些场景。

## 5. 结论去向

CR-72 已补充代码证据；CR-78–CR-83 已登记到 `docs/reviews/BACKLOG.md`，原 CR-74 保持观察状态；CR-84 随用户排除罗技支持归档为 wontfix。

建议实施顺序：先建立故障注入与连接代次保护，修复坏句柄恢复及蓝牙监视器重建；再接入统一恢复调度，并处理全局阻塞隔离；随后补齐身份连续性与读数新鲜度。结构拆分围绕这些恢复边界进行，不为文件行数单独大改。

参考：[Windows 睡眠恢复事件](https://learn.microsoft.com/en-us/windows/win32/power/system-wake-up-events)、[会话锁定/解锁通知](https://learn.microsoft.com/en-us/windows/win32/termserv/wm-wtssession-change)、[DeviceWatcher 状态与重启要求](https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.devicewatcher?view=winrt-26100)。
