# DP.Vision 图像采集：当前有效状态

> **本文是"采集现在长什么样"的唯一入口。**
> 计划与设计文档只作为归档保留，不再与本文平行描述现状；它们回答"当初为什么这样决定"，
> 本文回答"现在是什么、验收到哪一步、还差什么"。
>
> 最后更新：2026-09-22 · 基线 `DP.Vision.sln` 构建 **0 警告 0 错误**、
> 测试 **1202 例 0 失败**（6 工程 × 双 TFM = 12 运行条目）。

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
DP.Vision                         中立图像、算法、UI
    ↑
DP.Vision.Acquisition.Abstractions  中立采集契约（33 个文件，无厂商依赖）
    ↑
DP.Vision.Acquisition.Runtime       组合 / 机器配置 / 设备生命周期 / 帧仓
    ↑
DP.Vision.Halcon  ·  DP.Vision.Basler   厂商 Driver Module（目录扫描发现，无 Manifest）
```

| 工程 | 职责 |
|---|---|
| `DP.Vision.Acquisition.Abstractions` | `IVisionAcquisition`、DriverModule 契约（含可选健康报告）、运行所有者、机器相机定义、中立帧与诊断 |
| `DP.Vision.Acquisition.Runtime` | Driver Module 目录扫描与加载、不可变 Provider 组合、机器配置解析与修订、`VisionAcquisitionTypeCatalog`、`VisionResourceSession`、`VisionFrameInbox` |
| `DP.Vision.Halcon` | HALCON 采集接口 Provider（面阵/线扫/采集卡通道），编译期 `HALCON_SDK` 开关 |
| `DP.Vision.Basler` | pylon Provider，原生运行时健康探测 |

**采集只有一条路径**：Workflow 侧只声明 `IVisionAcquisition`；旧 `ICameraCapture` / `CameraCaptureOptions` / `HalconCameraCapture` 已删除，并有架构测试禁止复发。

## 2. 不变式（改动时不要破）

1. **依赖方向**：`DP.Vision` → `Abstractions` → `Runtime`；Provider → Abstractions + 厂商 SDK。
   **禁止** `Runtime → DP.WorkFlow`、Provider → Workflow、公共契约 → 厂商 SDK。
   由 `tests/DP.Vision.Acquisition.Tests/Contracts/AssemblyBoundaryTests.cs` 强制。
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
修复后本工程 196 例 0 失败（基线 194，+2 即本用例）。
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
