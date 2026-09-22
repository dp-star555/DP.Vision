# 图像采集连接架构 V2 实施状态

实施基线：[ACQUISITION_CONNECTION_V2_PLAN.md](ACQUISITION_CONNECTION_V2_PLAN.md)（Approved design，分阶段 V2-0..V2-9）。

## V2-0：冻结证据与合并在研改动

状态：**已完成**（2026-09-21）

### 证据基线

| 项 | 值 |
|---|---|
| DP.Vision HEAD | `1361204` feat(halcon): 采集深化V1-E——真实流式长连接适配器 |
| DP.WorkFlow HEAD | `1cffe71` refactor(AR-21): 删除旧通用图像编辑页残留双轨 |
| DP.Vision 工作区 | 干净（仅剩未跟踪的 V2 计划文档，已入库本记录后为空） |
| 测试基线 | `dotnet test DP.Vision.sln -c Debug -f net8.0-windows`：**480 通过 / 0 失败** |

net8.0-windows 分套件：

- DP.Vision.Tests 115 · DP.Vision.Acquisition.Tests 110 · DP.Vision.Halcon.Tests 102
- DP.Vision.Basler.Tests 82 · DP.Vision.Algorithms.Tests 67 · DP.Vision.Acquisition.Integration.Tests 4

### 处理记录

1. **HALCON 在研流式分支**：已由并发进程提交为 `1361204`（21 文件，+2723/-108），
   构建通过、含新增流式测试（StreamSession/NeutralFrames/Faults/TriggerMapping/DeviceStream + TestDoubles）。
   本阶段未重写任何同名 HALCON 文件。
2. **工作区来源**：V2 计划文档为新增文件；HALCON 在研改动确认后合并入库。
3. **OnDemand 与 BufferedExternal 基线测试**：
   - OnDemand：`tests/DP.Vision.Acquisition.Tests/Contracts/AcquisitionModeContractTests.cs` 等契约级测试。
   - BufferedExternal：`BufferedExternalInboxTests`、`StreamingFrameSinkContractTests`、
     `HalconAcquisitionDeviceStreamTests`、`HalconStreamSessionTests`、`BaslerAcquisitionDeviceStreamTests`。

## 阶段状态总览

| 阶段 | 状态 |
|---|---|
| V2-0 冻结证据与合并在研改动 | ✅ 已完成 |
| V2-1 AcquisitionTypeCatalog 与自动 Module 发现 | ✅ 已完成（见下文） |
| V2-2 机器相机定义与不可变 Composition | ✅ 已完成（见下文） |
| V2-3 应用级连接生命周期 | ✅ 已完成（见下文） |
| V2-4 TransferPolicy 与 Epoch 解耦 | ✅ 已完成（见下文） |
| V2-5 面阵/线扫双节点模型 | ✅ 已完成（见下文） |
| V2-6 Basler 迁移 | ✅ 已完成（见下文） |
| V2-7 HALCON 迁移 | ✅ 已完成（见下文） |
| V2-8 线扫与采集卡首个 Adapter | ✅ 已完成（见下文） |
| V2-9 Acquisition UI、审计和运行优化 | ✅ 已完成（V2-9a 服务端 + V2-9b 表现层与 WinForms 控件，见下文） |
| V2-11 服务端收口遗留三项 | ✅ 已完成（见下文） |

每阶段完成时更新本文并记录提交、测试结果与验收证据。

## V2-1：AcquisitionTypeCatalog 与自动 Module 发现

状态：**已完成**（2026-09-21）

### 验收证据

| 验收（§21） | 证据 |
|---|---|
| DLL Module 自动发现且顺序确定 | `VisionAcquisitionDriverModuleLoaderTests.DiscoveryOrder_IsDeterministic` |
| 重复 AcquisitionTypeId 拒绝发布 | `VisionAcquisitionTypeCatalogComposerTests.DuplicateAcquisitionTypeId_IsRejected` |
| 不读取机器相机配置列出已安装 Type | `VisionAcquisitionDriverModuleLoaderTests.CatalogTypes_AreListableWithoutMachineConfiguration` |

### 新增契约与实现

Abstractions（公共契约层）：

- `EVisionAcquisitionKind`：`AreaScan`/`LineScan`，面阵/线扫节点 Source 下拉过滤依据。
- `IVisionAcquisitionDriverModule`：Driver Module 入口（`ExtensionId` + `Contribute`）。
- `IVisionAcquisitionTypeContributionBuilder` + `VisionAcquisitionTypeRegistration`（候选注册）+
  `VisionAcquisitionTypeCapabilities`（自由运行/软件触发/外部触发/完整帧回调能力集）。

Runtime（实现层）：

- `VisionAcquisitionTypeDescriptor`：冻结后不可变的 Type 描述。
- `VisionAcquisitionTypeCatalog`：一次 Freeze 的 Catalog；`TryGetType`、`GetByKind`、`CatalogId`/`Manifest`。
- `VisionAcquisitionTypeCatalogComposer`：候选贡献 → 完整验证 → 一次 Freeze；
  校验重复 TypeId/Module 身份、空工厂、未知配置版本、能力一致性（外部触发必须带完整帧回调）、无取图路径拒绝。
- `VisionAcquisitionDriverModuleLoader`：扫描受信任插件目录递归发现 `IVisionAcquisitionDriverModule`；
  Manifest 不作为加载依据；原生 SDK 依赖 DLL 静默跳过、托管依赖无 Module 静默跳过、失败被报告但不阻断。

厂商贡献：

- `HalconAcquisitionDriverModule`：`dp.acquisition.halcon.area`（外部触发/完整帧回调按 SDK 编译开关声明）。
- `BaslerAcquisitionDriverModule`：`dp.acquisition.basler.area`。
- LineScan：`dp.acquisition.test.line` 测试 Type 先允许冻结（测试程序集）。

禁止依赖保持：厂商 Adapter 不反向引用 DP.WorkFlow；公共采集契约不引入厂商 SDK；机器配置不引用 CLR 完整类型名。

### 测试结果

`dotnet test DP.Vision.sln -c Debug -f net8.0-windows`：**504 通过 / 0 失败**（基线 480 + 新增 24）。

分套件增量：

- DP.Vision.Acquisition.Tests 110 → 130（+20：Catalog Composer 14 · Driver Module Loader 6）
- DP.Vision.Halcon.Tests 102 → 104（+2：Driver Module 贡献与插件目录扫描）
- DP.Vision.Basler.Tests 82 → 84（+2：Driver Module 贡献与插件目录扫描）

其余套件（DP.Vision.Tests 115、Algorithms 67、Integration 4）不变。

## V2-2：机器相机定义与不可变 Composition

状态：**已完成**（2026-09-21）

### 需求覆盖（§20）

1. **版本化 CameraDefinition 文档**：`VisionAcquisitionCameraDefinition`（`DocumentFormatVersion = 1`），
   构造器校验身份/设置版本，并强制"OnConnect 必须带 Inbox、PerRequest 不接受 Inbox"。
   `VisionAcquisitionMachineConfigurationParser` 严格解析单对象或数组，未知公共字段一律拒绝，`deviceSettings` 以原始 JSON 保留。
2. **Plugin 解析自己的 deviceSettings**：`IVisionAcquisitionDriverModule` 注册新增可选 `DeviceSettingsParser`，
   Catalog 冻结前拒绝缺解析器的 Type；Basler/HALCON 各自实现解析器生成内部绑定身份、规范 ResourceKey 与配置摘要。
3. **Validated Binding 与规范 ResourceKey**：`VisionAcquisitionMachineConfigurationComposer` 调 Plugin 解析器，
   生成 `VisionAcquisitionSourceBinding`（`camera:serial:xxx` / `camera:name:xxx` / `halcon:camera:iface|device`）。
4. **Composition 直接投影 SourceCatalog**：`VisionAcquisitionProviderComposition` 实现 `IVisionAcquisitionSourceCatalog`；
   WorkFlow 侧 `WorkflowVisionSourceCatalog.FromAcquisition(...)` 一键投影（含保真不可用条目）。
5. **私有配置摘要进入 CompositionId**：`ComputeCompositionId` 追加按 sourceId 排序的 `settings|sourceId|summary` 行。
6. **样例迁移**：WinForms / Legacy WPF 删除手工 `sourceBindings` 与重复 SourceCatalog 构造，
   改为"DriverModuleLoader → TypeCatalogComposer → 内嵌机器配置 JSON → MachineConfigurationComposer → FromAcquisition"；
   删除 `vision-providers` 私有配置投放与其 JSON。

### 验收证据

| 验收（§21） | 证据 |
|---|---|
| 一份机器配置生成唯一 Composition 和 SourceCatalog（完成条件） | `VisionAcquisitionMachineConfigurationTests.SingleCamera_ProjectsUniqueCompositionAndSourceCatalog` |
| 未安装 Type 保真且 Source 不可用（§21.3） | `UninstalledType_IsPreservedUnavailable`；WorkFlow 侧 `未安装Type保真投影为不可用条目` |
| 私有配置变化改变 CompositionId（§21.26） | `PrivateSettingsChange_ChangesCompositionId` |
| settingsVersion 不一致拒绝 | `SettingsVersionMismatch_IsRejected` |
| OnConnect/PerRequest 缓冲策略约束 | `OnConnectWithoutInbox_IsRejected` · `PerRequestWithInbox_IsRejected` · `OnConnectWithoutCallbackCapability_IsRejected` |
| 同一 Type 多源共享 Provider 注册 | `MultipleSourcesOnSameType_ShareProviderRegistration` |
| Plugin 私有字段校验（歧义选择器/必填缺失/未知字段） | `BaslerDeviceSettingsParserTests` · `HalconDeviceSettingsParserTests` |

### 新增契约与实现

Abstractions（公共契约层）：

- `EVisionAcquisitionTransferStart`（`PerRequest`/`OnConnect`）+ `VisionAcquisitionConnectionPolicy`。
- `VisionAcquisitionCameraDefinition`（版本化相机定义）。
- `VisionDeviceSettingsParseResult`（Plugin 解析结果：绑定身份/规范资源键/配置摘要）。
- `VisionAcquisitionSourceInfo` + `IVisionAcquisitionSourceCatalog`（Composition 直接投影的目录契约）。

Runtime（实现层）：

- `VisionAcquisitionMachineConfigurationParser`（公共字段严格解析，deviceSettings 原样保留）。
- `VisionAcquisitionMachineConfigurationComposer`（Catalog × CameraDefinition → 不可变 Composition；
  未安装 Type 保真不可用不阻断；共享 Provider 注册；settingsVersion/策略/回调能力校验）。
- `VisionAcquisitionProviderComposer.Compose(providers, sources, sourceInfos, summaries)` 重载 + 摘要进 CompositionId。
- `VisionAcquisitionProviderComposition` 实现 `IVisionAcquisitionSourceCatalog` 并暴露 `GetConfigurationSummary`。

厂商与样例：

- `BaslerDeviceSettingsParser`（serial XOR name 必选一）/ `HalconDeviceSettingsParser`（interface+device 必填、默认 5s 超时）。
- 两个 DriverModule 注册尾部接入各自 Parser。
- WorkFlow：`WorkflowVisionSourceCatalog.FromAcquisition`；两个样例迁移到机器配置流程。

### 测试结果

`dotnet test DP.Vision.sln -c Debug -f net8.0-windows`：**532 通过 / 0 失败**（基线 504 + 新增 28）。

分套件增量：

- DP.Vision.Acquisition.Tests 130 → 144（+14：MachineConfiguration 13 · MissingDeviceSettingsParser 1）
- DP.Vision.Halcon.Tests 104 → 111（+7：HalconDeviceSettingsParser）
- DP.Vision.Basler.Tests 84 → 91（+7：BaslerDeviceSettingsParser）

WorkFlow 侧（独立仓库）：`DP.WorkFlow.Nodes.Vision.Acquisition.Tests` +2 通过（Composition 投影）；两个样例工程构建 0 警告 0 错误。

### 记录

- 样例的 SDK 健康可用性（真实设备在不在线）在 V2-2 中由"配置级可用性"（Type 已安装且解析成功）替代；
  Provider 打开路径（OpenAsync 带绑定）按计划推迟到 V2-3/V2-6/V2-7 迁移，本阶段只完成组合层。

## V2-3：应用级连接生命周期

状态：**已完成**（2026-09-21）

### 需求覆盖（§20）

1. **Runtime 增加 StartAsync/Ready/StopAsync**：`VisionAcquisitionRuntime` 新增 `StartAsync`（按 ResourceKey 去重打开设备，同一物理资源只打开一次）、
   `StopAsync`（原 DisposeAsync 关闭顺序：先关接受门、再停流清退未领取帧、等在途操作退出、释放唯一 SDK 设备对象、释放 Provider；Stopped 幂等）、
   `RuntimeState`/`IsReady`/`IsDegraded`。DisposeAsync 委托给 StopAsync。
2. **Provider OpenAsync 真正打开设备**：设备连接属于软件生命周期，只由 Runtime Start 打开；`GetOrOpenDeviceAsync` 在会话已有设备时直接复用。
3. **一个 ResourceKey 一个 Device Adapter 与一个 SDK 对象**：多个 Source 有意映射同一 ResourceKey 时共享同一设备与 Provider 实例。
4. **OnDemand 复用已连接对象**：`CaptureAsync`/`BeginRunAsync` 严格要求 Runtime 已启动（未启动抛 `VisionDeviceOfflineException` 并提示调用 StartAsync）；
   `CaptureOnDemandAsync` 删除惰性打开，节点不得隐式 Open/Reconnect/关闭设备。
5. **ConnectionState 与连接诊断**：新增 `EVisionConnectionState`（Created/Connecting/Connected/Faulted/Disconnecting/Disposed）与 `EVisionRuntimeState`
   （Created/Starting/Ready/Degraded/NotReady/Stopped）；`VisionSourceDiagnostics` 追加尾部参数 `ConnectionState`/`ConnectionMessage`（默认值保持兼容），
   连接成功报告设备规范身份、失败报告原因；`VisionAcquisitionSourceBinding` 追加 `IsRequired`（可选参数默认 true，机器配置可声明）。
6. **宿主进入可运行状态前启动 Runtime**：Required 源打开失败 → `NotReady`；Optional 源失败只降级 `Degraded` 且该 Source 保留完整诊断；
   WinForms / Legacy WPF 样例构造 Runtime 后立即 `StartAsync`，并将未就绪状态并入 Source 诊断；WorkFlow 端到端测试装配同步。

### 验收证据

| 验收（§21） | 证据 |
|---|---|
| 同一 ResourceKey 只创建一个 Device（§21.4） | `RuntimeConnectionLifecycleTests.SameResourceKey_StartsSingleDevice` |
| Runtime Start 真实 Open 一次（§21.5） | `MultipleCaptures_OpenOnceAndNeverCloseUntilStop` |
| 多次 OnDemand Capture 不重复 Open/Close（§21.6） | 同上：连续 3 次 Capture `OpenedBindings.Count` 恒为 1、Stop 前 `DisposeCount == 0`、Stop 后 `== 1` |
| 节点无法直接关闭 Device（§21.7） | `UnstartedRuntime_NodeCaptureFailsWithoutImplicitOpen` · `UnstartedRuntime_RootRunFailsWithoutImplicitOpen`（均断言 `Created.Count == 0`） |
| Required 设备失败时 Runtime 不 Ready（§21.8） | `RequiredFailure_RuntimeIsNotReady`（NotReady + Source Faulted + Capture 明确失败） |
| Optional 设备失败时 Source 不可用但 Runtime 可 Degraded（§21.9） | `OptionalFailure_RuntimeDegradesAndSourceUnavailable`（Degraded + 失败源 Faulted 保真 + Required 源仍可采集） |

### 新增契约与实现

Abstractions（公共契约层）：

- `EVisionConnectionState` / `EVisionRuntimeState` 枚举（状态机注释写明连接与取流分开维护、Ready/Degraded/NotReady 语义）。
- `VisionSourceDiagnostics` 追加 `ConnectionState`/`ConnectionMessage`（尾部带默认值的定位参数）。

Runtime（实现层）：

- `VisionAcquisitionSourceBinding.IsRequired`（机器配置 `camera.IsRequired` 透传，第 8 个可选参数）。
- `VisionResourceSession` 连接状态跟踪：`MarkConnecting`/`MarkConnected`、`MarkFaulted` 同步 Faulted 连接态、`DisposeAsync` 记录 Disconnecting→Disposed。
- `VisionAcquisitionRuntime.StartAsync`：Starting→按 ResourceKey 去重打开→`ComputeStartState`（Required 失败 NotReady，否则 Optional 失败 Degraded）；
  `StartResourceAsync` 失败不自动切换 Provider，按 `VisionSourceConfigurationException`/其他分别标记 SourceConfiguration/OpenFailure。
- `CaptureAsync`/`BeginRunAsync` 严格要求 `RuntimeState != Created`；`CaptureOnDemandAsync` 用 `session.Device` 直接复用并拒绝空设备。

### 测试结果

`dotnet test DP.Vision.sln -c Debug -f net8.0-windows`：**538 通过 / 0 失败**（基线 532 + 新增 6）。

- DP.Vision.Acquisition.Tests 144 → 150（+6：`RuntimeConnectionLifecycleTests`）。
- 其余套件不变：DP.Vision.Tests 115 · Integration 4 · Algorithms 67 · Basler 91 · Halcon 111。
- 既有测试按严格 Start 契约更新：SharingPolicyTests / RoutingTests 各用例补 `StartAsync`，
  `OpenFailure_ReleasesLease` 重写为 `OpenFailure_DuringStart_LeavesRuntimeNotReady`（NotReady 语义），
  `CanonicalKeyMismatch_IsRejected` 改为 Start 阶段拒绝（NotReady + Capture 抛 `VisionDeviceOfflineException`）；
  BufferedExternalInboxTests 的 Rig 构造即启动。

WorkFlow 侧（独立仓库）：`DP.WorkFlow.Nodes.Vision.Acquisition.Tests` 8 通过（`FakeStreamingRuntime` 构造即启动；
`运行准备校验失败时设备没有被布防` 改 V2-3 语义：OpenCount==1 且 StreamStartCount==0）；两个样例工程构建 0 警告 0 错误。

## V2-4：TransferPolicy 与 Epoch 解耦

状态：**已完成**（2026-09-21）

### 需求覆盖（§20 V2-4）

1. **PerRequest/OnConnect 策略**：`EVisionAcquisitionTransferStart`（V2-2 已定义）与 `EVisionAcquisitionMode` 在 Composer 中 1:1 映射
   （OnConnect ≡ BufferedExternal ≡ ExclusiveRun）。本阶段落实为行为解耦：TransferPolicy 决定"接收流何时布防"，
   Epoch 决定"哪些帧可领取"，两者不再耦合；"增加策略"不需要新增枚举。
2. **OnConnect 流在 Runtime Start 阶段启动**：`StartResourceAsync` 打开设备后对 `BufferedExternal` 源调用 `session.StartStreamAsync`，
   接收流布防一次、跨根运行保持（§21.10）；失败文案改为"打开设备或启动接收流失败"。
3. **BeginEpoch 只开放本代次接收/领取，不启动设备**：`BeginEpoch` 改为状态门（仅 Streaming/Armed 通过）+ 代次严格递增 +
   队列 `BeginEpoch` 清退旧代次并释放，不再打开设备、不再启动接收流。
4. **EndEpoch 清理本代次未领取帧，但不停流、不关设备**：新增 `EndEpoch`，移出并释放本代次未领取帧、回到
   Streaming（流运行、无活动代次）；退役 `RunLease.DisposeAsync` 只调 `EndEpoch`，删除 `DisarmAsync`。
5. **无 ActiveEpoch 完整帧释放并计数**：`VisionFrameInbox` 持有活动代次；无代次时 `TryEnqueue` 以 `NoActiveEpoch` 拒绝、
   释放、单独计数（`RejectedWithoutEpochCount`），属正常间隔隔离，不触发 `FaultSource`；仅 `Overflow` 仍 `MarkFaulted`。
6. **Runtime Dispose 固定顺序停流和关设备**：`DisposeAsync` 保持 停流（等待回调退出）→ 排空 → 等在途操作 → 关设备。

### 验收证据（§21）

| 验收（§21） | 证据 |
|---|---|
| 完成条件：第二根运行不重新 Open、不重新创建 SDK 对象 | `SecondRun_ReusesOpenDeviceInsteadOfOpeningCameraAgain`（StreamStartCount==1、OpenedBindings==1、DisposeCount==0）；WorkFlow `上一根运行未领取的帧不会进入下一根运行`（StreamStartCount==1、OpenCount==1） |
| OnConnect 流由 Runtime Start 布防、先于第一根运行（§21.10） | `OnConnectStream_ArmedAtRuntimeStart_BeforeFirstRun`（State==Streaming、StreamStartCount==1、无代次帧拒绝计数） |
| Epoch 隔离仍成立：旧代次帧不进新代次 | `BeginEpoch_DropsPreviousEpochFrames` · `PreviousRunFrames_DoNotEnterNextRun` · WorkFlow `上一根运行未领取的帧不会进入下一根运行` |
| 退役不停流、不关设备（§21.15/17） | `RunEnd_KeepsStreamRunningAndRejectsFramesWithoutEpoch`（StreamStopCount==0、DisposeCount==0、IsStreaming）· `EndEpoch_DrainsUnclaimedFramesAndRecordsCount`（UnclaimedAtEpochEnd==1）· WorkFlow `根运行退役后接收流保持运行且无代次帧被释放` |
| 无活动代次帧被释放并计数、不触发故障 | `Enqueue_WithoutActiveEpoch_RejectsAndCounts` · 退役用例（FramesRejectedWithoutEpoch==1 且 IsFaulted==false） |

### 新增/变更契约与实现

- `EVisionResourceState` 新增 `Streaming = 6`（保留原枚举值）：流运行、无活动代次，可接收（无代次帧拒绝计数）但不可领取。
- `VisionResourceSession`：`ArmAsync(openDevice, ct)` → `StartStreamAsync(ct)`（幂等、要求设备已打开、校验流式能力）；
  `BeginEpoch` 状态门 + 严格递增；新增 `EndEpoch`；删除 `DisarmAsync`；`PublishCore` 按 `reject` 区分 `Overflow`（FaultSource）
  与 `NoActiveEpoch`（正常间隔）；`EnsureClaimable` 无活动代次抛 `VisionDeviceOfflineException`；`Snapshot` 追加两个计数。
- `VisionFrameInbox`：Epoch 归队列所有（计划 §10.3，不是相机连接状态）；`TryEnqueue(frame, receivedAtUtc, out seq, out reason, out reject)`
  （去掉 epoch 参数）；`TryClaim(now)`（用内部 `_activeEpoch`）；`DropStaleEpochs` → `BeginEpoch(epoch)`（严格递增、清退旧代次、内部释放）+
  `EndEpoch()`（移出全部、计数未领取、清空活动代次）；新增 `VisionFrameInboxReject`（None/Overflow/NoActiveEpoch）。
- `VisionAcquisitionRuntime`：`StartResourceAsync` 对 BufferedExternal 布防接收流；`RunLease.ArmAsync` 改为非 async
  （只 `BeginEpoch` + 登记，避免 CS1998），`DisposeAsync` 改 `EndEpoch`；删除 `ResolveRegistration`。
- `VisionSourceDiagnostics` 追加尾部可选参数 `FramesRejectedWithoutEpoch`/`UnclaimedAtEpochEnd`（默认值保持兼容）。

### 测试结果

`dotnet test DP.Vision.sln -c Debug -f net8.0-windows`：**541 通过 / 0 失败**（基线 538 + 净增 3）。

- DP.Vision.Acquisition.Tests 150 → 153：
  - `FrameInboxUnitTests` 按新 Inbox 契约重写（`BeginEpoch`/`EndEpoch`/无 epoch 参数），新增 `Enqueue_WithoutActiveEpoch_RejectsAndCounts`、
    `EndEpoch_DrainsAllEntriesAndCountsUnclaimed`、`BeginEpoch_RejectsNonIncreasing`；`Claim_RejectsStaleEpochEvenWhenInboxWasNotDrained` 删除（新 API 不可达），
    `Claim_DoesNotReturnFramesFromAnotherEpoch` 与 `DropStaleEpochs_KeepsOnlyCurrentEpoch` 合并为 `BeginEpoch_DropsPreviousEpochFrames`。
  - `BufferedExternalInboxTests` 新增 `OnConnectStream_ArmedAtRuntimeStart_BeforeFirstRun`、`EndEpoch_DrainsUnclaimedFramesAndRecordsCount`；
    `SecondRun_ReusesOpenDevice...`（StreamStartCount 2→1）与 `RunEnd_StopsStreamWhileRuntimeStaysAlive`（→ `RunEnd_KeepsStreamRunningAndRejectsFramesWithoutEpoch`）按 V2-4 语义重写。
- 其余套件不变：DP.Vision.Tests 115 · Integration 4 · Algorithms 67 · Basler 91 · Halcon 111。

WorkFlow 侧（独立仓库）：`DP.WorkFlow.Nodes.Vision.Acquisition.Tests` 8 通过；
`根运行退役后相机停流且不再交付回调` → `根运行退役后接收流保持运行且无代次帧被释放`（IsStreaming==true、StreamStopCount==0、after-run 帧被释放计数），
`上一根运行未领取的帧不会进入下一根运行` StreamStartCount 2→1，
`运行准备校验失败时设备没有被布防` → `运行准备校验失败时本轮没有活动代次`（StreamStartCount==1）；
`DP.WorkFlow.Nodes.Vision.Tests` 39 通过、`DP.WorkFlow.Runtime.Tests` 32 通过，无回归。

## V2-5：面阵/线扫双节点模型

状态：**已完成**（2026-09-21）· 仅 WorkFlow 仓库（DP.Vision 无改动）

### 需求覆盖（§20 V2-5）

1. **面阵节点**：`CaptureAreaFrameNodeModel`/`Handler`（`Vision.CaptureAreaFrame`，显示名"采集面阵帧"）、输出 `ImageFrame`。
2. **线扫节点**：`CaptureLineScanFrameNodeModel`/`Handler`（`Vision.CaptureLineScanFrame`，显示名"采集线扫帧"）、输出 `ImageFrame`。
   线扫节点不预设触发模式（触发时序属设备/Adapter 的工艺约定），`VisionCaptureRequest` 固定用 `KeepCurrent`。
3. **Source 候选按 AcquisitionKind 过滤**：`WorkflowVisionSourceInfo` 追加可选 `Kind`（`FromAcquisition` 从 `VisionAcquisitionSourceInfo.Kind` 投影），
   `WorkflowVisionSourceChoices.CreateProvider` 按编辑器键投影面阵/线扫两份候选；`Kind` 未声明的源在两个列表都保留并标注"采集类型未声明"（不猜测）。
4. **共享无状态执行与输出提交**：`VisionCaptureNodeExecution.ExecuteAsync` 承载"源非空校验 → `IVisionAcquisition.CaptureAsync` → 源事实 Trace → `LoadVisionFileNodeHandler.Output` 提交"，
   两个 Handler 均为单行转发，没有共享基类。
5. **~~旧 `Vision.CaptureFrame` 迁移器~~（偏离，按用户决定取消）**：用户明确要求"彻底抛弃旧兼容，直接删除掉"，
   因此删除旧节点 `CaptureVisionFrameNode.cs` 与持久化层旧兼容迁移（`MigrateCaptureNodeConfig`/`MigrateLegacyPhysicalQuantity`/`CreateEnumValueNode` 共 ~73 行）。
   旧文档中的 `Vision.CaptureFrame` 不再被静默转换，而是按未注册节点降级为 `UnknownWorkflowNodeModel`、`RawConfig` 保真保留，
   载入时给出迁移警告、编译/绑定时明确报错，不产生静默行为改变。
6. **WinForms/WPF PropertyGrid 回归**：`VisionFrameEditorPage` 预览页与两个样例的 ChoiceProvider 同步接入；
   `WorkflowPropertyInspectorModel.IsChoiceEditor` 识别两个新编辑器键，属性面板按节点自身的键取候选。

完成条件达成：两个节点输出相同的 `ImageFrame`，无厂商类型进入 Workflow。

### 验收证据（§21）

| 验收（§21） | 证据 |
|---|---|
| 面阵/线扫节点均输出中立 `ImageFrame`（§21.21/22） | `VisionModuleTests.Module_RegistersOnlyNewContractsAndUniqueHandlers`（两节点 `OutputType == typeof(ImageFrame)`）· `VisionAcquisitionNodeTests.线扫节点输出与面阵节点相同的中立ImageFrame` |
| Source 候选按类型过滤（§21.23） | `NewVisionCompletionTests.VisionSourceChoices_FilterCandidatesByAcquisitionKind`（面阵列表 = 面阵源 + 不可用面阵源 + 未声明源；线扫列表 = 未声明源 + 线扫源；未知键返回空） |
| 不匹配类型在首节点执行前被拒绝（§21.24） | `线扫节点绑定面阵源时在首节点前拒绝` · `面阵节点绑定线扫源时在首节点前拒绝`（均断言首节点未执行、异常文案含面阵/线扫语义） |
| 形态未声明的源不做类型拒绝（不猜测） | `形态未声明的源不做类型拒绝` |
| 属性面板按节点键区分候选（§21.25） | `WorkflowPropertyInspectorTests.Inspector_UsesKindSpecificChoiceEditorsForCaptureNodes`（键原样传给 provider、面阵有 TriggerMode 且线扫没有） |
| 旧节点不再静默转换（偏离后的替代验收） | `VisionModuleTests.OldNodeType_IsNotSilentlyConvertedOnLoad` · `旧采集节点类型未注册时按未知节点保真保留配置`（`UnknownWorkflowNodeModel` + CameraId/Exposure/Gain/Triggered 全保真） |
| Type 形态投影 | `WorkflowVisionSourceCatalogProjectionTests.线扫Type投影为线扫形态` |

### 新增/变更契约与实现

新增（WorkFlow）：

- `Nodes.Vision/Acquisition/CaptureAreaFrameNode.cs` · `CaptureLineScanFrameNode.cs` · `VisionCaptureNodeExecution.cs`（共享执行主干，internal static）。
- `Vision.UI/Editors/WorkflowVisionSourceChoices.cs`（按编辑器键投影候选）。
- `WorkflowVisionSourceInfo.Kind`（尾部可选参数，既有 6 参位置调用行为不变）。

删除（WorkFlow）：

- `Nodes.Vision/Acquisition/CaptureVisionFrameNode.cs`（旧 `Vision.CaptureFrame`）。
- `WorkflowDocumentJsonStore` 中 3 个旧兼容迁移方法与调用点；旧文档降级为未知节点保真。

变更（WorkFlow）：

- `WorkflowPropertyEditorKeys`：`VisionSource` → `VisionAreaSource` + `VisionLineScanSource`；`IsChoiceEditor` 同步。
- `WorkflowVisionFrameScope`：重复 ID 检查与 `ValidateCaptureNodes` 改为枚举两种采集节点的 `CaptureCandidates`，
  并在源 `Kind` 与节点 `RequiredKind` 不一致时拒绝（`Kind` 为 null 放行）；`CreateRequest` 延迟构造，使绑定错误先于参数错误暴露。
- `WorkflowImageRuntimePluginModule`：注册两个节点与两个 Handler。
- `VisionFrameEditorPage`、WinForms 样例 `Form1.cs`、Legacy WPF 样例 `MainWindow.xaml.cs` 同步。

### 测试结果

`dotnet test DP.WorkFlow.sln`：**843 通过 / 0 失败**（全部 14 个测试工程；`tests/Platform/ModernUI.*` 不在解决方案内）。

相关套件：

- `DP.WorkFlow.Nodes.Vision.Tests` 39 → 43（+4：线扫输出等价、两个跨类型拒绝、形态未声明放行；旧迁移用例改为未知节点保真）。
- `DP.WorkFlow.Nodes.Vision.Acquisition.Tests` 8 → 9（+1：线扫 Type 形态投影；`BufferedExternalRunScopeEndToEndTests` 改用面阵节点）。
- `DP.WorkFlow.UI.Shared.Tests` 62 → 63（+1：属性面板按节点键区分候选）。
- `DP.WorkFlow.UI.Windows.Tests` 352 通过（`VisionSourceChoices_FilterCandidatesByAcquisitionKind` 等按新节点迁移；无回归）。
- 其余套件不变：Core 46 · Standard 48 · Process 56 · Composite 7 · Motion 12 · Persistence 10 · Runtime 32 · ScriptEngine 各套件不变。

DP.Vision 侧无改动，仍为 V2-4 的 `dotnet test DP.Vision.sln -c Debug -f net8.0-windows`：541 通过 / 0 失败。

## V2-6：Basler 迁移

状态：**已完成**（2026-09-21）· 仅 DP.Vision 仓库

### 需求覆盖（§20 V2-6）

1. **OnDemand 与 Callback 共用同一个已连接 `Camera`**：`BaslerAcquisitionDevice` 只保留一个相机字段 `_camera`，
   由 `_cameraFactory` 在**第一次被用到时创建一次**；`CaptureAsync` 与 `StartStreamAsync` 都取用同一个对象
   （`_camera ??= _cameraFactory(_binding)`），设备不再区分"单次采集用的相机"和"布防用的相机"。
   相机写入入口收敛为契约 `IBaslerStreamCamera`：主动单次采集 `CaptureSingleFrame` 与持续取流 `StartContinuousGrab`
   都在这一个设备侧对象上发生，`pylon` 的 `StreamGrabber` 只有一条通道。
2. **删除每次 Capture 的 `new Camera/Open/Close`**：`BaslerAcquisitionDevice.CaptureAsync` 不再有 `#if BASLER_SDK` 分支，
   也不再构造 / 打开 / 关闭任何设备；原 `Grab(Camera, …)` 中"新建相机 → Open → 抓图 → Close"整段（约 90 行）已删除，
   抓图职责迁移到 `PylonStreamCamera.CaptureSingleFrame`。相机只在设备释放时 `Close` 一次。
3. **PerRequest 与 OnConnect 不能同时创建两个 StreamGrabber 状态**：互斥由两层保证——
   设备适配器在 `_sync` 锁下拒绝"已作为缓冲源布防时再来单次采集"（`_session is not null`，断线时优先报断线原因）；
   反向的"单次采集在途时布防"由共享相机的单通道检查拦下（真实实现是 `StreamGrabber.IsGrabbing`，
   假相机在 `_grabArmed || _capturing` 时同样抛异常），失败发生在任何设备动作之前。
   `StopContinuousGrab` 只收尾本侧真正开始过的持续取流（`_onFrame is not null` 才停），不会误停他人取流。
4. **保留回调边界中立像素复制**：回调路径仍走 `BaslerNeutralFrames.Copy`，
   `PylonGrabFrame.ForCallback` / `ForRetrieved` 的释放语义区分未动；单次采集路径继续在返回前把像素落地为中立图像
   （`new VisionProviderFrame(BaslerNeutralFrames.Copy(grabFrame), …)`），不把 pylon 缓冲交给上层。
5. **验证关闭顺序和断线状态**：释放顺序仍是"先 `session.DisposeAsync()`（停流并等在途回调退出）→ 再 `camera.Dispose()`"；
   断线（`GrabSucceeded == false`）经 `onFailure` 结束会话、源标记故障且**不关闭相机**，
   此后 `CaptureAsync` 优先报 `VisionDeviceOfflineException` 并带上断线原因，而不是误报"正在布防"。

完成条件达成：真实 SDK 路径上同一台 Basler 设备只存在一个已连接 `Camera` 与一条取流通道，两种采集模式互斥且复用同一对象。

### 验收证据（§21）

| 验收（§21） | 证据 |
|---|---|
| 第二根运行复用同一个 SDK 对象（§21.18） | `BaslerAcquisitionDeviceStreamTests.ThreeRuns_ShareSingleConnectedCameraAndCloseOnce`（OpenCount==1、StartGrab==3、StopGrab==3、释放前 Close==0）· `SecondArm_ReusesOpenCameraInsteadOfOpeningAgain` |
| 多次 OnDemand Capture 不重复 Open/Close（§21.6，Basler 侧） | `CaptureAsync_ReusesSingleConnectedCameraAcrossRequests`（3 次采集 OpenCount==1、事件序列 `open,capture-single,capture-single,capture-single`） |
| Runtime Dispose 等待在途回调与在途 Capture 退出（§21.19） | `BaslerStreamSessionTests.Dispose_WaitsForInFlightCallback` · `StartStream_WhileCapturing_IsRejected`（单次采集在途时布防被拒且 `StartGrabCount==0`） |
| Shutdown 事件顺序：停流先于关设备（§21.20） | `DeviceDispose_StopsStreamBeforeClosingCamera`（`open,apply-parameters,start-grab,stop-grab,close`）· `DeviceDispose_AfterCaptureOnly_ClosesCameraOnce` · `DeviceDispose_IsIdempotent` · `DeviceDispose_WithoutArming_DoesNotTouchCamera` |
| 断线状态可解释且不关闭相机（§20.5） | `StreamFailure_EndsSessionWithoutClosingCamera` · `CaptureAsync_AfterStreamFailure_ReportsDisconnect`（消息含断线原因） |
| 单次采集失败后相机保持打开可重试 | `CaptureAsync_Failure_KeepsCameraOpenForNextRequest` |
| 两种模式互斥，不产生两个 StreamGrabber 状态（§20.3） | `CaptureAsync_WhileArmed_IsRejected` · `StartStream_WhileCapturing_IsRejected` · `SecondArm_WhileArmed_IsRejectedWithoutReplacingSink` |
| 回调边界中立像素复制保留（§20.4） | `BaslerStreamSessionTests.Frame_IsDeliveredAsNeutralImageWithDeviceSequence` · `Frame_WideMonoLandsOnGray16Layout` · `BaslerNeutralFramesTests.DirectFormat_StillGoesThroughDeviceConverter`（§21.28：交付后的 `ImageFrame` 不引用设备缓冲） |

### 变更文件

- `src/DP.Vision.Basler/BaslerStreamContracts.cs`：`IBaslerStreamCamera` 新增 `CaptureSingleFrame(...)`；
  接口与 `BaslerStreamCameras` 文档改为"一台相机上全部设备侧动作的唯一入口、物理设备上只有一条取流通道"。
- `src/DP.Vision.Basler/PylonStreamCamera.cs`：实现 `CaptureSingleFrame`（参数写入 → `OneByOne`/`ProvidedByStreamGrabber` →
  软件触发 → `RetrieveResult(timeout, ThrowException)` → `GrabSucceeded` 校验 → `PylonGrabFrame.ForRetrieved`，
  `finally` 中停掉本次抓图）；`StartContinuousGrab` 在 `IsGrabbing` 时明确失败；`StopContinuousGrab` 按是否真正开始过持续取流决定是否 `Stop`。
- `src/DP.Vision.Basler/BaslerAcquisitionDevice.cs`：字段收敛为 `_camera`；`CaptureAsync` 移除 `#if BASLER_SDK` 分支与 per-Capture 设备生命周期；
  `DisposeAsync` 顺序改为 `session.DisposeAsync()` → `camera.Dispose()`；删除旧 `#if` 抓图实现。
- `tests/DP.Vision.Basler.Tests/TestDoubles/StreamTestDoubles.cs`：假相机模拟单条取流通道（`_grabArmed`/`_capturing` 互斥、
  `SingleCaptureCount`、可阻塞的 `CaptureEntered`/`CaptureRelease`、参数记录）。
- `tests/DP.Vision.Basler.Tests/BaslerAcquisitionDeviceStreamTests.cs`：新增 7 个用例（见上表），既有释放顺序与布防用例保留。
- `src/DP.Vision.Basler/README.md`：改为"一台设备只有一个已连接相机"，补充"单次采集与持续取流互斥"与断线语义。

### 测试结果

`dotnet test DP.Vision.sln -c Debug -f net8.0-windows`：**548 通过 / 0 失败**（V2-4 基线 541 + 新增 7）。

分套件：DP.Vision.Basler.Tests 91 → 98（+7）· DP.Vision.Tests 115 · DP.Vision.Acquisition.Tests 153 · DP.Vision.Halcon.Tests 111 ·
DP.Vision.Algorithms.Tests 67 · DP.Vision.Acquisition.Integration.Tests 4。

`dotnet build DP.Vision.sln -c Debug`（net48 + net8.0-windows）：0 错误；仅剩 3 个与本次改动无关的预存 net48 可空警告
（`HalconDeviceSettingsParser.cs(100)` CS8604 ×2、`TestDeviceSettingsParser.cs(20)` CS8604）。

### 记录（相对计划的偏离）

- **相机仍在"第一次使用时打开"，未在 `BaslerAcquisitionProvider.OpenAsync` 急打开**。理由：
  `CrossVendorProviderCoexistenceTests.TwoRealVendors_CoexistAndRouteBySourceId` 会在无相机的开发机上调用
  `baslerProvider.OpenAsync` 并断言设备身份；急打开会让 `CameraFinder.Enumerate()` 找不到设备而抛 `VisionDeviceOfflineException`，
  破坏该"无硬件可验证"的测试意图。懒打开同样满足 V2-3 完成条件（多次 Capture 的 OpenCount 恒为 1，直到 Runtime 停止才 Close 一次），
  也满足 V2-6 第 1/2 条（两种模式复用同一已连接对象、无 per-Capture 生命周期）。
- 单次采集在途时**拒绝布防**（而不是排队等待），这是"一条取流通道"的直接推论；宿主若要两种模式交替，需自行等待前一次采集返回。

## V2-7：HALCON 迁移

状态：**已完成**（2026-09-21）· 仅 DP.Vision 仓库

### 需求覆盖（§20 V2-7）

1. **`HalconAcquisitionDevice` 持有唯一 `HFramegrabber`**：字段收敛为 `_camera`（`IHalconStreamCamera`），
   由 `_cameraFactory` 在第一次被用到时创建一次（`_camera ??= _cameraFactory(_binding)`）；
   `CaptureAsync` 与 `StartStreamAsync` 取用同一个对象，设备只在 `DisposeAsync` 时 `Close` 一次。
   已删除 `HalconCameraCapture`（public `ICameraCapture` 实现，每次采集新建/打开/关闭 `HFramegrabber`）与其测试。
2. **OnDemand 重复使用同一句柄**：`CaptureAsync` 不再有 `#if HALCON_SDK` 分支、不再构造设备，
   抓图职责迁移到 `HalconFramegrabberCamera.CaptureSingleFrame`。单次采集失败后相机保持打开，下一次请求可直接重试。
3. **OnConnect 流复用同一句柄和采集线程**：`HalconStreamSession` 仍在自建线程上循环 `GrabOnce()`，
   但句柄来自设备适配器的同一个 `_camera`；同一设备第二次布防复用已打开相机（OpenCount 恒为 1），
   停止只停流、不关设备（关设备统一由设备释放负责）。
4. **修正 `KeepCurrent`、`Software` 与 `Gain` 单位语义**：
   - 新增 SDK 无关的决策点 `HalconFramegrabberParameters`（可无相机、无许可证验证）：
     写入顺序固定为 `grab_timeout` → 曝光/增益 → 触发，两条采集路径共用同一份决策。
   - `KeepCurrent`：**一个触发参数都不写**（旧实现把"保持当前设置"写成 `external_trigger='default'` 这类打开参数，语义与结果都不确定）。
   - `Software`：按 MVTec 官方示例 `genicamtl_software_trigger.hdev` 实现——`[Consumer]trigger=Software` +
     `AcquisitionMode=Continuous`，每帧前写 `[Consumer]trigger_software=1` 再 `grab_image`，不写 `external_trigger`；
     `HalconAcquisitionDriverModule` 声明的 `SupportsSoftwareTrigger: true` 由此成为真实能力（旧实现明确拒绝）。
   - `Gain`：公开单位统一为**分贝**，写 SFNC `Gain` 节点前先关 `GainAuto`；曝光同理先关 `ExposureAuto` 再写 `ExposureTime`。
   - `null`（不动设备当前设置）与显式 `0`（真实取值）严格分离，不再用 `> 0` 守卫把两者混为一谈。
5. **不覆盖当前并发在研实现，先做差异合并**：在研流式分支（`1361204`）的文件保留，只在其上收敛契约与生命周期，
   未重写同名文件；`HalconStreamFaults`、`HalconNeutralFrames`、`HalconImageSource`、会话线程纪律与停止顺序均未改动。

完成条件达成：真实 SDK 路径上同一台 HALCON 设备只存在一个 `HFramegrabber`，两种采集模式复用同一句柄且双向互斥。

### 验收证据（§21）

| 验收（§21） | 证据 |
|---|---|
| 多次 OnDemand Capture 不重复 Open/Close（§21.6） | `HalconAcquisitionDeviceStreamTests.CaptureAsync_ReusesSingleConnectedCameraAcrossRequests`（3 次采集 OpenCount==1、事件序 `Open,CaptureSingle×3`、`AppliedExposure==1500`、`AppliedGain==2.5`）· `DeviceDispose_AfterCaptureOnly_ClosesCameraOnce` |
| 第二根运行复用同一个 SDK 对象（§21.18） | `ThreeRuns_ShareSingleConnectedCameraAndCloseOnce`（OpenCount==1）· `SecondArm_ReusesOpenCameraInsteadOfOpeningAgain` |
| Shutdown 事件顺序：停流先于关设备（§21.20） | `DeviceDispose_StopsStreamBeforeClosingCamera` · `DeviceDispose_IsIdempotent` · `DeviceDispose_WithoutArming_DoesNotTouchCamera` |
| 断线状态可解释且不关闭相机（§20.5） | `StreamFailure_EndsSessionWithoutClosingCamera` · `CaptureAsync_AfterStreamFailure_ReportsDisconnect`（消息含断线原因） |
| 两种模式互斥，不产生两条取流通道（§20.3） | `CaptureAsync_WhileArmed_IsRejected` · `StartStream_WhileCapturing_IsRejected`（单次采集在途时布防被拒且 `GrabCount==0`） |
| `KeepCurrent` 不改动设备触发设置 | `HalconFramegrabberParametersTests.KeepCurrent_WritesNoTriggerParameter` |
| `FreeRun` / `External` 触发写入正确 | `FreeRun_DisablesExternalTrigger` · `External_WritesDeclaredTriggerSource` · `External_WithoutDeclaredTriggerSource_IsRejected` · `HalconAcquisitionDeviceStreamTests.ArmTriggerMode_FollowsBindingTriggerSource`（DataRow 四态） |
| `Software` 触发真实可用 | `Software_ConfiguresConsumerTriggerAndContinuousAcquisition` · `RequiresSoftwareTriggerCommand_OnlyForSoftware` · `SoftwareTriggerCommand_IsAnExplicitSingleShotWrite` · `DriverModule_ContributesAreaScanType`（`SupportsSoftwareTrigger`） |
| 曝光/增益单位语义与显式 0 | `CaptureParameters_NothingGiven_WritesNothing` · `CaptureParameters_WritesManualValuesWithAutomaticAlgorithmsOff` · `CaptureParameters_ExplicitZeroIsWrittenToTheDevice` · `CaptureParameters_OnlyGainGiven_LeavesExposureUntouched` · `DisplayValue_UsesInvariantCulture` |
| 缺 SDK 时不伪造帧、不留悬空入口 | `HalconBoundaryTests.MissingSdk_IsExplicitlyUnavailable`（工厂在造设备时抛 `VisionProviderUnavailableException`）· `HalconAcquisitionProviderPluginTests.PluginHealth_ReportsSdkAvailability` |
| 厂商适配不依赖 Workflow / 旧视觉程序集 | `HalconBoundaryTests.DependencyDirection_IsIndependent`（按 `HalconAcquisitionDevice` 所在程序集断言引用方向） |

### 变更文件

- `src/DP.Vision.Halcon/HalconStreamContracts.cs`：`IHalconStreamCamera.ApplyArmParameters` 增补 `grabTimeoutMilliseconds`；
  新增 `CaptureSingleFrame(...)`（与持续取流互斥）；新增 `HalconStreamCameras.IsSdkEnabled`（从被删除的 `HalconCameraCapture` 迁入）。
- `src/DP.Vision.Halcon/HalconFramegrabberParameters.cs`（新增）：`HalconDeviceParameter` 值与全部参数名常量、
  `ResolveTrigger` / `ResolveCaptureParameters` / `RequiresSoftwareTriggerCommand` / `SoftwareTriggerCommand`，
  承载 `KeepCurrent`、`Software`、dB 增益与"null 与 0 分离"的全部决策。
- `src/DP.Vision.Halcon/HalconStreamCamera.cs`：`HalconFramegrabberCamera` 增加 `_sync`/`_captureInFlight` 互斥状态；
  实现 `ApplyArmParameters`（与单次采集互斥）与 `CaptureSingleFrame`（单次采集与 `_grabbing` 互斥、写参数 → 软触发 → `GrabImage` → 中立帧）；
  参数写入收敛为 `WriteParameters`/`WriteParameter` 一条路径；超时与 `HOperatorException` 一律翻译为 Provider 级异常。
- `src/DP.Vision.Halcon/HalconStreamSession.cs`：`Arm` 签名带上布防参数，委托相机执行后起自建线程（线程纪律不变）。
- `src/DP.Vision.Halcon/HalconAcquisitionDevice.cs`：单一 `_camera`、`CaptureAsync` 改为镜像 Basler 的非 async 形态并删除 per-Capture 生命周期。
- `src/DP.Vision.Halcon/HalconAcquisitionProviderPlugin.cs` / `HalconAcquisitionDriverModule.cs` / `HalconAcquisitionProvider.cs`：
  `IsSdkEnabled` 迁址；类文档改为"按绑定创建持有唯一 `HFramegrabber` 的设备适配器"。
- `src/DP.Vision.Halcon/README.md`：改为"一个绑定一个设备适配器、一个适配器一个句柄"，
  补充四态触发、dB 增益、"null 与 0 分离"、双向互斥与 `Software` 的现场确认要求。
- `tests/DP.Vision.Halcon.Tests/HalconFramegrabberParametersTests.cs`（新增）：14 个参数决策用例。
- `tests/DP.Vision.Halcon.Tests/TestDoubles/StreamTestDoubles.cs`：假相机模拟单条取流通道
  （`_grabbing`/`_captureInFlight` 互斥、`SingleCaptureCount`、可阻塞的 `CaptureEntered`/`CaptureRelease`、四参记录）。
- `tests/DP.Vision.Halcon.Tests/HalconAcquisitionDeviceStreamTests.cs`：新增 7 个用例（句柄复用、双向互斥、断线可解释、释放顺序）。
- `tests/DP.Vision.Halcon.Tests/HalconStreamSessionTests.cs` / `HalconBoundaryTests.cs`：随签名与 SDK 探测迁址更新。
- **已删除**：`src/DP.Vision.Halcon/HalconCameraCapture.cs`、`tests/DP.Vision.Halcon.Tests/HalconTriggerMappingTests.cs`。

### 测试结果

`dotnet test DP.Vision.sln -c Debug -f net8.0-windows`：**566 通过 / 0 失败**（V2-6 基线 548 + 18）。

分套件：DP.Vision.Halcon.Tests 111 → 129（+18，新增 14 个参数决策用例 + 7 个句柄/互斥用例，随删除 `HalconTriggerMappingTests` 抵减）·
DP.Vision.Tests 115 · DP.Vision.Acquisition.Tests 153 · DP.Vision.Basler.Tests 98 ·
DP.Vision.Algorithms.Tests 67 · DP.Vision.Acquisition.Integration.Tests 4。

`dotnet build`（HALCON 源码与测试项目，net8.0-windows）：0 警告 0 错误。
整解加 `-f net8.0-windows` 会因 `DP.Vision.Algorithms`（`netstandard2.0`）等基础项目不提供该目标框架而报 `NETSDK1005`，
这是既有配置，与本次改动无关；测试按各套件的目标框架执行。

### 记录（相对计划的偏离与待办）

- ~~**`DP.Vision.Algorithms` 的 `ICameraCapture` / `CameraCaptureOptions` 推迟清理**~~：该接口与选项类型由旧采集路径引入，
  现已无实现者，但定义在跨项目契约程序集内（`DP.Vision.Algorithms` 还被 WorkFlow 侧引用），
  删除会波及本阶段之外的仓库，故只删除本Provider内的实现与测试，接口保留并在本记录中标记待清理。
  → **已于 2026-09-22 清理完毕**：确认生产代码零消费者、零实现者后删除
  `src/DP.Vision.Algorithms/Acquisition/ICameraCapture.cs`（含 `CameraCaptureOptions`），
  并新增 `tests/DP.Vision.Algorithms.Tests/Architecture/LegacyCaptureAbstractionTests.cs` 两条独立断言
  （运行期导出类型不存在 + 生产源码标识符不出现）禁止复发，两条断言在删除前双 TFM 均变红、删除后转绿。
- **`DP.WorkFlow/docs` 旧 SOP 文档待同步**：`vision-architecture.md`、`vision-acquisition-providers.md`、
  `nodes/new-vision-file-pipeline.md` 仍描述 `HalconCameraCapture` / `ICameraCapture` / "每次采集打开关闭设备"，
  属于跨仓文档同步，需在 WorkFlow 仓库单独提交。本仓 `README.md` 的同类表述已随本次改动更正；
  `UNIFIED_IMAGE_SOURCE.md` 仍把 `ICameraCapture.CaptureAsync` 列为统一入口，属历史迁移记录，未改。
  → **本仓侧已于 2026-09-22 更正**：`README.md` 与 `UNIFIED_IMAGE_SOURCE.md` 均改为描述当前形态
  （相机像素只经 `DP.Vision.Acquisition` 中立契约与 Provider 插件进入，旧类型已删除）。
  **跨仓部分（`DP.WorkFlow/docs`）仍待处理。**
- **`Software` 触发未经现场验收**：实现依据 MVTec 官方示例与本机 SDK 反射结果，缺少真实相机验证；
  现场若所用采集接口不接受 `[Consumer]trigger`，会以 `VisionParameterNotSupportedException` 明确失败（不静默降级）。
- **提交状态**：V2-6 与 V2-7 已提交为 `ff2a873`（Basler 迁移）与 `c1a725b`（HALCON 迁移，含本文档）。

## V2-8：线扫与采集卡首个 Adapter

状态：**已完成**（2026-09-21）· 仅 DP.Vision 仓库

选型：**扩展 HALCON 插件**（本机仅安装 MVTec HALCON 23.11；Basler pylon、Euresys、Silicon Software、Matrox、海康 MVS、大华均未安装）。
HALCON 采集接口（`hAcqGigEVision2`/`hAcqUSB3Vision`/`hAcqGenICamTL` 及采集卡对应接口）本身即支持线扫相机与采集卡通道，
因此线扫不需要新的Provider，只需要一个独立的AcquisitionType。

### 需求覆盖（§20 V2-8）

1. **选择真实线扫SDK**：HALCON 采集接口（现场为 GigE/USB3/GenICamTL 或采集卡接口）。选择依据是本机唯一可验证的 SDK，
   不在无法验证的 SDK 上写适配器。
2. **Plugin 贡献 LineScan AcquisitionType**：`HalconAcquisitionDriverModule` 新增 `dp.acquisition.halcon.line`
   （`EVisionAcquisitionKind.LineScan`，显示名"HALCON 线扫相机"），与面阵 `dp.acquisition.halcon.area` 并列贡献；
   `VisionAcquisitionTypeCatalogComposer` 按 Kind 分别列出，工作流线扫节点只显示 Line Source。
3. **Adapter 只向上返回SDK完成的整张图**：线扫与面阵复用同一个 `HalconAcquisitionDevice`——
   `grab_image` 返回的就是采集接口/采集卡组装好的整张图，适配器只做"设备帧 → 中立图像"的复制，
   不做行拼接、不做分块交付；一次请求对应一张完整图像。
4. **不引入 Line/Chunk 公共模型**：两个Type共用同一个 `HalconDeviceSettingsParser`，线扫没有额外 deviceSettings 字段；
   公共契约程序集不新增任何 Line/Chunk/Block 类型或厂商原生类型（由用例断言）。
5. **完成 Node PropertyGrid 和请求映射**：已由 V2-5 交付（`CaptureLineScanFrameNode` 使用
   `WorkflowPropertyEditorKeys.VisionLineScanSource`，`CreateRequest()` 固定 `EVisionTriggerMode.KeepCurrent`），
   本阶段只需把 HALCON 线扫 Source 以 `Kind = LineScan` 投影进 SourceCatalog 即可被该节点选中。

### 验收证据（§21）

| 验收（§21） | 证据 |
|---|---|
| 线扫节点只显示 Line Source（§21.22） | `HalconLineScanAcquisitionTests.MachineConfiguration_ProjectsLineScanSourceByKind`（线扫Source `Kind == LineScan`、面阵Source `Kind == AreaScan`）· `HalconAcquisitionDriverModuleTests.PluginDirectory_ContributesBothKindsWithoutManifest`（`GetByKind(LineScan)` 只含线扫Type） |
| 面阵节点只显示 Area Source（§21.21） | 同上（`GetByKind(AreaScan)` 只含面阵Type） |
| 两种节点都输出完整 ImageFrame（§21.23） | `LineScanBinding_DeliversSdkAssembledWholeFrame`（一次请求一张 4096×2048 整图、`Gray8`、`SingleCaptureCount == 1`） |
| 线扫Provider不会把 Line/Block 对象越过公共 Interface（§21.24） | `LineScanBinding_DeliversSdkAssembledWholeFrame`（交付对象是 `VisionProviderFrame`，图像类型不在 HALCON 程序集）· `PublicContract_IntroducesNoLineChunkModel`（公共契约无 Line/Chunk/Block 与厂商原生类型） |
| 两个Type能力与私有配置契约一致 | `DriverModule_ContributesLineScanType`（能力集与面阵相等）· `DriverModule_TypesSharePrivateDeviceSettingsContract`（同一份deviceSettings解析出相同绑定身份与资源键） |
| 机器配置按 Type 注册 Provider | `MachineConfiguration_ProjectsLineScanSourceByKind`（`ProviderManifest == [area@1.0.0, line@1.0.0]`） |

### 变更文件

- `src/DP.Vision.Halcon/HalconAcquisitionDriverModule.cs`：新增 `LineScanTypeId`，贡献线扫Type；
  类文档说明"整图由SDK组装、两个Type只在Kind上区分"。
- `src/DP.Vision.Halcon/README.md`：新增"面阵与线扫两个 AcquisitionType"小节；测试数 129 → 134，
  补充线扫的现场确认项（整图高度由相机帧触发还是采集接口/采集卡参数决定、行频与整图尺寸的对应关系）。
- `tests/DP.Vision.Halcon.Tests/HalconAcquisitionDriverModuleTests.cs`：改为按 TypeId 收集注册，新增线扫Type用例与
  "按Kind分别列出"用例。
- `tests/DP.Vision.Halcon.Tests/HalconLineScanAcquisitionTests.cs`（新增）：机器配置投影、整图交付、公共契约无Line/Chunk模型共 3 例。

### 测试结果

`dotnet test DP.Vision.Halcon.Tests -c Debug -f net8.0-windows`：**134 通过 / 0 失败**（V2-7 基线 129 + 5）。
`dotnet build`（HALCON 源码与测试项目，net8.0-windows）：0 警告 0 错误。

### 记录（相对计划的偏离与待办）

- **线扫没有独立 deviceSettings 解析器，也没有线扫专属参数**。计划只要求"Adapter只向上返回SDK完成的整张图"，
  而 HALCON 线扫的整图高度由相机帧触发或采集接口/采集卡参数决定，属于**设备侧配置**而非适配器职责；
  在无法现场验证的前提下新增 `imageHeight`/`lineRateHertz` 之类的参数只会把"猜接线"写进契约。
  两个Type因此共用同一个解析器，另立第二份解析器只会让两侧校验逐渐分叉。
- **线扫仍未经真实硬件验收**：本阶段只证明"线扫Type可贡献、可被机器配置投影、适配器交付整图"，
  行频/整图高度/触发拓扑/采集卡多通道并行都必须在现场确认（见 README 的现场确认项）。
- **发现既有缺陷（V2-2 遗留，本阶段未修）**：机器配置路径下 Provider 注册的工厂不带私有绑定。
  `VisionAcquisitionMachineConfigurationComposer` 用 `descriptor.Factory`（HALCON 为 `() => new HalconAcquisitionProvider()`，
  Basler 同理）注册 Provider，而 `VisionAcquisitionTypeRegistration.Factory` 是 `Func<IVisionAcquisitionProvider>`（无参），
  **无法注入 `DeviceSettingsParser` 解析出的绑定**；`HalconAcquisitionProvider.OpenAsync(providerBindingId)` 查不到绑定即抛
  `VisionSourceConfigurationException`。样例已删除插件私有配置加载（`Form1.cs` 只用机器配置路径），
  因此当前**机器配置路径无法真正打开 HALCON/Basler 设备**，样例靠 `RuntimeState != Ready` 只显示诊断而未暴露。
  证据：`VisionAcquisitionMachineConfigurationComposer.cs` L78-82 与 `HalconAcquisitionProvider.cs` L50-52；
  机器配置用例用的是 `FakeVisionProvider.WithDevices(...)`（自带设备、不需要绑定），所以这条路径没有测试覆盖。
  修复方向（跨 Abstractions/Runtime/Basler/HALCON 的契约变更，需单独立项）：
  把 Type 工厂改为绑定感知（`Func<IReadOnlyList<VisionDeviceSettingsParseResult>, IVisionAcquisitionProvider>`，
  并让解析结果携带不透明 `ProviderBinding` 载荷），或让工厂接收该 Type 的原始 deviceSettings 列表自行构建绑定。
- **提交状态**：已提交为 `fa2cbe1`（feat(acquisition): V2-8 线扫首个Adapter——HALCON线扫Type与SDK整图交付）。

## V2-9a：设备发现、配置修订与运行制品（服务端）

状态：**已完成**（2026-09-21）· 仅 DP.Vision 仓库

范围界定：本阶段只做服务端能力——§20 V2-9 第 3/4/5 条，加上第 1 条中"设备发现（`IVisionDeviceDiscovery` 实现）"与
"机器配置修订号与候选验证"的服务端部分。第 1 条的试拍/发布/回滚**界面**与第 2 条"连接/取流/Inbox 监控面板"由宿主承载，
留待 V2-9b，本阶段不产出界面代码。

### 需求覆盖（§20 V2-9）

1. **设备发现（第 1 条服务端部分）**：Basler 与 HALCON 的 Provider 同时声明 `IVisionDeviceDiscovery`
   （契约 `IVisionDeviceDiscovery`/`VisionDeviceDescriptor` 在 V2-2 已冻结）。
   - Basler：`BaslerDeviceDiscovery.Enumerate()` 走 `CameraFinder.Enumerate()`，取
     `SerialNumber`/`UserDefinedName`/`ModelName`/`VendorName`/`FriendlyName`；
     序列号与自定义名皆空的候选被跳过——没有稳定身份就无法写进机器配置。
   - HALCON：`HalconDeviceEnumeration.Enumerate()` 逐接口执行 `info_framegrabber(..., 'info_boards', ...)`，
     默认三个工业相机接口（`GigEVision2`/`USB3Vision`/`GenICamTL`）并可由构造参数覆盖；
     `HalconBoardInfo` 按 `token:value` 解析权威条目，只把 `device:` 当作可回填的设备 ID（HALCON 只认它）。
   - 两者都把候选转成与各自 `DeviceSettingsParser` **完全一致**的 bindingId 与规范 ResourceKey（用例断言），
     因此"发现到的候选"可以直接落成机器配置，不需要人工改写资源键。
   - 确定性：按资源键排序（ordinal），两次枚举同序。缺 SDK/原生运行时不伪造"没有设备"，而是抛
     `VisionProviderUnavailableException`（HALCON 仅在**全部接口都失败**时抛，单个接口失败按故障分类保留文本并跳过）。
2. **Composition 与配置修订进入运行制品（第 3 条）**：
   - 新增 `VisionAcquisitionRunArtifact`（含 `VisionAcquisitionRunSourceArtifact`/`VisionAcquisitionRunFrameArtifact`），
     `IVisionAcquisitionRunArtifactSource.TryGetArtifact` 由 `VisionAcquisitionRuntime` 的 RunLease 实现，宿主在 Run 结束后取走。
   - 制品字段照 §18 齐备：`WorkflowCompositionId`/`AcquisitionCompositionId`/`MachineConfigurationRevision`/`PluginManifest`/
     每源 `AcquisitionTypeId` 与绑定身份/`Epoch`/领取帧 `CaptureId`+`ReceivedSequence`+`DeviceSequence`/
     `UnclaimedAtEpochEnd`/`InboxHighWatermark`/`BytesHighWatermark`/`DeviceSequenceGaps`/`LastFailureKind`/`ConnectionState`/`TransferState`。
   - 相机配置摘要**脱敏**：`serialNumber`/`userDefinedName`/`deviceName` 这类设备身份键的值按"≤4 字符整段替换，
     否则保留首 2 与末 2"转为 `***` 形态，便于把制品带出车间或贴进工单。
   - `BeginRunAsync` 追加 `workflowCompositionId` 重载（原签名委托到它，调用方无需改）。
3. **长时间运行、断线、重连和关闭测试（第 4 条）**：`LongRunLifecycleTests` 覆盖 20 轮 Epoch 的长期运行、
   接收流故障、重连需开新 Runtime、停止等待在途采集退出四种场景。
4. **像素转换耗时与字节数（第 5 条）**：新增 `VisionPixelTransferObservation`/`VisionPixelTransferSummary`；
   `VisionProviderFrame` 追加可选 `deviceSequence` 与 `transferObservation`；
   两家 `NeutralFrames.CopyObserved` 用 `Stopwatch` 实测"中立像素落地"的耗时与字节数；
   `VisionResourceSession.RecordTransfer` 汇总进 `VisionSourceDiagnostics` 的
   `Transfer`/`TransferState`/`FramesRejectedOverflow`。
   缓冲路径由回调帧自带观测，OnDemand 路径由 Runtime 在拿到帧后记录，两条路径共用同一份累计值，运行监视不必区分采集模式。
   **从未观测到落地时 `Transfer` 为空**，以便把"Provider 没上报观测"与"上报了 0 字节"区分开（后者只可能是上报实现出错）。
5. **机器配置修订号与候选验证（第 1 条服务端部分）**：新增 `VisionAcquisitionMachineConfigurationRevisionStore`：
   `SetCandidate`（只记录不校验）→ `ValidateCandidate`（返回 `IsValid`/`Errors`/`CompositionId`/`SourceIds`/`UnavailableSourceIds`）
   → `Publish` → `History`/`Find` → `Rollback(revision)`。

设计要点（已写进类型文档）：

- **历史只追加、不移动指针**：回滚不删除中间修订，而是追加一条"内容等于目标修订"的新修订并记录 `RestoredFromRevision`；
  "当前生效"恒等于修订号最大的那一条，审计不需要还原一串指针变更才能解释"当时机器上跑的是什么"。
- **校验与发布共用同一次组合快照**，不存在"校验过的内容"与"发布出去的内容"不同。
- 校验路径**不抛配置异常**：第三方 Plugin 解析时抛出的意外异常也转成诊断文本，
  否则一次插件缺陷会让管理界面在"校验"按钮上直接崩溃；发布失败才抛 `VisionSourceConfigurationException` 并拼接全部错误。
- 未安装 Type 的 Source 是**合法配置**（校验通过），但在结果里显式列出 `UnavailableSourceIds`——
  现场最容易出错的正是"插件没部署"被当成"相机没接"。
- 候选与校验分离还带来一个副作用：同一个候选可以反复校验而不必重设，非法候选的多条错误能一次报全。

### 验收证据

| 依据 | 证据 |
|---|---|
| §18 制品字段齐备与敏感字段脱敏 | `RunArtifactTests.Artifact_RecordsCompositionRevisionAndClaimedFrames`（修订号/组合身份/插件清单/`serialNumber=***`/领取帧身份）· `Artifact_WithoutHostIdentity_LeavesOptionalFieldsEmpty` |
| §18 本轮 EndEpoch 未领取帧计数 | `Artifact_AfterRetirement_RecordsUnclaimedFrames`（退役后 `UnclaimedAtEpochEnd == 2`） |
| §18 TransferState / ConnectionRevision 与像素观测 | `PixelTransferObservationTests.BufferedFrames_AccumulateTransferObservations`（`TransferState == "Streaming"`、`ConnectionRevision == 1`、领取后不清零）· `RejectedFrame_StillCountsTransferBytes` · `OnDemandCapture_RecordsTransferObservation`（`TransferState == "NotStarted"`）· `FramesWithoutObservation_LeaveTransferEmpty` |
| §20 V2-9.1 发现能力已实现且与配置解析一致 | `BaslerDeviceDiscoveryTests.Provider_ImplementsDeviceDiscovery` · `DiscoveredCameraWithSerial_MatchesDeviceSettingsParser` · `DiscoveredCameraWithoutSerial_FallsBackToUserDefinedName` · `CameraWithoutStableIdentity_IsSkipped` · `Descriptors_AreOrderedByCanonicalKey` · `EmptyEnumeration_ReturnsEmptyDescriptors` |
| §20 V2-9.1 HALCON 候选解析与去重 | `HalconDeviceDiscoveryTests.Provider_ImplementsDeviceDiscovery` · `DefaultInterfaceNames_CoverIndustrialCameras` · `Parse_ReadsAuthoritativeTokens` · `Parse_ToleratesPipesAndUnknownEntries` · `Parse_TreatsPlainStringAsDeviceId` · `DescriptorsWithSerial_MatchDeviceSettingsParser` · `DescriptorsWithoutSerial_MatchDeviceSettingsParser` · `SameCameraOnTwoInterfaces_IsDeduplicatedAndOrdered` |
| 缺 SDK 时不伪造"没有设备" | `HalconDeviceDiscoveryTests.AllInterfacesUnavailable_FailsInsteadOfReportingNoDevices`（断言 `VisionProviderUnavailableException.ProviderId`，含把接口名设为不存在接口的对照用例） |
| §20 V2-9.4 长期运行（§21.4 / §21.18 / §21.20） | `LongRunLifecycleTests.ManyEpochs_KeepSingleDeviceAndSingleStream`（20 轮 Epoch：`Devices.Count == 1`、`OpenedBindings.Count == 1`、`StreamStartCount == 1`、`ConnectionRevision == 1`、40 帧全领、收口 0；Dispose 后事件序 `stream-start,stream-stop,device-dispose`） |
| §20 V2-9.4 断线可解释 | `LongRunLifecycleTests.StreamFailure_FaultsSourceWithStreamFailureKind` |
| §20 V2-9.4 重连语义 | `LongRunLifecycleTests.Reconnect_UsesNewRuntimeAndOpensDeviceAgain`（只有换新 Runtime 才重新打开设备：`OpenedBindings.Count` 累计为 2、新会话 `ConnectionRevision == 1`） |
| §21.19 关闭等待在途操作退出 | `LongRunLifecycleTests.StopAwaitsInFlightCaptureBeforeDisposingDevice`（在途未退出时 `StopAsync` 不完成、设备未释放；停止中再次采集明确失败且不新增 Open） |
| §20 V2-9.1 修订发布/回滚/候选校验 | `MachineConfigurationRevisionStoreTests.Publish_AppendsFirstRevisionAndClearsCandidate` · `ConstructorWithInitialConfiguration_PublishesFirstRevision` · `ConstructorWithInvalidConfiguration_Throws` · `InvalidCandidate_ReportsErrorAndRefusesPublish` · `WithoutCandidate_ValidationAndPublishFail` · `EmptyCandidate_IsRejected` · `EachPublish_AppendsNextRevisionAndChangesCompositionOnSettingsChange` · `Rollback_AppendsNewRevisionWithTargetContent` · `Rollback_UnknownRevision_Throws` · `UninstalledType_IsValidButReportsUnavailableSource` |

### 变更文件

公共契约层（`DP.Vision.Acquisition.Abstractions`）：

- `VisionPixelTransferObservation.cs`（新增）：`VisionPixelTransferObservation` + `VisionPixelTransferSummary`。
- `VisionAcquisitionRunArtifact.cs`（新增）：`IVisionAcquisitionRunArtifactSource` + 运行/源/帧三类制品。
- `VisionProviderFrame.cs`：追加可选 `deviceSequence`、`transferObservation` 与 `TransferObservation` 属性。
- `VisionSourceDiagnostics.cs`：尾参追加 `AcquisitionTypeId`/`PluginId`/`PluginVersion`/`TransferState`/`ConnectionRevision`/
  `FramesRejectedOverflow`/`Transfer`（全部带默认值，既有调用点不受影响）。

实现层（`DP.Vision.Acquisition.Runtime`）：

- `VisionAcquisitionMachineConfigurationRevisionStore.cs`（新增）：候选、校验、发布、历史、回滚与修订记录类型。
- `VisionResourceSession.cs`：连接修订号与像素观测累计（`RecordTransfer`/`DescribeTransferState`），`Transfer` 在无观测时为空。
- `VisionAcquisitionRuntime.cs`：`BeginRunAsync` 追加 `workflowCompositionId` 重载与 `machineConfigurationRevision` 参数；
  RunLease 实现制品源（领取登记、未领取累计、来源身份补齐、配置摘要脱敏）。

厂商（Basler / HALCON）：

- `BaslerDeviceDiscovery.cs`（新增）/ `HalconDeviceDiscovery.cs`（新增）：`IVisionDeviceDiscovery` 实现、
  候选→`VisionDeviceDescriptor` 转换、规范资源键与确定性排序。
- `Compatibility/IsExternalInit.cs`（两处新增）：net48 目标下为 `record` 与 `init` 访问器提供编译器占位类型，
  与 `Acquisition.Abstractions`/`Acquisition.Runtime` 既有的同名文件同一写法（见"记录"里的 net48 说明）。
- `BaslerAcquisitionProvider.cs` / `HalconAcquisitionProvider.cs`：声明并实现发现能力；
  HALCON 构造器新增可选 `discoveryInterfaceNames`（便于现场指定采集卡接口）。
- `BaslerNeutralFrames.cs` / `HalconNeutralFrames.cs`：`CopyObserved` 返回图像 + 像素落地观测（`Stopwatch` 计时），
  `Copy` 委托给它。
- `BaslerAcquisitionDevice.cs` / `BaslerStreamSession.cs` / `HalconAcquisitionDevice.cs` / `HalconStreamSession.cs`：
  调用点改用 `CopyObserved` 并把观测带进帧。

测试：

- 新增 `tests/DP.Vision.Acquisition.Tests/Configuration/MachineConfigurationRevisionStoreTests.cs`（10）·
  `Lifecycle/LongRunLifecycleTests.cs`（4）· `Lifecycle/RunArtifactTests.cs`（3）·
  `Streaming/PixelTransferObservationTests.cs`（4）·
  `tests/DP.Vision.Basler.Tests/BaslerDeviceDiscoveryTests.cs`（6）·
  `tests/DP.Vision.Halcon.Tests/HalconDeviceDiscoveryTests.cs`（11）。
- `tests/DP.Vision.Acquisition.Tests/TestDoubles/FakeStreamingVisionDevice.cs`：`Emit` 支持携带设备序号与像素观测。

### 测试结果

`dotnet test DP.Vision.sln -c Debug -f net8.0-windows`：**609 通过 / 0 失败**（V2-8 基线 571 + 新增 38）。

分套件增量：

- DP.Vision.Acquisition.Tests 153 → 174（+21：修订存储 10 · 长跑/断线/重连/关闭 4 · 运行制品 3 · 像素观测 4）
- DP.Vision.Halcon.Tests 134 → 145（+11：设备发现）
- DP.Vision.Basler.Tests 98 → 104（+6：设备发现）

其余套件（DP.Vision.Tests 115 · DP.Vision.Algorithms.Tests 67 · DP.Vision.Acquisition.Integration.Tests 4）不变。

双目标构建（`dotnet build <csproj> -c Debug`，即 net48 + net8.0-windows）：

| 项目 | 警告 | 错误 |
|---|---|---|
| `DP.Vision.Basler` | 0 | 0 |
| `DP.Vision.Basler.Tests` | 0 | 0 |
| `DP.Vision.Halcon` | 0 | 0 |
| `DP.Vision.Halcon.Tests`（net48） | 0 | 2（V2-8 既有：`HalconLineScanAcquisitionTests` 用了 net48 不存在的 `string.Contains(char, StringComparison)`） |
| `DP.Vision.Halcon.Tests`（net8.0-windows） | 0 | 0 |

### 记录（相对计划的偏离与待办）

- **只做服务端，UI 留待 V2-9b**：§20 V2-9 第 1 条的试拍/发布/回滚界面与第 2 条的监控面板需要宿主工程承载，
  本阶段交付的是这些界面必须依赖的服务端能力（发现、候选校验、发布/回滚、运行制品、像素观测），界面不在本仓产出。
- **修订存储不负责"试拍"**：试拍要求打开真实设备并取流，那是 Runtime 的能力（V2-3 已交付）；
  修订存储既不持有设备也不触发采集，避免"发布配置"与"动设备"耦合。
- **回滚是追加而非回退**：现场若期望"回滚后修订号变小"，需要指针语义；本实现有意选择可解释优先（见上文设计要点）。
- **V2-2 遗留缺陷仍未修**（机器配置路径 Provider 工厂无参、无法注入解析出的绑定，见上一节记录）：
  该缺陷会阻断机器配置路径上的真实设备打开，按约定本阶段不动它。
  影响面：本阶段新交付的"发现 → 写配置 → 发布"链路只能到**发布**为止，**试拍需待缺陷修复**；样例仍靠 `RuntimeState != Ready` 只显示诊断。
- **发现能力未经真实硬件验收**：Basler 侧本机没有 pylon 原生运行时，HALCON 侧无线扫/采集卡通道设备，
  枚举结果目前只有单元测试与格式解析断言；接口名取值、`info_boards` 条目形态、同一相机跨接口去重、以及多接口枚举的耗时都需现场确认
  （见两家 README 的现场确认项）。
- **net48 目标顺带修复（V2-7 遗留，非本阶段引入）**：新增的发现类型用了 `record`，而 net48 需要
  `System.Runtime.CompilerServices.IsExternalInit` 占位类型，因此在两个厂商项目各加了 `Compatibility/IsExternalInit.cs`
  （与 `Acquisition.Abstractions`/`Acquisition.Runtime` 既有写法一致——它们因为 `netstandard2.0` 早就各有一份）。
  在此之前 **`DP.Vision.Halcon` 的 net48 目标根本编译不过**（V2-7 的 `HalconFramegrabberParameters` 用了 `readonly record struct`，
  报 8 个 CS0518；V2-7 记录里的"0 警告 0 错误"只针对 net8.0-windows，所以这条一直没暴露）。加上占位类型后该目标可编译，
  同时暴露并修掉了 `HalconDeviceSettingsParser` 在 net48 下的 2 条 CS8604（net48 引用程序集没有 `NotNullWhen`，
  编译器学不到 `IsNullOrWhiteSpace` 守卫的结论；用 `!` 显式断言，对 net8.0-windows 无影响）。
- **`DP.Vision.Halcon.Tests` 的 net48 目标仍编译不过**（2 个 CS1501，V2-8 的 `HalconLineScanAcquisitionTests`
  用了 net48 不存在的 `string.Contains(char, StringComparison)`），属既有问题，本阶段未改；
  测试与验收仍按 `-f net8.0-windows` 执行。
- **提交状态**：已提交为 `0c0fe40`（feat(acquisition): V2-9 设备发现与配置修订——发现能力、运行制品与像素落地观测，30 文件 +2524/-32）。

## V2-9b：采集管理界面（表现层与 WinForms 控件）

状态：**已完成**（2026-09-21）· 仅 DP.Vision 仓库

分层：把界面拆成"可单元测试的表现层"（`DP.Vision.UI`，`netstandard2.0`，不依赖任何UI框架）与"薄视图"（`DP.Vision.Winform`，`net48;net8.0-windows`）。
这样设备发现、配置修订、监视与试拍的编排逻辑全部可以在没有相机、没有许可证、没有窗体的机器上被验证，WinForms 控件只负责绑定与显示。

### 需求覆盖（§20 V2-9）

1. **设备发现与绑定（第 1 条）**：`AcquisitionManagementPresenter.DiscoverAsync` 遍历组合中已注册的 Provider，
   对实现 `IVisionDeviceDiscovery` 的逐一枚举并合并候选。
   - **只用 `ProviderManifest`（已注册的 Provider 清单），不用已发布的 Source**：发现的用途正是"还没给这个 Provider 配置任何源时先看见现场有哪些设备"，
     按已发布 Source 反推会让尚未配置的 Provider 永远发现不到设备。
   - **单个 Provider 失败被隔离为一条诊断**，不整体失败，也绝不伪装成"现场没有设备"——缺 SDK 与"确实没有相机"在界面上必须能分开看。
   - 候选按 ProviderId → 规范资源键 → 显示名 → 绑定身份依次排序，两次枚举同序，界面不抖动。
   - 候选与当前机器配置比对（`CanonicalKey` 对已发布 `ResourceKey`），标出"已被配置引用"；控件提供"复制资源键"（缺规范资源键时退回序列号）供操作员写配置。
2. **配置修订（第 1 条）**：`SetCandidate` / `ValidateCandidate` / `Publish` / `Rollback(revision)` 全部经表现层暴露，
   快照给出当前修订号与 CompositionId、候选错误、已发布源行（含 Kind、采集模式、共享策略、可用性、配置摘要）、
   历史行（Revision / CompositionId / PublishedAtUtc / RestoredFromRevision / IsRollback）；
   校验结果里的 `UnavailableSourceIds` 与全部错误文本一次性显示——现场最易混淆的"插件没部署"与"相机没接"因此可以分开。
3. **连接/取流/Inbox 监控面板（第 2 条）**：`CaptureMonitor` 把运行时状态与每个 Source 的一行诊断投影成快照；
   控件按固定间隔刷新（可暂停），逐源显示连接状态与消息、取流状态、连接修订号、Epoch、
   接收/领取/超龄/无代次拒绝/溢出拒绝、待领取数与字节与水位、设备序号缺口、像素落地（次数/总字节/总耗时/最近字节/最近耗时）与最后故障类别与消息。
   - **运行时未启动或已停止时同样能采样**：行来自组合而不是来自已打开的会话，因此面板在这两段时间里显示"Created/Stopped + 尚未打开"，而不是空白。
   - **`Transfer` 为空显示占位符而不是 0**：把"Provider 没上报观测"与"观测到 0 字节"在界面上分开（后者只可能是上报实现出错）。
4. **试拍（第 1 条）**：`TrialCaptureAsync` 先确保运行时已 `StartAsync`（未启动则启动并保持，停止后无法重启 → 作为失败结果返回原因），
   然后对指定逻辑源执行一次 `CaptureAsync`。**除取消外的不成功都结果化**：配置错误、资源冲突、设备离线、参数不支持、采集超时、数据异常、
   SDK 不可用、其他采集异常共 8 类落在 `FailureKind` 上，界面显示原因即可；取消原样传播为 `OperationCanceledException`。
   试拍遵守与节点采集相同的共享策略（占用资源键上的采集互斥门）。

### 验收证据

| 依据 | 证据 |
|---|---|
| §20 V2-9.1 发现合并与"是否已配置"标记 | `AcquisitionManagementPresenterTests.DiscoverAsync_MergesCandidatesInDeterministicOrderAndMarksConfiguration` |
| 单个 Provider 发现失败被隔离为诊断（缺 SDK ≠ 没有设备） | `DiscoverAsync_IsolatesProviderFailuresAsDiagnostics` |
| §20 V2-9.1 校验失败时快照携带全部错误 | `InvalidCandidate_ExposesErrorsInConfigurationSnapshot` |
| §20 V2-9.1 发布后修订号与 CompositionId 进入快照 | `Publish_ExposesRevisionCompositionAndSourceRows` |
| 发布失败不写历史 | `PublishFailure_CarriesAllErrorsAndLeavesHistoryUntouched` |
| §20 V2-9.1 回滚追加新修订并体现在历史 | `Rollback_AppendsRevisionVisibleInHistory` |
| §20 V2-9.2 未启动时仍可采样、无观测时为空 | `CaptureMonitor_BeforeStart_ReportsCreatedRowsAndEmptyTransfer` |
| §20 V2-9.2 像素落地与取流状态进入监控行 | `CaptureMonitor_MapsTransferObservationAndStreamingState` |
| §20 V2-9.2 停止后仍可采样 | `CaptureMonitor_AfterStop_ReportsStoppedStateWithRows` |
| §20 V2-9.1 试拍成功并自动启动运行时 | `TrialCapture_SucceedsAndStartsRuntime` |
| 试拍失败结果化（配置/离线/停止/空源） | `TrialCapture_UnboundSource_ReturnsConfigurationFailure` · `TrialCapture_DeviceUnavailable_ReturnsOfflineFailure` · `TrialCapture_StoppedRuntime_ReturnsFailureInsteadOfThrowing` · `TrialCapture_EmptySourceId_IsRejected` |
| 取消原样传播 | `TrialCapture_CancellationPropagates` |

### 变更文件

表现层（`src\DP.Vision.UI\Acquisition\`，全部新增）：

- `AcquisitionMonitorSnapshot.cs`：`AcquisitionMonitorSnapshot` + `AcquisitionMonitorRow`（搬运 `VisionSourceDiagnostics` 全部字段，`Transfer` 可空）。
- `AcquisitionConfigurationSnapshot.cs`：配置面板快照 + 已发布源行 + 修订历史行。
- `AcquisitionDiscoverySnapshot.cs`：候选设备行（含"是否已被配置引用"）、Provider 发现失败行。
- `AcquisitionTrialCaptureResult.cs`：试拍结果（成败、耗时、失败类别与消息、图像元数据）。
- `AcquisitionManagementPresenter.cs`：发现编排、配置修订编排、监视采样、试拍。
- `DP.Vision.UI.csproj`：新增对 `DP.Vision.Acquisition.Runtime` 的 ProjectReference。

视图（`src\DP.Vision.Winform\Acquisition\AcquisitionManagementControl.cs`，新增）：
`public sealed class AcquisitionManagementControl : UserControl`，三页（设备发现 / 配置修订 / 运行监控），
只做绑定与显示；`Dispose` 停表并取消在途动作。

测试：

- `tests\DP.Vision.Acquisition.Tests\Ui\AcquisitionManagementPresenterTests.cs`（新增，15 例）。
- `tests\DP.Vision.Acquisition.Tests\TestDoubles\FakeVisionProvider.cs`：实现 `IVisionDeviceDiscovery`，可注入候选与发现失败。
- `tests\DP.Vision.Acquisition.Tests\DP.Vision.Acquisition.Tests.csproj`：新增对 `DP.Vision.UI` 的 ProjectReference。

依赖与锁文件：引用 `DP.Vision.UI` 的工程（`DP.Vision.Demo`、`DP.Vision.WPF`、`DP.Vision.Winform`、`DP.Vision.Probe`、两个测试工程）
因传递依赖（`Acquisition.Runtime` → `System.Text.Json` 等）由 restore 更新了 `packages.lock.json`，属依赖图真实变化的正常结果。

### 测试结果

`dotnet test DP.Vision.sln -c Debug -f net8.0-windows`：**624 通过 / 0 失败**（V2-9a 基线 609 + 新增 15）。

分套件：DP.Vision.Acquisition.Tests 174 → 189（+15）· DP.Vision.Tests 115 · DP.Vision.Algorithms.Tests 67 ·
DP.Vision.Basler.Tests 104 · DP.Vision.Halcon.Tests 145 · DP.Vision.Acquisition.Integration.Tests 4。

`dotnet build src\DP.Vision.UI\DP.Vision.UI.csproj -c Debug`（netstandard2.0）：0 警告 0 错误。
`dotnet build src\DP.Vision.Winform\DP.Vision.Winform.csproj -c Debug`（net48 + net8.0-windows）：0 警告 0 错误。

### 记录（相对计划的偏离与待办）

- **界面尚未接入任何宿主样例**：两个采集样例都在 DP.WorkFlow 仓库（`samples\DP.WorkFlow.WinForms.Sample`、`samples\Legacy\WpfApptest`），
  它们目前都没有引用 `DP.Vision.Winform`。把 `AcquisitionManagementControl` 挂进样例、并让样例改用修订存储发布配置，
  属跨仓集成，需在 WorkFlow 仓库单独提交（与既有的跨仓 SOP 文档同步待办同类）。
- **"发现 → 一键生成 deviceSettings"做不到**：`VisionDeviceDescriptor` 只给出绑定身份与规范资源键，
  **不给出厂商私有 deviceSettings 的字段结构**，而宿主编译期不允许引用厂商类型。因此界面只能把资源键/序列号交给操作员，
  无法一键生成 `deviceSettings`。要支持一键回填，需要 Provider 侧新增"候选 → deviceSettings 投影"能力（公共契约的增量扩展），
  与 V2-2 的工厂缺陷同源，列入后续立项。
- **真实设备试拍在机器配置路径上必然失败**（V2-2 遗留缺陷：Provider 工厂无参，解析出的绑定注入不进去）。
  这是**预期行为**：界面会把失败类别与原因显示出来，而不是绕过它去自己 new 一个厂商 Provider。
  缺陷修复前，试拍只在测试 Provider 与"已配置绑定"的路径上可用。
- **Provider 身份曾取自 `ProviderManifest` 的 `providerId@version` 切分**（V2-11 已消除）：当时组合尚未暴露"已注册 Provider 清单"这一只读属性，
  为不改 Runtime 公共契约而采用按最后一个 `@` 切分的办法（已核对生成端就是 `ProviderId + "@" + Version`）。
  V2-11 已在 Composition 上补出只读注册清单 `Providers`，该处解析已删除，见下文 V2-11。
- **`DP.Vision.UI` 未新增 `Compatibility\IsExternalInit.cs`**：该工程既有类型不使用 `record`，
  新类型改用 internal 构造器 + 只读属性以保持在 `netstandard2.0` 下零警告（`record` 仍需占位类型，故回避）。
- **提交状态**：已提交为 `6911496`（18 文件 +3152/-13）。

## V2-11：服务端收口遗留三项（Provider 注册清单、§21 帧所有权证据、net48 构建卫生）

状态：**已完成**（2026-09-22）· 仅 DP.Vision 仓库

本阶段只处理 V2-10 会话中最初锁定、但当时未落地的 DP.Vision 侧三项遗留。范围之外的一律未动：
V2-2 Provider 工厂无参缺陷、discovery→deviceSettings 投影、DP.WorkFlow 宿主集成。

### 需求覆盖

1. **组合暴露只读 Provider 注册清单**：`VisionAcquisitionProviderComposition.Providers` 返回按 `ProviderId` 排序的
   `VisionAcquisitionProviderRegistration` 只读清单；`VisionAcquisitionProviderRegistration` 新增可选 `DisplayName`
   （机器配置组合器用 `VisionAcquisitionTypeDescriptor.DisplayName` 填充，插件路径未声明时保持为空）。
   `AcquisitionManagementPresenter` 删除按最后一个 `@` 切分 `ProviderManifest` 的临时解析，改用该清单取 Provider 身份。
2. **§21 未显式引用条目补齐**：逐条核对 §21 全部 28 条，把此前从未被引用的 8 条（§21.1/2/11/12/13/14/16/25）补齐显式引用。
   其中 §21.27/§21.28 为重点（帧所有权与设备关闭后可读性），其余 6 条与 §21.27/28 一样，**实现与测试均已存在**，本阶段只补文档证据；
   §21.25 在 DP.Vision 侧不适用（属 WorkFlow 宿主与 Studio 的属性迁移路径）。
3. **net48 构建卫生与插件加载正确性**：修复 `DP.Vision.Halcon.Tests` 在 net48 下的两处 CS1501；
   并修复 net48 首次可执行后暴露的插件加载缺陷——两个加载器对插件目录里的 DLL 一律 `Assembly.LoadFrom`，
   在 .NET Framework 上会把副本装进 LoadFrom 上下文，插件入口类型实现另一份契约接口而被静默漏掉（详见"记录"）。

### 验收证据（§21）

| 验收（§21） | 证据 |
|---|---|
| 未领取帧、超龄帧、拒绝帧全部只 Dispose 一次（§21.27） | 队列级：`FrameInboxUnitTests`（`Enqueue_RejectsWhenCapacityReached` 溢出、`Enqueue_RejectsWhenByteBudgetExceeded` 字节预算、`Enqueue_WithoutActiveEpoch_RejectsAndCounts` 无代次、`Claim_DropsExpiredFramesInsteadOfReturningThem` 超龄、`BeginEpoch_DropsPreviousEpochFrames` 旧代次、`EndEpoch_DrainsAllEntriesAndCountsUnclaimed` 未领取、`Drain_ReturnsAllEntriesWithoutReleasingThem`）全部断言精确 Dispose 次数与 `IsBalanced` 等式（创建 + 保留 == 释放，同时锁住漏释放与重复释放）· 运行期：`BufferedExternalInboxTests.InboxOverflow_FaultsSourceAndReleasesFrames`、`ExpiredFrame_IsReleasedAndNeverReturned`、`RunEnd_KeepsStreamRunningAndRejectsFramesWithoutEpoch`、`EndEpoch_DrainsUnclaimedFramesAndRecordsCount`、`EveryFrame_IsDisposedExactlyOnce` |
| 已返回 ImageFrame 在设备关闭后仍然可读（§21.28） | OnDemand：`RoutingTests.CapturedImage_SurvivesRuntimeDisposal`（Runtime Dispose 后仍能 `CopyTo` 出非零像素）· 缓冲：`BufferedExternalInboxTests.ReturnedFrame_RemainsReadableAfterDeviceDisposed`（事件序 `stream-start,stream-stop,device-dispose` + 关闭后读回首末像素）· 设备级：`FakeProviderContractTests.FakeDevice_ReturnsImageReadableAfterDeviceDisposed` · 厂商复制边界：`HalconNeutralFramesTests`（设备帧释放后中立图像仍可读）、`BaslerNeutralFramesTests.DirectFormat_StillGoesThroughDeviceConverter` |
| 组合只读注册清单是版本清单的结构化形式 | `ComposerTests.Providers_ExposesStructuredRegistrationList`（按 ProviderId 排序、与 `ProviderManifest` 表达同一批 Provider、未配置 Source 的 Provider 同样在列、显示名带出/为空） |
| 机器配置把 Type 显示名带进注册清单 | `VisionAcquisitionMachineConfigurationTests.SingleCamera_ProjectsUniqueCompositionAndSourceCatalog`（`Providers.Single()` 的 `DisplayName == "测试面阵相机"`） |
| 表现层不再切分清单行仍能发现未配置 Provider | `AcquisitionManagementPresenterTests.DiscoverAsync_MergesCandidatesInDeterministicOrderAndMarksConfiguration`（无 Source 绑定的 `dp.fake.gamma` 仍被枚举，且发现实例被释放） |

### 补齐：§21 其余未显式引用条目（V2-11）

§21 共 28 条，此前有 8 条从未被任何文档显式引用（§21.1、§21.2、§21.11、§21.12、§21.13、§21.14、§21.16、§21.25）。
逐条核对后：前 7 条**实现与测试均已存在**，本阶段只补齐显式引用；§21.25 在 DP.Vision 侧不适用（见下）。

| 验收（§21） | 证据 |
|---|---|
| DLL Module 自动发现且顺序确定（§21.1） | `VisionAcquisitionDriverModuleLoaderTests.DriverModuleDll_IsDiscoveredWithoutManifest`（不读 Manifest 也能发现）· `DiscoveryOrder_IsDeterministic`（同一目录两次扫描得到同一顺序）· `DependencyDll_WithoutDriverModule_IsIgnored` 与 `NativeDll_IsSkippedSilently`（依赖/原生 DLL 不误报为失败）· 厂商侧 `HalconAcquisitionDriverModuleTests.PluginDirectory_ContributesBothKindsWithoutManifest` |
| 重复 AcquisitionTypeId 拒绝发布（§21.2） | `VisionAcquisitionTypeCatalogComposerTests.DuplicateAcquisitionTypeId_IsRejected`（组合期抛 `VisionSourceConfigurationException`，文案含"重复"）· `DuplicateModuleExtensionId_IsRejected`（Module 身份同样去重） |
| 无 Epoch 回调被释放并计数（§21.11） | 队列级：`FrameInboxUnitTests.Enqueue_WithoutActiveEpoch_RejectsAndCounts`（`TryEnqueue` 返回 `NoActiveEpoch`、帧被释放、`RejectedWithoutEpochCount` 递增）· 运行期：`BufferedExternalInboxTests.RunEnd_KeepsStreamRunningAndRejectsFramesWithoutEpoch`、`OnConnectStream_ArmedAtRuntimeStart_BeforeFirstRun`（均为"流仍在跑但没有代次"的拒绝并计数） |
| BeginEpoch 后回调可早于采集节点入队（§21.12） | `BufferedExternalInboxTests.CallbackBeforeCapture_IsClaimedImmediately`（回调先到，随后 Claim 立即拿到该帧）· `CaptureBeforeCallback_CompletesWhenFrameArrives`（反向时序：先 Claim 后回调也能完成，证明入队与领取不要求固定先后） |
| FIFO 顺序领取（§21.13） | 队列级：`FrameInboxUnitTests.Claim_ReturnsFramesInArrivalOrder`（按到达顺序返回，序列号单调）· 运行期：`BufferedExternalInboxTests.ThreeFrames_AreClaimedInFifoOrder`（三帧按到达序领取） |
| 同一 Source 并行 Claim 确定性冲突（§21.14） | 运行期：`BufferedExternalInboxTests.ParallelClaims_RejectExactlyOneDeterministically`（两路并行领取必有一路失败且为 `VisionResourceConflictException`，另一路仍在等待，不是两个都失败）· `SecondRootRun_ConflictsWithHolderIdentity`（冲突报告当前持有者身份与策略）· 并发策略：`SharingPolicyTests.ExclusiveOperation_SecondCaptureFailsWithOccupantDiagnostics`（并行第二路确定性失败并带占用方诊断）、`SameResourceKey_TwoSources_ShareOneMutex` |
| 下一 Epoch 不能领取上一 Epoch 帧（§21.16） | 队列级：`FrameInboxUnitTests.BeginEpoch_DropsPreviousEpochFrames`（开新代次即清退旧代次帧并计数）、`BeginEpoch_RejectsNonIncreasing`（代次必须严格递增，回退/重复直接抛）· 运行期：`BufferedExternalInboxTests.PreviousRunFrames_DoNotEnterNextRun`（上一根运行的帧不会被下一根领走） |
| Source 切换和旧节点迁移不丢配置（§21.25） | **DP.Vision 侧不适用**：Source 切换与旧节点迁移发生在 WorkFlow 宿主与 Studio 的属性迁移路径上，采集侧只负责"按 SourceId 保真投影"，由 DP.WorkFlow 仓库的验收覆盖，不属本仓库范围。 |

### 变更文件

- `src/DP.Vision.Acquisition.Abstractions/IVisionAcquisitionProviderModule.cs`：`VisionAcquisitionProviderRegistration` 新增可选 `DisplayName`（init-only，默认空）。
- `src/DP.Vision.Acquisition.Runtime/VisionAcquisitionProviderComposition.cs`：新增只读 `Providers`（按 ProviderId 排序）。
- `src/DP.Vision.Acquisition.Runtime/VisionAcquisitionMachineConfigurationComposer.cs`：注册 Provider 时带出 Type 显示名。
- `src/DP.Vision.UI/Acquisition/AcquisitionManagementPresenter.cs`：删除 `ProviderIdOf` 与 `ProviderManifest` 切分，改用 `Providers`。
- `tests/DP.Vision.Acquisition.Tests/Composition/ComposerTests.cs`：新增 1 例注册清单回归。
- `tests/DP.Vision.Acquisition.Tests/Configuration/VisionAcquisitionMachineConfigurationTests.cs`：补显示名断言。
- `tests/DP.Vision.Acquisition.Tests/TestDoubles/TestDeviceSettingsParser.cs`：net48 下补非空断言，消除既有 CS8604。
- `tests/DP.Vision.Halcon.Tests/HalconLineScanAcquisitionTests.cs`：两处 `string.Contains(string, StringComparison)` 改为 `IndexOf(...) >= 0`。
- `src/DP.Vision.Acquisition.Runtime/VisionAcquisitionPluginAssemblyLoader.cs`（新增）：插件程序集统一加载入口，同身份程序集已由宿主加载时复用该实例。
- `src/DP.Vision.Acquisition.Runtime/VisionAcquisitionDriverModuleLoader.cs`：改用统一加载入口，删除重复的元数据预检与 `Assembly.LoadFrom`。
- `src/DP.Vision.Acquisition.Runtime/VisionAcquisitionProviderPluginLoader.cs`：同上，并对非托管程序集给出明确诊断。
- `tests/DP.Vision.Acquisition.Tests/Plugins/VisionAcquisitionDriverModuleLoaderTests.cs`：新增 1 例"契约副本 + 子目录副本不得遮蔽入口类型"回归。

### 测试结果

`dotnet build DP.Vision.sln -c Debug`：**0 警告 0 错误**（net8.0-windows 与 net48 全目标）。
`dotnet test DP.Vision.sln -c Debug -f net8.0-windows`：**626 通过 / 0 失败**（V2-9b 基线 624 + 新增 2）。
分套件：DP.Vision.Acquisition.Tests 191 · DP.Vision.Tests 115 · DP.Vision.Algorithms.Tests 67 · DP.Vision.Basler.Tests 104 ·
DP.Vision.Halcon.Tests 145 · DP.Vision.Acquisition.Integration.Tests 4。

net48 目标此前因 `DP.Vision.Halcon.Tests` 的 CS1501 根本无法编译，故从未执行；本阶段修好编译后首次运行即暴露插件加载缺陷
（`DP.Vision.Basler.Tests` 2 例、`DP.Vision.Halcon.Tests` 2 例），修复后 **net48 与 net8.0-windows 同为 626 通过 / 0 失败**，
分套件数量与上完全一致。

### 记录（相对计划的偏离与待办）

- **§21 全部 28 条无需改实现**：核对结论是未显式引用的 8 条（§21.1/2/11/12/13/14/16/27/28 中的 7 条）早已由既有用例满足（见上两表），
  本阶段只补齐文档显式引用，未新增实现改动；§21.25 属 WorkFlow 宿主侧，明确不适用。
- **`DisplayName` 只用于界面与诊断**：不参与 Provider 身份判定，也不进入 `CompositionId`（组合身份仍只由 `providerId@version`、绑定与私有配置摘要决定）。
- **插件路径（`Compose(modules, sources)`）不填 `DisplayName`**：`IVisionAcquisitionProviderModule` 只提交工厂与身份，
  显示名目前只由 AcquisitionType 声明；消费方应把空值回退为 `ProviderId`。
- **修复了 net48 首次可执行后暴露的插件加载缺陷（超出原定范围，经确认后实施）**：
  两个加载器原先对插件目录里的每个 DLL 一律 `Assembly.LoadFrom`。.NET Framework 会把副本装进 LoadFrom 上下文，
  于是同一份契约程序集在进程里出现两个类型身份——插件入口类型实现的是 LoadFrom 上下文那一份，
  而宿主用 `typeof(...).IsAssignableFrom` 比对的是默认上下文那一份，结果静默为 `false`，
  插件被误判成"程序集里没有入口类型"。部署约定把整个输出目录当插件包，目录里必然带有宿主提供的契约程序集，
  也可能存在被复制到子目录的副本，因此这条路径在真实部署中同样会踩到。
  修复方式：新增 `VisionAcquisitionPluginAssemblyLoader`，按程序集身份（名称 + 版本 + 区域 + 公钥标记）先查
  `AppDomain.CurrentDomain.GetAssemblies()`，命中即复用宿主实例，只有宿主确实没有时才 `LoadFrom`。
  .NET 上的行为不变（其 `LoadFrom` 本就按身份去重），net48 由此与 net8.0 一致。
  因果已用 `git stash` 双向验证：去掉该修复时 4 例失败复现，恢复后全绿。
- **提交状态**：主提交 `2c048a7`（9 文件 +139/-27）；§21 引用补齐为 `8cdad6a`（1 文件 +21/-2）；插件加载修复为 `47ae296`（5 文件 +115/-31）。

