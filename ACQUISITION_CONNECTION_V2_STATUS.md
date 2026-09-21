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
| V2-5 面阵/线扫双节点模型 | ⏳ 待实施 |
| V2-6 Basler 迁移 | ⏳ 待实施 |
| V2-7 HALCON 迁移 | ⏳ 待实施 |
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
