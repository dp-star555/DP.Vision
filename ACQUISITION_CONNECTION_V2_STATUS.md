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
| V2-8 线扫与采集卡首个 Adapter | ⏳ 待实施 |
| V2-9 Acquisition UI、审计和运行优化 | ⏳ 待实施 |

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

- **`DP.Vision.Algorithms` 的 `ICameraCapture` / `CameraCaptureOptions` 推迟清理**：该接口与选项类型由旧采集路径引入，
  现已无实现者，但定义在跨项目契约程序集内（`DP.Vision.Algorithms` 还被 WorkFlow 侧引用），
  删除会波及本阶段之外的仓库，故只删除本Provider内的实现与测试，接口保留并在本记录中标记待清理。
- **`DP.WorkFlow/docs` 旧 SOP 文档待同步**：`vision-architecture.md`、`vision-acquisition-providers.md`、
  `nodes/new-vision-file-pipeline.md` 仍描述 `HalconCameraCapture` / `ICameraCapture` / "每次采集打开关闭设备"，
  属于跨仓文档同步，需在 WorkFlow 仓库单独提交。本仓 `README.md` 的同类表述已随本次改动更正；
  `UNIFIED_IMAGE_SOURCE.md` 仍把 `ICameraCapture.CaptureAsync` 列为统一入口，属历史迁移记录，未改。
- **`Software` 触发未经现场验收**：实现依据 MVTec 官方示例与本机 SDK 反射结果，缺少真实相机验证；
  现场若所用采集接口不接受 `[Consumer]trigger`，会以 `VisionParameterNotSupportedException` 明确失败（不静默降级）。
- **提交状态**：本次改动**尚未提交**。沙箱禁止在 `C:\Data\PiProgects\WorkFlow\DP.Vision\.git` 下创建 `index.lock`，
  `git commit` 无法执行；提交前需按约定再次确认。
