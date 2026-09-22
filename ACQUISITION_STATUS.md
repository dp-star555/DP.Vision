# DP.Vision 图像采集：当前有效状态

> **本文是"采集现在长什么样"的唯一入口。**
> 计划与设计文档只作为归档保留，不再与本文平行描述现状；它们回答"当初为什么这样决定"，
> 本文回答"现在是什么、验收到哪一步、还差什么"。
>
> 最后更新：2026-09-22 · 基线 `DP.Vision.sln` 构建 **0 警告 0 错误**、
> 测试 **1208 例 0 失败**（6 个测试工程 × 双 TFM = 12 运行条目）。

## 文档地图

| 文档 | 性质 | 用途 |
|---|---|---|
| **本文** | **现状** | 当前形态、不变式、验收状态、待办 |
| [ACQUISITION_CONNECTION_V2_STATUS.md](ACQUISITION_CONNECTION_V2_STATUS.md) | 阶段记录 | V2-0..V2-9、V2-11 的逐阶段提交号、测试证据与偏离 |
| [ACQUISITION_RUNTIME_V1.md](ACQUISITION_RUNTIME_V1.md) | 设计 + 阶段记录 | 采集深化 V1（两种采集模式、帧窗口、故障语义） |
| [ACQUISITION_CONNECTION_V2_PLAN.md](ACQUISITION_CONNECTION_V2_PLAN.md) | **归档计划** | V2 的原始设计决策，已被 STATUS 取代 |
| `../DP.WorkFlow/docs/vision-acquisition-providers.md` | **归档设计** | 多厂商 Provider 化改造的动机、目标形状与验收矩阵 |

## 1. 当前形态

进程内三层，依赖方向单向：

```text
采集链（依赖方向单向向上）：
DP.Vision                          中立图像、算法
    ↑
DP.Vision.Acquisition.Abstractions  中立采集契约（33 个文件，无厂商依赖）
    ↑
DP.Vision.Acquisition.Runtime       组合 / 机器配置 / 设备生命周期 / 帧仓
    ↑
DP.Vision.Halcon  ·  DP.Vision.Basler   厂商 Driver Module（目录扫描发现，无 Manifest）

采集视图（在 Runtime 之上，与宿主 UI 套件解耦）：
DP.Vision.Acquisition.Management    采集配置 / 发现 / 监控快照与呈现模型（netstandard2.0）
    ↑
DP.Vision.Acquisition.WinForms      采集会话 WinForms 控件（只依赖 Management）

宿主侧（不参与采集依赖链）：
DP.Vision.UI                        平台中立 UI 套件（画布 / ROI / 结果浏览器）——不依赖采集层
DP.Vision.Winform  ·  DP.Vision.WPF    宿主外壳（WinForms / WPF 各一）
```

| 工程 | 职责 |
|---|---|
| `DP.Vision.Acquisition.Abstractions` | `IVisionAcquisition`、DriverModule 契约（含可选健康报告）、运行所有者、机器相机定义、中立帧与诊断 |
| `DP.Vision.Acquisition.Runtime` | Driver Module 目录扫描与加载、不可变 Provider 组合、机器配置解析与修订、`VisionAcquisitionTypeCatalog`、`VisionResourceSession`、`VisionFrameInbox` |
| `DP.Vision.Acquisition.Management` | 采集配置 / 发现 / 监控快照与呈现模型（`AcquisitionManagementPresenter` 等），**平台中立、不引用任何 UI 套件** |
| `DP.Vision.Acquisition.WinForms` | 采集会话视图（`AcquisitionManagementControl`），**只依赖 Management** |
| `DP.Vision.Halcon` | HALCON 采集接口 Provider（面阵/线扫/采集卡通道），编译期 `HALCON_SDK` 开关 |
| `DP.Vision.Basler` | pylon Provider，原生运行时健康探测 |
| `DP.Vision.UI` | 平台中立 UI 套件（画布 / ROI / 结果浏览器）。**不声明任何 `DP.Vision.Acquisition*` 依赖** |
| `DP.Vision.Winform` / `DP.Vision.WPF` | 宿主外壳。WinForms 侧只通过 `DP.Vision.Acquisition.WinForms` 接触采集 |

**采集只有一条路径**：Workflow 侧只声明 `IVisionAcquisition`；旧 `ICameraCapture` / `CameraCaptureOptions` / `HalconCameraCapture` 已删除，并有架构测试禁止复发。

## 2. 不变式（改动时不要破）

1. **依赖方向**：`DP.Vision` → `Abstractions` → `Runtime`；Provider → Abstractions + 厂商 SDK；
   `Acquisition.Management` → `Runtime` + `DP.Vision`；`Acquisition.WinForms` → `Management`。
   **禁止** `Runtime → DP.WorkFlow`、Provider → Workflow、公共契约 → 厂商 SDK、
   `DP.Vision.UI` → 采集层、`Management` → UI 套件、`WinForms` → UI 套件 / Workflow。
   **声明方向**由 `tests/DP.Vision.Acquisition.Tests/Architecture/SolutionDependencyBoundaryTests.cs`
   直接读 `.csproj` **全文**强制——**不能**靠程序集引用清单：Roslyn 只为**实际用到**的引用写
   `AssemblyRef`，"声明了却没用"在清单里完全看不见，却照样把对方整条依赖链拷进每个消费者的输出目录
   （实测：把 `Acquisition.Runtime` 的引用加回 `DP.Vision.UI.csproj`，基于程序集引用的断言全绿）。
   实际用到的类型方向另有 `Contracts/AssemblyBoundaryTests.cs` 兜底。
2. **`deviceSettings` 是设备配置的唯一来源**。设备字段只由该 AcquisitionType 的解析器解析一次，
   结果作为插件私有的 `ProviderState` 随公共绑定一路带到 `IVisionAcquisitionProvider.OpenAsync`；
   公共层只原样转交、不解释。**插件私有配置不再承载设备绑定**（非空即明确拒绝）。
   同一台相机绝不能在两处各写一遍——那会让"改了一处、另一处没改"变成难查的现场问题。
   由 `tests/DP.Vision.Acquisition.Tests/Contracts/DeviceSettingsBindingFlowTests.cs` 与两家
   `*DeviceSettingsParserTests` 强制。
3. **设备连接属于软件生命周期，取图属于节点或回调行为**。节点不得打开/关闭/重连/释放物理设备。
4. **一台物理相机或一个采集卡通道 = 一个 `VisionResourceSession`**，按物理 `ResourceKey` 互斥。
5. **流式三条契约**（真实 Adapter 必须照做）：
   - 所有权：`Publish` 一进入即转移，**接收方即使拒绝也必须释放帧**；
   - 线程：异常**不得抛回 SDK 回调线程**；
   - 停止：`DisposeAsync` 必须**等待已进入的交付退出**，之后不得再交付。
6. **厂商差异显式处理而非抹平**：HALCON 缺 SDK 是编译期问题；Basler 缺的是原生运行时。
7. **插件包必须自包含厂商依赖，但不得包含宿主契约程序集**（否则插件拿到第二份类型，组合必然失败）。
   插件发现**不读 Manifest**：依据是"程序集里存在实现 `IVisionAcquisitionDriverModule` 的公开类型"。
8. **插件可用性是机器部署状态，不是内容身份**：缺 SDK / 缺原生运行时 / 架构不匹配由
   `IVisionAcquisitionDriverModuleHealth.TryGetHealth` 在 Type Catalog 冻结时**按 Module 记录一次**，
   **不进入 `CatalogId`**——两台部署相同的机器必须得到同一个 `CatalogId`。
   不可用插件声明的 Source 被**保真保留**并带诊断，但**不产生绑定**，因此不会进入 Runtime 的
   打开设备流程；与"Type 未安装"同一条路，缺 SDK 在首节点执行前就可见。
   未实现健康报告的 Module 视为**可用**（可选接口，不是"默认不可用"）。
9. **运行隔离**：根运行 Epoch 只属于 `FrameInbox`；`EndEpoch` 清理本 Epoch 未领取帧并计入诊断，不交给下一根运行。
10. **Provider 失败不自动切换**到另一个 Provider。

## 3. 验收状态

### 3.1 采集 Provider 化（阶段 A–E）— 已完成

两个真实厂商 Provider 可在同一进程组合并按 `SourceId` 路由；工作流文档只保存逻辑 `SourceId`，
机器配置绑定 `ProviderId` + `ProviderBindingId` + `ResourceKey`。

**阶段 F（RunScope 与高级共享模式）**：RunScope 部分已由 V1-C 覆盖（见下）。
共享策略当前只实现 `ExclusiveOperation` 与 `Serialized`；`ExclusiveRun` 只对缓冲源有效，
`Broadcast` 待真实需求 —— 两者在准备阶段被**显式拒绝**，不做隐式降级。

### 3.2 采集深化 V1 — 软件结构验收已完成，现场验收待做

| 阶段 | 内容 | 状态 |
|---|---|---|
| V1-A | 模式与队列策略契约、可控 Fake 流式 Provider | ✅ |
| V1-B | Runtime 有界 `VisionFrameInbox` | ✅ |
| V1-C | 根运行 Epoch 接线（`IVisionAcquisitionRunOwner`） | ✅ |
| V1-D | Basler 真实回调 Adapter（`ImageGrabbed`） | ⚠️ 软件结构验收完成，**真实相机现场验收待做** |
| V1-E | HALCON 真实流式 Adapter（自建采集线程跑 `grab_image_async`） | ⚠️ 同上 |
| V1-F | 运行审计与长期验证 | ❌ 未做 |

两种采集模式：`OnDemand`（节点到达后采集）与 `BufferedExternal`（长期布防 + 有界 FIFO 待领取）。

### 3.3 采集连接架构 V2 — 已完成

V2-0..V2-9、V2-11 全部完成，逐阶段证据见 [STATUS](ACQUISITION_CONNECTION_V2_STATUS.md)。
包含：类型目录与自动 Module 发现、机器相机定义与不可变 Composition、应用级连接生命周期、
TransferPolicy 与 Epoch 解耦、面阵/线扫双节点模型、两家 Provider 迁移、首个线扫/采集卡 Adapter、
采集管理界面与审计。

### 3.4 采集插件体系收口（优化项 Phase B）— 已完成

- **B-1**（`a8da196`）：`deviceSettings` 成为设备配置唯一来源，插件私有配置不再承载设备绑定。
- **B-2**（`337604e`）：删除旧插件路径——`IVisionAcquisitionProviderPlugin` /
  `IVisionAcquisitionProviderHealth` / `VisionAcquisitionProviderPluginLoader` /
  厂商 `*AcquisitionProviderPlugin`·`*AcquisitionProviderModule` / 两家 `plugin.json`；
  SDK 健康检查与 `PluginIdentity` 迁到 `IVisionAcquisitionDriverModule`，
  并以可选接口 `IVisionAcquisitionDriverModuleHealth` 承载"已安装但当前不可用"的诊断。

结果：**采集插件只剩一条发现路径**——目录扫描 Driver Module + Type Catalog 冻结。
`tests/DP.Vision.Acquisition.Tests/Architecture/LegacyProviderPluginPathTests.cs` 冻结该删除
（被删类型缺席 + `plugin.json` 不再随包投放 + `.csproj` 不再引用，且带扫描下限防假绿）。

> 注意：本仓另有**一套无关的** `plugin.json` 体系——Workflow 节点插件的
> `WorkflowPluginLoader.ManifestFileName`。那是节点插件契约，与采集无关，未改动。

### 3.5 采集视图工程拆分（优化项 Phase D 的工程拆分半）— 已完成

把 `DP.Vision.UI` 里的采集区与 `DP.Vision.Winform` 里的采集控件拆成两个独立工程，
让"平台中立 UI 套件"彻底摆脱采集依赖：

- **`DP.Vision.Acquisition.Management`**（新，`netstandard2.0`）：`AcquisitionManagementPresenter`
  与 4 个快照 / 结果类型（配置、发现、监控、试采）。依赖 `DP.Vision` + `Abstractions` + `Runtime`。
- **`DP.Vision.Acquisition.WinForms`**（新，`net48;net8.0-windows`）：`AcquisitionManagementControl`。
  **只依赖 `Management`**（与既有 `DP.Vision.Winform` 只依赖 `DP.Vision.UI` 同构）。
- `DP.Vision.UI` 因此**去掉了 `Acquisition.Runtime` 引用**，回到纯画布 / ROI / 结果浏览器。

结果：**纯移动**——拆分后全量 `DP.Vision.sln` 仍为 12 运行条目 / **1202 例 0 失败 / 0 警告**，
与拆分前逐项一致；两个新工程各自产出成功。随后补的两条边界测试再 +4（§3.5 末）、Phase E 的三机用例
再 +2（§3.6），当前全量为 **1208 例 0 失败 / 0 警告**。
命名空间 `DP.Vision.UI.Acquisition` → `DP.Vision.Acquisition.Management`、
`DP.Vision.Winform` → `DP.Vision.Acquisition.WinForms`。

**为什么这次拆分是安全的**（动手前逐条确认过）：`DP.Vision.UI/Acquisition/*` 与
`AcquisitionManagementControl` 在 DP.WorkFlow 侧**零引用**；`AcquisitionManagementControl` 在本仓
**没有任何消费方**（连 `DP.Vision.Winform` 都没实例化它）；`DP.Vision.UI` 其余目录（Canvas / Roi / Results）
不使用任何采集类型；`DP.Vision.Tests` / `WPF` / `Demo` / `Probe` 均不使用采集类型。

> **一个必须记住的教训**：这条边界**不能**用"程序集引用清单"来测。
> 第一版边界测试断言 `DP.Vision.UI` 的程序集引用里没有 `DP.Vision.Acquisition*`，
> 变异验证（把 `Runtime` 引用加回 `DP.Vision.UI.csproj`）**未命中**——两个 TFM 各 1206 例 0 失败。
> 原因：Roslyn 只为**实际用到**的引用写 `AssemblyRef`，"声明了却没用到"在清单里完全看不见，
> 但它照样把对方整条依赖链拷进每个消费者的输出目录。
> 现已改为 `Architecture/SolutionDependencyBoundaryTests.cs` 直接读 `.csproj` **全文**
> （顺带也拦得住 `<Reference HintPath=…>` / `PackageReference` 绕道），并在文件头写明为什么。

**验证**：`SolutionDependencyBoundaryTests` 基线两 TFM 各 1 例全绿；三处变异**全部咬住**
（每处 2 失败 / 0 通过）——M1 把 `Runtime` 引用加回 `DP.Vision.UI`、M2 让 `Management` 引用
`DP.Vision.UI`、M3 让 `WinForms` 引用 `DP.Vision.UI`（同时证明确实扫到了 WinForms 工程文件）；
三份工程文件逐字节还原。

### 3.6 优化项 Phase E（相机自治验收）— 自动化项已覆盖

Phase E 的验收项多数早已由既有用例覆盖，本轮只补了**唯一真实缺口**：

| 验收项 | 现状 |
|---|---|
| 按 `ResourceKey` 冲突 | 已覆盖：`SharingPolicyTests.SameResourceKey_TwoSources_ShareOneMutex` / `ExclusiveOperation_SecondCaptureFailsWithOccupantDiagnostics`、`AcquisitionModeCompositionTests.TwoBufferedSourcesOnSameResourceKey_AreRejected` / `MixedAcquisitionModeOnSameResourceKey_IsRejected` |
| 旧窗口帧不进新窗口 | 已覆盖：`BufferedExternalInboxTests.PreviousRunFrames_DoNotEnterNextRun`、`FrameInboxUnitTests.BeginEpoch_DropsPreviousEpochFrames` |
| 无窗口帧被拒并计数 | 已覆盖：`RunEnd_KeepsStreamRunningAndRejectsFramesWithoutEpoch`、`FrameInboxUnitTests.Enqueue_WithoutActiveEpoch_RejectsAndCounts` / `EndEpoch_DrainsAllEntriesAndCountsUnclaimed` |
| 每台相机各自的消费方 | 已覆盖：`RoutingTests.BoundSource_OnlyCallsItsOwnProvider` / `ProviderFailure_DoesNotFallBackToOtherProvider` |
| **三机故障隔离** | **本轮补齐 ↓** |
| 诊断 / UI / 制品失败不影响采集 | 部分覆盖：`RunArtifactTests.Artifact_WithoutHostIdentity_LeavesOptionalFieldsEmpty`；UI 侧待随 Phase C 的诊断面重做一起补 |

**本轮新增：`Lifecycle/RuntimeConnectionLifecycleTests.MiddleCameraFailure_LeavesOtherCamerasFullyFunctional`**

三台相机（SourceId 排序后 `Camera.B` 居中）由**中间**那台打开失败，断言：

- 运行时状态为 `Degraded`（只有可选相机失败，而不是整体不可用）；
- 失败相机带故障诊断与原因；
- 另两台各自 `Connected`、**都能采集**、诊断不被牵连；
- 两台健康相机各打开一次自己的绑定（`CollectionAssert.AreEquivalent`，不假定顺序）。

> **为什么必须是三台、且失败的在中间**：既有 `OptionalFailure_RuntimeDegradesAndSourceUnavailable`
> 只有两台、且失败的那台排在**最后**。"遇到失败就提前收手"（打开循环里 `break` / `return`，
> 或把打开异常直接抛出而不吞掉）这类缺陷在那两种排布下都看不出来——排在失败者**之后**的相机
> 根本不会被检查到。

**验证**：基线两 TFM 各 1 例全绿；变异 M1（代表 Source 只取第一个 → 后面的相机不会被打开）与
M2（打开失败重新抛出 → `StartAsync` 直接失败、不再是 `Degraded`）**均咬住**（各 2 失败 / 0 通过），
源文件逐字节还原。

## 4. 尚未验证 / 待办

### 4.0 优化项 Phase A（相机自治）— 并行启停**已落地**；契约部分仍被跨仓在途改动阻塞

**目标**：相机只管理"连接 / 取流 / 帧窗口 / 缓冲 / 诊断"，**不理解 Workflow、根流程或流程资源集合**。

**契约变化（实施清单）**

| 动作 | 对象 | 说明 |
|---|---|---|
| 删除 | `IVisionAcquisitionRunOwner`、`IVisionAcquisitionRunLease` | 根运行不是相机的概念；`VisionAcquisitionRuntime.BeginRunAsync` 与内部 `RunLease` 一并删除 |
| 新增 | `IVisionFrameWindow` | `OpenWindow(sourceId, …)` 的返回值：`Generation` + `TakeNextAsync(timeout, ct)` + `IAsyncDisposable` |
| 改名 | `VisionFrameInbox.BeginEpoch/EndEpoch` → 窗口的 open/close | **窗口是唯一持有代次的对象**，队列只有一条实现，不保留第二套 |
| 改签名 | `IVisionAcquisition.CaptureAsync` → `VisionCaptureResult(Image, Failure)` | 让"取到了但这一帧有问题"不必靠异常表达 |
| 收窄 | `EVisionRuntimeState` | 只反映软件生命周期（打开/关闭/降级），不与运行状态耦合 |
| 并行 | `StartAsync` / `StopAsync` | 不同 `ResourceKey` 互相独立，一个慢相机不阻塞其他 —— **已落地，见 §4.0.2** |

**关键语义**：窗口的持有者是**消费方**（节点侧的帧作用域），不是根运行。
窗口关闭 = 收口本代次并释放未领取帧；窗口之外的帧仍按现有规则被拒绝并计数。

**为什么契约部分现在不做**：`IVisionAcquisition` 在 DP.WorkFlow 侧的**全部 5 个消费点**
（`VisionCaptureNodeExecution.cs`、`WorkflowImageRuntimePluginModule.cs`、两个 sample、
`BufferedExternalRunScopeEndToEndTests.cs`）此刻正被**另一个进程暂存或修改中**
（2026-09-22 17:2x 仍在写），而 `VisionAcquisitionRunScope.cs` 与 `WorkflowVisionFrameScope.cs`
是必须同步改的桥。现在改契约会打断对方在途的工作并让其暂存批次编译不过。
**等对方那批落地后再动**；解阻后 DP.Vision 侧与本仓侧要作为一个整体提交。

### 4.0.1 顺序约束：Phase A 必须先于 Phase C（诊断重做）

Phase C 要重做的诊断面（`VisionSourceDiagnostics.State`/`Epoch`/`TransferState`、
`VisionAcquisitionRunSourceArtifact.TransferState`/`Epoch`/`UnclaimedAtEpochEnd`）**与 Phase A 要删的
根运行/代次概念大面积重叠**——`Epoch`、`UnclaimedAtEpochEnd` 这些字段在 A 之后根本不存在，
`RunArtifact` 一族也会随租约一起消失。先做 C 等于改一遍马上要删的记录。

因此顺序固定为 **A → C**。解阻后 A 的收尾动作（按上面表格执行）本身就会把
`State` 拆成"连接 / 取流 / 窗口"三个强类型状态，C 只剩"资源级 vs 源级分离 + 计数等式"。
C 的计数口径要点已定：`FramesReceived` 定义为**到达会话回调边界的帧（含被拒）**，
使 `Received = Claimed + Expired + 被拒(溢出/无窗口/未布防) + 收口未领取 + 在队列` 恒成立，
并暴露 `FramesUnaccounted` 供断言——现在的口径里 `FramesRejected` 含了
`FramesReceived` 不含的一类（未布防拒绝），等式不成立，这正是 C 要修的。

### 4.0.2 已落地：按 `ResourceKey` 并行启动 / 停止（2026-09-22）

`VisionAcquisitionRuntime.StartAsync` / `StopAsync` 现在对**不同 `ResourceKey` 并行**执行，
**单个资源内的行为一字未改**。这一项不依赖任何被对方占用的契约，因此可以先落地。

- `StartAsync`：先在**单线程**里按 `ResourceKey` 选出代表 Source（去重），再 `Task.WhenAll` 并行打开。
- `StopAsync`：先并行释放全部会话，再并行释放全部 Provider。用 `WhenAll` 而不是逐个 `await`——
  某个会话释放失败也不会让其余会话与 Provider 永远得不到释放（异常在所有任务落定后抛出）。
- 取消语义未变：`WhenAll` 等**全部**任务落定才抛，所以取消时不会出现"打开动作还在半空中就重置状态"。
- 每个会话"先关接受门、再停流、再等在途操作退出、最后释放设备"的顺序**没有变**，
  它由 `VisionResourceSession.DisposeAsync` 自己保证（同步前段就在锁内置位停止态并唤醒等待者）。

**验证**：`tests/DP.Vision.Acquisition.Tests/Concurrency/ParallelResourceStartStopTests.cs` 两条用例
在**未修复代码上确认变红**（两个 TFM 各 2 失败 / 0 通过；失败信息分别是
"第一个资源还卡在打开里时，另一个资源必须已经打开完成"与"另一台必须已经停流完成"），
修复后本工程 196 例 0 失败（该提交时；基线 194，+2 即本用例）。
变异验证 3/3 咬住，且隔离干净：启动改回串行 → 只有启动用例红；停止改回串行 → 只有停止用例红；
停止时不释放 Provider → 停止用例 + 既有 `MultipleCaptures_OpenOnceAndNeverCloseUntilStop` 红。

> **断言刻意不按名字假定谁先谁后**：`VisionAcquisitionProviderComposition.Sources` 按 `SourceId` 排序，
> 声明顺序不作数。第一版用例把"慢"绑在 `Camera.Slow` 上，于是**串行实现也假绿**
> （`Camera.Fast` 排在前面，快相机本来就会先打开完）。现在卡住的是"第一个真正开始动作的资源"，
> 由测试自己分辨出另一个，顺序无关。
>
> **一个未命中的变异（如实记录）**：去掉"单线程选代表源"后全部用例仍绿。原因是该不变式另有
> `GetOrOpenDeviceAsync` 的 `OpenGate` + 二次判空独立保证，且组合期已禁止两个外部回调缓冲源
> 共用一个资源键。保留去重是为了让本次改动**只涉及并发**、不改单个资源的语义，
> 而不是因为有一条测试咬得住它。

- **真实相机现场验收**：V1-D / V1-E 的断线、重连、停流时序只能在现场签署。
  待现场确认项：HALCON 目标采集接口是否支持 `do_abort_grab`；`grab_image_async` 的实际取流频率上限。
  **真实 SDK 像素测试不代替相机现场验收。**
- **`deviceSettings.pixelFormat`（Basler）目前只进入配置摘要，没有写到设备上**：
  真正生效的像素格式来自 `BaslerNeutralFrames` 对设备上报格式的转换，超出支持范围会明确报错。
  把配置值写到 `PLCamera.PixelFormat` 需要 pylon 现场验证，因此留待现场验收一并处理。
- **V1-F（运行审计与长期验证）** 未实现。
- **组合键与插件身份仍是两个不同的字符串，必须区分**：机器配置路径下，
  组合/绑定里的 `ProviderId` 实际是 `AcquisitionTypeId`（如 `dp.acquisition.halcon.area`），
  而插件对外报告的身份是 `PluginIdentity`（`dp.vision.halcon`），设备身份与可用性诊断里用后者。
  Phase B-2 已把 `PluginIdentity` 收敛到各厂商 Driver Module 的**单一常量**，不再有两份声明；
  但两个字符串本身不会合并——在断言或诊断里混用，仍会得到"Provider 不在当前组合中"这类误导信息。
- **`Software` 触发**（HALCON）依据 MVTec 官方示例与本机 SDK 反射实现，未经现场验收；
  若所用采集接口不接受 `[Consumer]trigger`，以 `VisionParameterNotSupportedException` 明确失败，不静默降级。
- 采集侧遗留：`WorkflowVisionAcquisitionSession` 仍兼"文件夹采集会话 + 帧作用域准备"两职责；
  插件加载器不校验跨插件 `ProviderId` 唯一性（归 Composer）。
- **跨仓**：`DP.Vision` 与 `DP.WorkFlow` 之间仍是 `ProjectReference` 源码引用、**无版本锁定**，
  结构性改动前先提交可回退基线。

## 5. 怎么跑

```bash
export APPDATA="C:\Users\25845\AppData\Roaming"      # 缺它 NuGet 报 path1 为 null
export ProgramFiles="C:\Program Files"
export MSBUILDDISABLENODEREUSE=1
export PROCESSOR_ARCHITECTURE=AMD64                  # net48 下 OpenCV/HALCON 需要
export HALCONROOT="C:\Program Files\MVTec\HALCON-23.11-Progress"   # 缺它少 4 例、HALCON 工程退化成无 SDK 版本
cd DP.Vision && dotnet build DP.Vision.sln -c Debug
cd DP.Vision && dotnet test DP.Vision.sln
```

- 12 个运行条目（6 工程 × 双 TFM），当前 **1202 例 0 失败**：
  Algorithms 69、Acquisition 196、Basler 91、Halcon 127、Integration 3、Vision 115（各 ×2）。
- **解决方案级 `dotnet build` / `dotnet test` 建议加 `-m:1`**：本机同时构建 `DP.WorkFlow`
  （源码引用本仓工程）时，`obj/` 下的 dll/pdb 会被另一个 MSBuild 进程持有，
  多线程构建会报 CS2012「文件被占用」——那是**文件锁**，不是编译错误。
- **"文件已投放"不等于"插件能加载"**：验证部署必须在目标输出目录里真正跑一次加载器。
