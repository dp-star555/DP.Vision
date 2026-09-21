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
| V2-3 应用级连接生命周期 | ⏳ 待实施 |
| V2-4 TransferPolicy 与 Epoch 解耦 | ⏳ 待实施 |
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
