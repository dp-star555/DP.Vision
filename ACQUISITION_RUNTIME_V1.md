# 图像采集深化 V1：主动请求与外部回调 FIFO

状态：**V1-A、V1-B、V1-C 已实施；V1-D、V1-E 软件结构验收已完成、真实相机现场验收待做；V1-F 待实现**
范围：`DP.Vision.Acquisition.*`、HALCON/Basler 采集 Adapter 及 Workflow 采集节点接线。  
目的：解决“外部触发图像已经回调，但 Workflow 尚未运行到采集节点”的问题，同时保留现有主动采集路径。

> 本文只定义采集时序和所有权。Plugin 配置文件布局、原生 `HObject/Mat` 表示缓存和 Broadcast 不在本版本展开。
>
> 实施状态以 §13 各阶段标题的状态标签为准。

## 1. V1结论

V1只实现两种采集模式：

```text
OnDemand
    节点到达后发起一次采集并等待结果；可以自由取帧、软件触发，
    也可以在节点到达后布防并等待下一次外部触发。

BufferedExternal
    相机长期布防；外部触发帧先进入相机级有界 FIFO，节点稍后领取最早未消费帧。
```

约束：

1. 正常机器配置采用“一台物理相机对应一个 SourceId”。
2. 一个物理相机只有一个接收 FIFO；FIFO 实际挂在 `ResourceKey` 对应的 Runtime ResourceSession 上。
3. 软件触发继续使用同步请求/响应，不引入 TriggerId。
4. 外部触发 V1 不要求 TriggerId；节点只执行 `TakeNext`，不指定帧序号。
5. Runtime 为回调帧分配递增 `ReceivedSequence`，只用于排序、去重和诊断。
6. FIFO 满、超龄或设备序号异常必须显式诊断；生产采集不得静默覆盖。
7. 被节点领取后，像素转交当前运行的 `WorkflowVisionFrameScope`；未领取前由 Acquisition Runtime 暂时拥有。

## 2. 当前事实与缺口

当前唯一消费入口是：

```csharp
IVisionAcquisition.CaptureAsync(source, request, owner, cancellationToken)
```

当前HALCON和Basler Adapter都在节点调用后才开始一次采集。即使底层SDK使用异步Grab，也不存在节点之前持续接收帧的站点级Session。

因此当前行为是：

```text
外部触发先发生
→ Runtime尚未等待
→ 平台没有保存该帧
→ 节点之后无法领取
```

当前还没有：

- 长期Streaming Session；
- Provider回调接收接口；
- 相机级待领取FIFO；
- Runtime接收序号；
- 根运行采集Epoch；
- 被动源的根运行独占；
- 停止时等待在途回调退出的生命周期屏障。

## 3. 统一语言

| 名称 | 含义 |
|---|---|
| AcquisitionMode | Source采用主动单次采集还是外部回调缓冲 |
| ResourceSession | Runtime按ResourceKey维护的物理设备状态、流和FIFO |
| FrameInbox | ResourceSession内部的有界待领取FIFO；不是公共数据或永久帧仓 |
| ReceivedSequence | Runtime成功接收回调帧时分配的单调递增序号 |
| DeviceSequence | 相机SDK可选提供的设备帧序号 |
| AcquisitionEpoch | 根运行开始时建立的接收边界；旧Epoch帧不可进入新运行 |
| Claim | 一个Operation原子领取FIFO头帧的动作 |

`ReceivedSequence`不是TriggerId，也不证明图像属于某个产品。外部设备不需要知道或指定它。

## 4. 两条正式链路

### 4.1 OnDemand

```text
CaptureFrame节点
→ IVisionAcquisition.CaptureAsync
→ ResourceKey操作租约
→ IVisionAcquisitionDevice.CaptureAsync
→ 软件触发/自由运行取一帧/节点到达后等待外部触发
→ VisionProviderFrame
→ VisionCapturedImage
→ WorkflowVisionFrameScope
```

规则：

- 同一ResourceKey只允许一个在途采集；沿用`ExclusiveOperation`或`Serialized`。
- 软件触发的调用和结果天然相关，不增加TriggerId。
- OnDemand External只保证取得节点布防之后的外部触发帧；不解决节点前回调。
- `CaptureId`继续作为本次采集结果和`ImageFrame.FrameId`。
- 现有调用方和节点默认保持此模式。

### 4.2 BufferedExternal

```text
ResourceSession打开并布防相机
→ 外部硬件触发
→ Provider收到SDK回调
→ VisionProviderFrame
→ Runtime分配ReceivedSequence/CaptureId
→ FrameInbox有界入队
→ CaptureFrame节点稍后调用CaptureAsync
→ 原子领取FIFO头帧
→ VisionCapturedImage
→ WorkflowVisionFrameScope
```

如果节点先到：

```text
CaptureAsync
→ 等待FIFO
→ 回调到达
→ 直接满足最早等待者
```

如果回调先到：

```text
回调入队
→ CaptureAsync
→ 立即领取FIFO头帧
```

两种时序对Workflow节点透明。

## 5. Source与物理相机规则

V1推荐：

```text
SourceId Camera.Top
→ ResourceKey camera:serial:001
→ 一个ResourceSession
→ 一个FrameInbox
```

现有Composer允许多个SourceId有意映射同一ResourceKey。V1不删除该通用能力，但对`BufferedExternal`模式增加限制：

- 同一ResourceKey只能发布一个主SourceId；
- 不支持通过多个SourceId形成多个独立FIFO；
- Source别名和多Profile是后续能力，不在V1实现；
- 如果未来需要多个采集参数组，应使用一个Source加Profile，而不是复制Source。

`ResourceKey`仍用于识别配置重复、设备互斥和来源审计。

## 6. FrameInbox规则

`FrameInbox`是`VisionAcquisitionRuntime`的内部实现，不进入Workflow、Provider插件或公共数据接口。

每个条目至少保存：

```text
CaptureId
ReceivedSequence
VisionProviderFrame
CapturedAtUtc
ReceivedAtUtc
DeviceSequence?
Epoch
```

### 6.1 FIFO

- 节点只能领取最早的未领取帧。
- V1不提供`Latest`生产策略。
- V1不提供按任意Sequence查询历史帧。
- 一张帧只能成功Claim一次。
- 同一Source同一时刻只允许一个等待中的Claim；并行第二个请求确定性冲突。

### 6.2 容量

BufferedExternal Source必须具有：

```text
InboxCapacity
InboxByteBudget
MaximumFrameAge
```

确切配置文件形状留给配置专题确定，但组合发布前必须能得到三个确定值。

V1溢出策略固定为：

```text
FaultSource
```

即：

1. 拒绝新帧；
2. 释放新帧租约；
3. 将ResourceSession标记Faulted；
4. 记录容量、字节数、ReceivedSequence和设备身份；
5. 后续Claim明确失败，直到宿主执行显式恢复。

V1不允许`DropOldest`或静默覆盖。预览“只保留最新”属于独立预览邮箱，不复用生产FrameInbox。

### 6.3 超龄

Claim前检查`ReceivedAtUtc`：

- 超过`MaximumFrameAge`的帧释放并记录Expired；
- 继续检查下一个FIFO条目；
- 全部超龄后按请求Timeout继续等待或失败；
- 过期不能被报告成一次成功采集。

### 6.4 序号

- `ReceivedSequence`由Runtime成功取得帧所有权时递增。
- `DeviceSequence`由Provider读取SDK信息；没有就保持空。
- DeviceSequence跳号时记录诊断，但是否停止Source由设备能力和现场策略决定。
- 仅凭ReceivedSequence不能检测“外部触发发生但相机没有产生回调”。

## 7. 根运行Epoch与所有权

BufferedExternal允许帧先于节点到达，但不允许上一产品的旧帧进入下一根运行。

目标时序：

```text
根运行取得被动Source独占权
→ Runtime开始新AcquisitionEpoch
→ 清退旧Epoch中未领取帧
→ 相机回调可以入新Epoch FIFO
→ Workflow Engine执行首节点
→ CaptureFrame节点稍后TakeNext
```

规则：

- BufferedExternal默认使用根运行独占，不与其他根运行共享同一物理相机。
- Nested运行继承根运行Epoch，不新建Epoch、不清FIFO、不释放设备。
- 根运行退役时释放未领取帧和被动Source所有权。
- Source没有活动根所有者时，回调可以被丢弃并计数，或保持设备未布防；V1实现必须选择一种并固定，不能让旧帧进入下一Epoch。
- 这一部分依赖Workflow根RunScope资源所有权契约（AR-01阶段3），不得用Nested传枚举自行清理替代。
  （**V1-C 已落地该机制**：中立的 `IWorkflowRunScopeOwner` + 采集侧 `VisionAcquisitionRunScope` 桥接。）

建议首个实现采用：

```text
无根运行所有者时不接受生产帧；
根运行准备完成后、Engine执行首节点前布防并建立Epoch。
```

这样既允许“回调早于采集节点”，又避免跨运行陈旧帧。

## 8. 公共Interface演进

### 8.1 Workflow消费Interface保持不变

```csharp
public interface IVisionAcquisition
{
    ValueTask<VisionCapturedImage> CaptureAsync(
        VisionSourceReference source,
        VisionCaptureRequest request,
        VisionAcquisitionOwner owner,
        CancellationToken cancellationToken);
}
```

它继续作为Workflow唯一需要学习的采集Interface。Runtime根据已发布Source绑定选择OnDemand或BufferedExternal。

### 8.2 Source绑定增加模式

目标增加：

```csharp
public enum EVisionAcquisitionMode
{
    OnDemand = 0,
    BufferedExternal = 1
}
```

`VisionAcquisitionSourceBinding`增加模式及经过验证的Inbox策略。为兼容现有调用方，默认值为`OnDemand`。

BufferedExternal组合验证：

- Provider存在；
- Provider Binding存在；
- Provider设备声明流式能力；
- 触发配置为External；
- Inbox容量、字节预算和最大年龄有效；
- 同一ResourceKey没有第二个BufferedExternal Source；
- 共享策略为未来正式实现的`ExclusiveRun`。

### 8.3 Provider增加可选流式能力

保留现有：

```csharp
IVisionAcquisitionDevice.CaptureAsync(...)
```

增加可选能力，目标职责如下：

```csharp
public interface IVisionStreamingAcquisitionDevice
{
    ValueTask<IVisionAcquisitionStream> StartStreamAsync(
        IVisionProviderFrameSink sink,
        CancellationToken cancellationToken);
}
```

```csharp
public interface IVisionProviderFrameSink
{
    void Publish(VisionProviderFrame frame);
    void Complete(Exception? failure);
}
```

```csharp
public interface IVisionAcquisitionStream : IAsyncDisposable
{
}
```

接口不向Workflow暴露。关键契约：

- `Publish`进入时帧所有权无条件转给Sink；Runtime即使拒绝也负责Dispose。
- `Publish`必须快速、非阻塞且不把异常抛回厂商SDK回调线程。
- `DisposeAsync`完成后不得再调用Sink；它必须等待已经进入的回调退出。
- SDK回调参数、GrabResult、HObject和裸指针不得越过Provider Adapter。
- Provider必须保证`VisionProviderFrame.Image`在SDK回调返回和设备关闭后仍可读。

具体类型名在编码前可做一次接口评审，但上述所有权和停止语义不得弱化。

### 8.4 Metadata增加接收序号

目标在`VisionCaptureMetadata`增加：

```text
ReceivedSequence?
ReceivedAtUtc?
AcquisitionMode
```

OnDemand允许接收序号为空；BufferedExternal必须有值。

V1不增加TriggerId。

## 9. 节点语义

继续使用现有`Vision.CaptureFrame`节点，不新增第二个调度节点。

### OnDemand Source

现有参数继续工作：

```text
Timeout
ExposureMicroseconds
GainDecibels
TriggerMode
```

### BufferedExternal Source

V1语义固定为：

```text
Timeout       等待FIFO出现下一帧的最长时间
TriggerMode   必须与机器Source的External模式一致
曝光/增益     由机器Source/Profile固定；节点级覆盖暂不支持
```

如果节点对BufferedExternal Source配置了曝光或增益覆盖，运行准备必须失败，不能在活动流中静默修改设备参数。

Handler仍然只调用`CaptureAsync`，不直接读取Queue、回调或Provider。

## 10. Runtime内部状态

ResourceSession目标状态：

```text
Created
→ Opening
→ Armed
→ Faulted
→ Stopping
→ Disposed
```

要求：

- `Armed`才接收生产帧；
- `Faulted`拒绝新Claim；
- `Stopping`先关闭接受门，再停止Stream，再等待在途Publish退出；
- 最后释放Inbox、设备和Provider；
- `DisposeAsync`不能与活动回调并发释放设备。

Frame条目状态：

```text
Received
→ Queued
→ Claimed
→ Transferred
```

异常出口：

```text
Queued → Expired
Queued → DisposedAtEpochEnd
Received → RejectedAndFaulted（容量不足）
```

一旦`CaptureAsync`成功返回，Runtime不再拥有该结果；调用方负责Dispose或交给FrameScope。

## 11. 故障与诊断

必须区分：

```text
等待超时
等待取消
Source未布防
Inbox溢出
帧超龄
设备离线
流意外结束
设备序号跳变
并行Claim冲突
根运行所有权冲突
Runtime正在停止
```

至少记录：

```text
SourceId
ResourceKey
ProviderId
OwnerId
OperationId
Epoch
ReceivedSequence
DeviceSequence
InboxCount
InboxBytes
FailureKind
```

运行指标：

```text
FramesReceived
FramesClaimed
FramesExpired
FramesRejected
InboxHighWatermark
BytesHighWatermark
DeviceSequenceGaps
CallbackFaults
```

## 12. 不在V1范围

- TriggerId或产品号精确相关；
- PLC TriggerCounter；
- 相机Chunk Trigger Counter统一抽象；
- Latest生产取帧；
- 多消费者Broadcast；
- 多个SourceId共享一个被动Inbox；
- 连续FreeRun分析流水线；
- 跨进程相机或帧队列；
- 运行中修改曝光/增益并等待设置生效；
- HObject/Mat原生表示缓存；
- Provider自动故障切换。

这些能力以后必须在真实需求和现场设备语义明确后单独设计。

## 13. 分阶段实施

### V1-A：公共模型与Fake Stream【已完成】

1. 增加`EVisionAcquisitionMode`和Inbox策略值对象。
2. 扩展Source绑定和Metadata。
3. 增加Provider可选Streaming Interface。
4. 创建可控Fake Streaming Provider。
5. 保持所有现有OnDemand测试通过。

验收：公共契约无Workflow和厂商SDK类型；Fake可确定性发布、完成和故障。

**实施记录（2026-09-21）**

新增文件（`src/DP.Vision.Acquisition.Abstractions/`）：

```text
EVisionAcquisitionMode.cs      OnDemand=0 / BufferedExternal=1，整数值是显式契约
VisionFrameInboxPolicy.cs      Capacity / ByteBudget / MaximumFrameAge，三者都必须为正
IVisionAcquisitionStream.cs    IVisionStreamingAcquisitionDevice + IVisionProviderFrameSink + IVisionAcquisitionStream
```

演进文件：

```text
VisionCaptureMetadata.cs       改为显式属性 + 两个构造入口（6参保持OnDemand，跨字段不变式在构造期强制）
VisionAcquisitionSourceBinding.cs  增加 AcquisitionMode 与 InboxPolicy，二者必须成对出现
VisionAcquisitionProviderComposer.cs  模式一致性与"一键一源"校验；队列策略进组合身份
```

测试替身（`tests/DP.Vision.Acquisition.Tests/TestDoubles/`）：

```text
FakeStreamingVisionDevice.cs  可布防、可逐帧推动回调、可注入结束与接收方拒绝；不使用计时器
RecordingFrameSink.cs         按序记录帧，可注入拒绝与阻塞；所有权语义按契约实现
TrackingImageSource.cs        观察真实图像释放，避免用被测对象自报的计数自证
```

测试（26 例，双 TFM 各一套）：

```text
Contracts/AcquisitionModeContractTests.cs        11 例  模式/策略/元数据校验与向后兼容
Composition/AcquisitionModeCompositionTests.cs    6 例  组合期可判定的模式约束与组合身份
Concurrency/StreamingFrameSinkContractTests.cs    9 例  交付顺序、停止语义、拒绝不泄漏、释放等待回调
```

**实测**：`DP.Vision.sln` 702 例 0 失败（650 → 702）；`DP.WorkFlow.sln` 0 警告 0 错误（既有 OnDemand 路径未受影响）。

**变异验证**（撤销后全部复绿）：

| 停用/改坏的行为 | 精确变红 |
|---|---|
| Source绑定两条模式校验 | 2 例 |
| Metadata 的 BufferedExternal 字段校验 | 2 例 |
| 同一资源键两个缓冲Source的拒绝 | 1 例 |
| 队列策略进入组合身份 | 1 例 |
| 接收结束后仍交付帧 | 1 例 |
| 释放接收流不等待进行中的回调 | 1 例 |
| 重复布防静默替换接收方 | 1 例 |
| 接收方拒绝后不释放帧 | 2 例 |
| 回调异常逃逸到调用线程 | 1 例 |

**本轮修正的两处测试缺陷**（都不是实现缺陷，但会让用例假绿）：

- `CaptureMetadata_BufferedExternalRequiresReceivedFacts` 初版只断言"抛 `ArgumentException`"。
  本类型还有一条"主动采集不得带接收字段"的分支会用同样的异常类型兜住缺失场景，
  变异后仍然通过。已改为断言失败原因与 `ParamName`，并补一个"字段齐备必须成功"的对照。
- 流式测试初版用接收方自己维护的 `DisposedCount` 断言"帧被释放"——该计数即使不释放也会自增，
  等于自证。已改为观察真实 `IImageSource.Dispose`。

**V1-A 的边界**：本阶段只建立模型与可控替身，**没有** ResourceSession、有界FIFO、超龄、单 Claim、
根运行 Epoch，也没有任何真实厂商回调。`BufferedExternal` 目前只能在组合期发布，
运行期尚无消费路径——它由 V1-B/V1-C 补齐。

### V1-B：Runtime FrameInbox【已完成】

1. 按ResourceKey增加ResourceSession。
2. 实现有界FIFO、字节预算、超龄和单Claim。
3. 实现回调先到、节点先到两种时序。
4. 实现取消与回调竞争的原子所有权转移。
5. 实现溢出FaultSource。
6. 修复Runtime停止门并等待活动Capture和Publish退出。

验收：没有重复领取、租约泄漏、静默覆盖或Dispose竞态。

**实施记录（2026-09-21）**

新增文件：

```text
src/DP.Vision.Acquisition.Runtime/VisionFrameInbox.cs        有界FIFO：容量/字节预算/超龄/代次过滤/高水位
src/DP.Vision.Acquisition.Runtime/VisionResourceSession.cs   资源会话：状态机 + 接收口 + 停止门 + 领取门
src/DP.Vision.Acquisition.Abstractions/VisionSourceDiagnostics.cs   公共诊断快照（状态/代次/持有者/计数）
src/DP.Vision.Acquisition.Abstractions/IVisionAcquisitionRunOwner.cs 采集侧运行所有权（BeginRun/EndRun）
```

演进文件：

```text
src/DP.Vision.Acquisition.Runtime/VisionAcquisitionRuntime.cs  按模式路由；BufferedExternal 经 ResourceSession
src/DP.Vision.Acquisition.Runtime/DP.Vision.Acquisition.Runtime.csproj  加 InternalsVisibleTo（队列白盒单测）
```

设计要点：

- `VisionFrameInbox` **只负责队列语义**，不感知设备与运行；`Enqueue` 的返回值告诉调用方
  "已接管"还是"已由队列释放"，使所有权在**进入点**就确定，避免调用方与队列互相猜测。
- 接收序号（`ReceivedSequence`）在 `Enqueue` 时分配，**即使该帧因满/超龄被拒也照样递增**——
  序号表达"第几次回调到达"，不是"第几帧入队"，否则诊断会因丢帧而失去时间轴。
- `VisionResourceSession` 的**单 Claim 门**用 `SemaphoreSlim(1,1).Wait(0)`：不做排队，
  同一 Source 同时只允许一个等待中的领取，第二个并行请求**确定性冲突**而不是排队。
- 领取时先按代次过滤、再按超龄清退、最后才取队首——三条都在同一个锁内完成，
  所以"上一轮运行的帧"不可能被新一轮领走。
- **停止门**：`DisposeAsync` 先停接收流（契约保证等待已进入的回调退出），再清空队列，
  再唤醒全部等待者令其失败。顺序颠倒会出现"回调仍在进入而队列已释放"。

测试（26 例，双 TFM 各一套）：

```text
Streaming/BufferedExternalInboxTests.cs  15 例  黑盒：两种时序、FIFO、并行领取、取消竞争、
                                                溢出、超龄、跨代次、所有权冲突、停止语义
Streaming/FrameInboxUnitTests.cs         11 例  白盒：容量/字节预算/超龄/代次过滤/高水位/
                                                Drain 所有权转移（这些无法只从公共 API 到达）
```

**实测**：`DP.Vision.sln` 750 例 0 失败（702 → 750）；`DP.WorkFlow.sln` 0 警告 0 错误。

**变异验证**（撤销后全部复绿）：

| 停用/改坏的行为 | 精确变红 |
|---|---|
| 容量与字节预算检查 | 4 例 |
| 超龄判定 | 2 例 |
| 单 Claim 门（并行领取不再冲突） | 1 例 |
| 退役时清空未领取帧 | 1 例 |
| 流结束不再唤醒等待者 | 1 例 |
| 代次过滤（旧代次帧可被领取） | 2 例 |
| 释放会话时不停接收流 | 1 例 |
| 运行结束（租约释放）时不停流 | 2 例 |

**变异验证抓出的覆盖盲区与测试缺陷**：

- `RuntimeDisposal_StopsStreamEvenWithoutLeaseRetirement` **第一版没红**：`StreamDisposeCount`
  被 `Stream.DisposeAsync` 与**设备自身 `DisposeAsync`** 两条路径同时自增，且断言用 `>= 1`，
  于是"释放会话不停流"被"随后释放设备"顶了上去。已把两条路径**分开计数并记录事件顺序**。
- 同一变异还暴露出：`ReturnedFrame_RemainsReadableAfterDeviceDisposed` 断言了同一件事却也没红——
  它被 `DisarmAsync`（运行结束）的停流兜住了。这说明**当时缺少"运行结束即停流、而运行时仍存活"**
  的用例，而这才是生产常态（运行时比单根运行活得久）。已补
  `RunEnd_StopsStreamWhileRuntimeStaysAlive`。
- `VisionFrameInbox.Drain()` 的注释写"释放全部待领取条目"，实现是**把所有权转移给调用方、不释放**。
  测试锁的是后者，注释与实现不符，已更正。

**V1-B 的边界**：`BufferedExternal` 现在运行期**可被领取**，但**根运行 Epoch 尚未接线**——
`BeginRun` 由调用方显式驱动，宿主还没有在首节点前调用它。跨代次过滤已在队列层实现并有测试，
但"上一根运行的帧不会进入下一运行"的端到端保证要等 V1-C。**没有任何真实厂商回调。**

### V1-C：根运行Epoch接线【已完成】

前置：完成AR-01根RunScope资源所有权契约。

1. 根运行在Engine首节点之前取得BufferedExternal Source所有权。
2. 建立新Epoch并清退旧Epoch帧。
3. Nested只继承，不重新布防和清理。
4. 根运行退役时停止/释放本Epoch。
5. 运行准备验证节点与Source模式兼容。

验收：回调可早于Capture节点，但上一根运行帧不会进入下一运行。

**实施记录（2026-09-21）**

前置条件的落地方式：Kernel 不得引用 `DP.Vision`，因此"本轮作用域取得与退役"必须是运行时中立的契约，
视觉侧只提供 Adapter。新增的 `IWorkflowRunScopeOwner` 就是 AR-01 阶段 3 所需的**所有权令牌机制**；
阶段 3 剩下的"文件夹采集会话与帧仓的运行级状态也迁入 RunScope、`WorkflowRunScopeKind` 退场"
仍待处理（见 §13 末"V1-C 的边界"）。

新增文件（`DP.WorkFlow`）：

```text
src/Workflow/Kernel/DP.WorkFlow.Abstractions/Execution/IWorkflowRunScopeOwner.cs
    IWorkflowRunScopeOwner + IWorkflowRunScopeLease：中立的运行级资源作用域契约。
    与 AR-01 阶段 2 的 IWorkflowRunResourceOwner 分工明确：
    后者负责"上一轮资源退役"，故意延迟到下一次根运行开始，使上一轮结果在查看窗口内仍然有效；
    本接口负责"本轮作用域取得与退役"，必须在首节点之前生效、在本轮结束时归还。
src/Workflow/Nodes/DP.WorkFlow.Nodes.Vision/Acquisition/VisionAcquisitionRunScope.cs
    Kernel 与采集侧之间唯一的桥：IWorkflowRunScopeOwner → IVisionAcquisitionRunOwner。
tests/Workflow/DP.WorkFlow.Nodes.Vision.Acquisition.Tests/
    跨层端到端测试工程：DP.WorkFlow 里唯一同时看得见真实采集运行时与工作流根运行宿主的地方。
    生产侧的依赖方向不变（采集节点仍只引用 Acquisition.Abstractions）。
```

演进文件：

```text
src/Workflow/Kernel/DP.WorkFlow.Runtime/Hosting/WorkflowRuntimeHost.cs
    调用顺序固定为：准备 → 上一轮退役 → 本轮取得 → 引擎执行 → 本轮退役（finally）。
src/Workflow/Nodes/DP.WorkFlow.Nodes.Vision/Acquisition/VisionSourceCatalog.cs
    WorkflowVisionSourceInfo 增加 AcquisitionMode（追加在末尾，五参位置调用行为不变）。
src/Workflow/Nodes/DP.WorkFlow.Nodes.Vision/Acquisition/WorkflowVisionFrameScope.cs
    运行准备：缓冲源拒绝节点级曝光/增益覆盖；缓冲源存在但宿主未注册 IWorkflowRunScopeOwner 时
    在首节点之前明确失败；主动采集源声明 ExclusiveRun 的拒绝理由改为"ExclusiveRun 只用于缓冲源"。
samples/DP.WorkFlow.WinForms.Sample/Form1.cs、samples/Legacy/WpfApptest/MainWindow.xaml.cs
    注册桥接，并把 binding.AcquisitionMode 一并发布到源目录。
src/DP.Vision.Acquisition.Runtime/VisionAcquisitionRuntime.cs
    RunLease.ArmAsync 改为复用会话里已打开的设备（见下"顺带修复的真实缺陷"）。
```

设计要点：

- **取得时机只能靠顺序保证**：先准备、后取得。准备校验失败时设备根本没有被打开——跨层用例直接断言
  `Provider.OpenCount == 0`，而不是断言"抛了异常"。
- **嵌套运行是类型级保证**：引擎自身（含恢复子流程路径）一次都不解析 `IWorkflowRunScopeOwner`，
  不依赖调用方传对 Root/Nested 枚举。这条由既有嵌套用例（`WorkflowRunPreparationScopeDeclarationTests`）
  锁住，而不是靠文档约定。
- **端到端用例的触发方式**：文档里放一个"触发节点"排在采集节点之前，它在首节点执行期间推动一次回调。
  这样"回调早于采集节点"是确定性的：宿主若没有在首节点之前布防，设备直接丢弃该回调（`Emit` 返回 false），
  采集节点随后只能超时。不用计时器，也不需要人工等待。

**顺带修复的真实缺陷（DP.Vision）**

`VisionAcquisitionRuntime.RunLease.ArmAsync` 原本每次布防都调用 `OpenDeviceAsync` 重新打开相机，
而 `DisarmAsync` 的契约是"设备保持打开以便下次布防复用"。后果有两条：

1. 第二根根运行会重新打开同一台相机——真实 SDK 通常直接失败（同一进程无法独占打开两次）；
2. 上一根运行持有的设备对象既不会停流也不会被释放，被静默漏掉。

修复为复用会话里已有的设备（`GetOrOpenDeviceAsync`）。回归用例
`SecondRun_ReusesOpenDeviceInsteadOfOpeningCameraAgain` 在未修复代码上两个 TFM 都变红；
跨层用例 `上一根运行未领取的帧不会进入下一根运行` 同时锁住 `Provider.OpenCount == 1`。

测试（新增 17 例）：

```text
DP.WorkFlow.Runtime.Tests/WorkflowRuntimeHostTests.cs                     3 例  取得/退役顺序、无作用域所有者时不取得、取得失败即不执行
DP.WorkFlow.Runtime.Tests/WorkflowRunPreparationScopeDeclarationTests.cs  1 例  嵌套路径一次都不取得
DP.WorkFlow.Nodes.Vision.Tests/VisionAcquisitionNodeTests.cs              3 例  缓冲源缺作用域所有者、拒绝曝光覆盖、注册后可以运行
DP.WorkFlow.Nodes.Vision.Tests/VisionAcquisitionRunScopeTests.cs          5 例  桥接：身份转换、所有权透传、释放转发、构造与取得失败
DP.WorkFlow.Nodes.Vision.Acquisition.Tests/                               6 例  跨层端到端
DP.Vision.Acquisition.Tests/BufferedExternalInboxTests.cs                 1 例  第二根根运行复用同一设备（双 TFM 各一套）
```

跨层端到端 6 例：

```text
回调早于采集节点到达时采集节点直接领取        触发节点在首节点执行期间推帧；宿主若未先布防，回调会被丢弃
根运行退役后相机停流且不再交付回调            退役后 Emit 必须返回 false
上一根运行未领取的帧不会进入下一根运行        第二根只拿到新代次的帧；相机只被打开一次
运行准备校验失败时设备没有被打开              顺序：准备校验 → 取得作用域
已有根运行持有采集所有权时本轮在首节点前失败  冲突在取得阶段暴露，不抢走别人的相机
处置子流程不清空父运行的待领取队列            嵌套运行不重新布防、不清空队列
```

**实测**：`DP.WorkFlow.sln` **834 例 0 失败**（817 → 834），Debug 构建 0 警告 0 错误；
`DP.Vision.sln` **752 例 0 失败**（750 → 752）。

**变异验证**（撤销后全部复绿）：

| 停用/改坏的行为 | 精确变红 |
|---|---|
| 宿主不在首节点之前取得运行作用域 | 8 例（跨 3 个工程） |
| 宿主在运行结束时不退役运行作用域 | 4 例 |
| 运行准备不拒绝缓冲源的曝光/增益覆盖 | 2 例 |
| 运行准备不检查作用域所有者是否存在 | 1 例 |
| 布防时重新打开相机而不是复用设备 | 3 例（DP.Vision 双 TFM + 跨层 1 例） |
| 桥接不转发租约释放 | 1 例 |
| 桥接把运行身份写成带连字符格式 | 1 例 |

**V1-C 的边界**：接线与所有权语义已完成，但**没有任何真实厂商回调**——OnDemand 与 BufferedExternal
都只由可控假流式设备驱动；真实长连接、断线、重连与停流时序属于 V1-D/V1-E。AR-01 阶段 3 只落地了
所有权令牌机制，`WorkflowVisionAcquisitionSession` 仍兼"文件夹采集会话 + 帧作用域准备"两职责，
`WorkflowRunScopeKind` 尚未退场。

### V1-D：Basler真实回调Adapter【软件结构验收已完成 · 现场验收待做】

1. 将设备Open/Close移到真实长连接Session。
2. 接入pylon ImageGrabbed或等价连续取流机制。
3. 在回调边界复制到独立`IImageSource`。
4. 保留DeviceSequence、时间和像素格式。
5. 验证Stop后无回调、断线进入Faulted。

验收分为软件结构验收和真实相机现场验收，不能混写。

#### 软件结构验收（已完成）

新增两个 **SDK 无关的窄接口**（`BaslerStreamContracts.cs`），把"厂商侧动作"与"流式纪律"分开：

```csharp
internal interface IBaslerGrabFrame : IDisposable   // 一帧设备数据的中立视图：格式名、尺寸、帧序号、时刻、转换
internal interface IBaslerStreamCamera : IDisposable // Open/Close、ApplyArmParameters、Start/StopContinuousGrab
```

好处是回调边界、像素落地与停止语义都可以用**可控假相机**在**没有相机、没有 pylon 运行时的机器上**
确定性验证（逐帧推动，不用计时器）；真实实现 `PylonStreamCamera` / `PylonGrabFrame` 只在 `BASLER_SDK`
下编译。`BaslerAcquisitionDevice` 通过内部构造函数接收设备工厂，测试注入假相机。

落地内容：

- `BaslerNeutralFrames.Copy` 成为**唯一的像素落地实现**，OnDemand 与回调交付共用同一条路径
  （两条路径各写一份，"行填充处理"和"尺寸校验"必然各自漂移）。尺寸校验保留为两步：先问设备侧
  "这个转换要多少字节"，再与中立布局比对。
- `BaslerStreamSession`（`IVisionAcquisitionStream`）承担回调边界与停止语义：
  - 所有权：帧进入 `sink.Publish` 即转移，会话不再释放；像素落地失败时帧从未离开会话，由会话释放。
  - 线程：回调内**所有**异常（含接收方违约、设备帧释放失败）都被吞掉并计入诊断。pylon 明确规定
    回调抛异常会向外传播、且**事件通知在抛异常后停止**——一次逃逸就等于永久静默停流。
  - 停止：先关交付口 → `StopContinuousGrab` → 等 `_inFlight` 归零。停流后仍在途的帧被拒绝并计数，
    不会漏给接收方。
- `BaslerAcquisitionDevice` 实现 `IVisionStreamingAcquisitionDevice`：相机**跨布防复用**，只有设备释放时
  才关闭；上一次布防已停止时允许重新布防（宿主每根根运行都会重新布防），仍在进行中则明确拒绝。
  布防参数来自机器配置（绑定声明了 `triggerSource` 就显式设为外部触发，否则保持设备当前设置）；
  缓冲源不接受节点级曝光/增益覆盖，与运行前校验一致。
- **运行时侧**：`VisionResourceSession.Complete(failure)` 现在把会话标为 `Faulted`（正常停止时状态已是
  `Stopping`，`MarkFaulted` 直接返回，不会把退役误判成故障）。于是"断线"表现为带原因的故障态，
  后续领取立刻拿到诊断而不是等到超时。

**从 pylon 包内 XML 文档核实的事实**（不是猜的，写进注释以免后人重复踩）：

| 事实 | 后果 |
|---|---|
| `ImageGrabbed` 在 `IStreamGrabber` 上；`GrabResult` **在事件返回后由 pylon 释放** | 必须在此前复制像素；`PylonGrabFrame.ForCallback` 的 `Dispose` 是空操作 |
| 回调抛异常会向外传播，且**事件通知在抛异常后停止** | 回调内绝不能抛；这条直接决定了会话的吞异常设计 |
| `IGrabResult.ImageNumber` 从 1 开始，且**每次 `IStreamGrabber.Start` 复位** | DeviceSequence 不是跨运行单调的；运行时的丢帧检测不会因此误报，但不能当全局序号用 |
| `IGrabResult.Timestamp` 是相机私有刻度，需配合 `GevTimestampTickFrequency` 且部分相机返回 0 | 本版不换算；`CapturedAtUtc` 取回调边界观测时刻，现场验收清单里保留"设备时间戳"一项 |
| `RetrieveResult` 得到的抓图结果由调用方释放，与回调帧相反 | `PylonGrabFrame` 用 `ForRetrieved` / `ForCallback` 两个入口区分所有权，避免漏释放或重复释放 |

测试（新增 25 例，双 TFM 各一套）：

```text
BaslerStreamSessionTests.cs          11 例  布防顺序、回调帧落地、宽位深布局、格式不支持、尺寸不一致、
                                            接收方违约不逃逸、断线结束只一次、停流后拒绝、在途回调等待、幂等
BaslerAcquisitionDeviceStreamTests.cs 10 例  布防触发模式、交付、第二根运行复用相机、重复布防拒绝、
                                            布防失败保持相机打开、布防期间拒绝主动采集、释放顺序、幂等、
                                            惰性创建、释放后拒绝布防
BaslerNeutralFramesTests.cs           3 例  直接可复制格式仍走设备转换器、尺寸不一致拒绝、格式不支持拒绝
```

**实测**：`DP.Vision.Basler.Tests` **82 例 0 失败**（57 → 82，双 TFM）；`DP.Vision.sln` **806 例 0 失败**
（6 个工程 × 双 TFM，0 错误）。`BaslerSdkEnabled=false` 的配置同样 0 警告 0 错误。

> **口径提醒**：`DP.Vision` 全量测试前必须补 `HALCONROOT`（本机 User 级已设为
> `C:\Program Files\MVTec\HALCON-23.11-Progress`，但**不会自动出现在本会话的进程环境里**）。
> 缺它时 `DP.Vision.Halcon.Tests` 的条件编译 `HALCON_SDK` 不成立，总数少 4（2 例 × 双 TFM），
> 而且 `DP.Vision.Halcon` 会被构建成无 SDK 版本。此前文档记录的 752 就是缺 `HALCONROOT` 时的口径。

**变异验证**（12 项，撤销后全部复绿；"红灯数"含双 TFM）：

| 停用/改坏的行为 | 精确变红 |
|---|---|
| 会话：停止后仍然交付帧 | 4 例（2 用例） |
| 会话：释放不等待在途回调 | 2 例（1 用例） |
| 会话：回调返回时不释放设备帧 | 12 例（6 用例） |
| 会话：结束通知不设唯一性 | 2 例（1 用例） |
| 会话：像素落地失败时静默丢帧 | 4 例（2 用例） |
| 会话：布防不写设备参数 | 6 例（3 用例） |
| 中立帧：跳过转换尺寸校验 | 4 例（2 用例） |
| 中立帧：直接复制而不走设备转换器 | 6 例（3 用例） |
| 设备：已停止的布防不允许重新布防 | 2 例（1 用例） |
| 设备：释放时先关相机再停流 | 2 例（1 用例） |
| 设备：布防失败时关闭相机 | 2 例（1 用例） |
| 设备：布防触发模式恒为保持当前 | 4 例（2 用例） |

> 其中"已停止的布防不允许重新布防"这一项是**测试先抓出来的真实缺陷**：宿主每根根运行都会重新布防，
> 而设备把已停止的会话继续当成"仍在布防"，第二根根运行会直接失败。第一版实现只写了
> `if (_session is not null) throw`，是这条用例把它顶出来的。

#### 真实相机现场验收（未做）

以下只能在装有 pylon 运行时与真实相机（且具备外部触发接线）的现场签署，**不能用假相机替代**：

- `ImageGrabbed` 的真实线程行为与回调频率上限；
- 停流时序：`StreamGrabber.Stop()` 期间在途回调的实际数量与耗时；
- 断线、重连、丢帧（`SkippedImageCount`）与 `ErrorCode` 的实际取值；
- 外部触发脉宽/极性/触发源，以及曝光时间对最大触发频率的限制；
- DeviceSequence 复位行为的实测确认（本版依据的是包内文档）；
- 长时间吞吐下的内存与租约回收。


### V1-E：HALCON真实流式Adapter【软件结构验收已完成 · 现场验收待做】

1. 使用HALCON支持的异步Grab循环或明确的回调机制。
2. 不把HObject/HFramegrabber越过Provider Interface。
3. 明确External触发源与KeepCurrent语义。
4. 验证超时、停止和许可证故障。

验收分为软件结构验收和真实相机现场验收，不能混写。

#### 与 V1-D 的根本差异：HALCON 没有事件回调

pylon 由 SDK 在 `ImageGrabbed` 回调线程上推帧，会话只需守纪律。**HALCON 没有等价的事件回调**，
只有阻塞式异步抓取，因此采集循环、线程所有权、停止等待与"停流后不得再交付"**必须由会话自建**，
不能指望 SDK 提供任何保证。这是 V1-E 与 V1-D 唯一的结构性差异，也是本轮的主要工作量。

#### 软件结构验收（已完成）

沿用 V1-D 的骨架，新增两个 **SDK 无关的窄接口**（`HalconStreamContracts.cs`），把"厂商侧动作"与
"流式纪律"分开：

```csharp
internal interface IHalconGrabFrame : IDisposable      // 一帧设备数据的中立视图：尺寸、像素类型、通道数、时刻、按通道复制
internal interface IHalconStreamCamera : IDisposable   // Open/Close、ApplyArmParameters、GrabOnce、AbortGrab
```

`IHalconGrabFrame` **刻意没有 ImageNumber**（见下方"能力差异"）。真实实现
`HalconFramegrabberCamera` / `HObjectGrabFrame` 只在 `HALCON_SDK` 下编译，其余部分（会话、故障分类、
像素落地）**无条件编译**，因此可以在没有相机、没有 HALCON 许可证的机器上确定性验证；
`HalconAcquisitionDevice` 通过内部构造函数接收设备工厂，测试注入假相机。

落地内容：

- `HalconNeutralFrames.Copy` 成为**唯一的像素落地实现**，OnDemand（`HalconImageSource.CopyFrom`）
  与长连接交付共用同一条路径。此前两条路径各有一份 `#if HALCON_SDK` 像素代码，是漂移的来源；
  合并后 OnDemand 侧的诊断文本逐字不变。
- `HalconStreamSession`（`IVisionAcquisitionStream`）**自建采集线程**：
  - 线程所有权：`Arm` 打开设备、写布防参数，然后启动一条 `IsBackground` 线程
    （`DP.Vision.Halcon.GrabLoop`），循环 `GrabOnce` → 像素落地 → `Publish`。
    交付**运行在采集线程上**，因此"等采集线程退出"同时就是"等已经进入 `Publish` 的交付退出"。
    这是**刻意依赖**的结构事实，已写进 `DisposeAsync` 的注释（含"若将来改用
    `set_framegrabber_callback` 把交付挪到别的线程，必须补回在途计数与等待"的警示）。
    这里刻意**没有**额外的"等在途计数归零"循环：在该结构下它永远不会生效（变异验证确认过）。
  - 停止：先关交付口 → `AbortGrab()`（尽力而为）→ 等采集线程退出。`DisposeAsync` 返回时
    不再有任何交付，且在途设备帧已释放。
  - 停流后到达的帧被拒绝、计数并**释放**，不交给接收方。
  - 接收方 `Publish` 抛异常被吞掉并计数，**绝不逃逸到采集线程之外**。
- `HalconAcquisitionDevice` 实现 `IVisionStreamingAcquisitionDevice`：相机**跨布防复用**（只在设备
  释放时才关闭）；上一次布防**已停止**时允许重新布防（宿主每根根运行都会重新布防），仍在进行中才
  拒绝；释放顺序先停流再关设备；布防期间拒绝节点级单次采集。
- 触发三态与 `KeepCurrent` 语义：`KeepCurrent` → 打开参数 `'default'`（**一个触发参数都不写**）；
  `FreeRun` → `'false'`；`External` → `'true'` + `TriggerSelector=FrameStart` + `TriggerSource` +
  `TriggerMode=On`（`TriggerSource` 由私有配置显式声明，不猜物理接线）；`Software` **明确拒绝**
  （`VisionParameterNotSupportedException`），不静默按自由运行采集。
  > 这里修掉一个既有缺陷：原实现把 `KeepCurrent` 也映射成 `'false'`，即"保持设备当前设置"
  > 实际会**显式关闭外部触发**，把硬件触发的相机改成自由运行。
- 故障分类（`HalconStreamFaults`，无 SDK 依赖、可确定性验证）：抓取超时（5322）→ 只计诊断并继续等
  下一轮，**不结束流**；许可证类 → `VisionProviderUnavailableException`；图像采集类（53xx）→
  `VisionDeviceOfflineException`；其余 → `VisionDataException`。超时、许可证与设备故障**必须分开**
  ——现场排查路径完全不同。

**从本机 HALCON 23.11 安装核实的事实**（不是猜的，写进注释以免后人重复踩）：

| 事实 | 后果 |
|---|---|
| 官方连续取流写法（`examples/hdevelop/Image/Acquisition/genicamtl_simple.hdev`）：`grab_image_start(-1)` → 循环 `grab_image_async(Image, Acq, -1)` → 停止用 `set_framegrabber_param('do_abort_grab', -1)` | 采集循环必须自建；`MaxDelay=-1` 表示**停用"图像太旧就丢"**，缓冲源要每一帧 |
| `do_abort_grab` 是**尽力而为**（"if the specific image acquisition interface supports it"）；它是"多线程并发使用"规则的唯一例外，可从另一线程调用 | 停止等待的上界＝布防时写入的 `grab_timeout`：中止成功则立即收敛，不支持则退化为等满超时 |
| `H_ERR_FGTIMEOUT`(5322) 表示"这一轮没等到帧"，外部触发下属正常现象；`H_ERR_TIMEOUT`(9400) 是通用超时，可能来自任何算子 | 只有 5322 计为抓取超时并继续；把 9400 也当抓取超时会让真故障被静默重试掉 |
| 许可证错误码**不构成连续区间**（`include/HErrorDef.h`）：2000/2001/2002 分别是 `H_ERR_WNP`/`H_ERR_HONI`/`H_ERR_WRKNN`，2100–2108、2200+ 也是别的错误类；2300–2399 整段是 `H_ERR_LIC_*` | 2003–2091 必须**逐个列出**，不能写 `>= 2000 && <= 2091` |
| HALCON **不提供设备帧序号**：通用采集参数表里没有帧计数项 | 中立帧 `DeviceSequence` 上报 `null`（`long?` 合法）。这是 Provider 能力差异，不是缺陷 |
| `HOperatorException` 的 `GetErrorNumber()`/`GetErrorText()` 已过时，现行 API 是 `GetErrorCode()`/`GetErrorMessage()`；继承链 `HOperatorException → HalconException → ApplicationException` | 分类器按现行访问器读错误码 |
| `HTuple` 与 `string` **双向隐式转换** | `SetFramegrabberParam(name, tuple)` 会造成 CS0121 二义性，两个实参都必须显式写成 `HTuple` |
| `grab_image_async` 把图像所有权交给调用方（与 pylon 回调帧相反） | `HObjectGrabFrame` 用 `Borrow`/`Own` 两个**具名工厂**区分所有权，写在调用点上而不是靠布尔参数 |

测试（新增 77 例，双 TFM 各一套）：

```text
HalconStreamSessionTests.cs            14 例  布防顺序、中立帧交付与 DeviceSequence 为空、抓取超时不算故障、
                                              设备故障只结束一次、许可证故障分类、像素落地失败结束流并释放帧、
                                              接收方违约不逃逸不结束流、停流后拒绝并释放、Dispose 等采集线程退出、
                                              幂等、未布防即释放、布防失败保持相机打开可重试、
                                              布防失败后会话仍可释放、释放不关闭设备
HalconAcquisitionDeviceStreamTests.cs  11 例  布防触发模式、交付、第二根运行复用相机、重复布防拒绝、
                                              布防失败保持相机打开、布防期间拒绝主动采集、释放顺序、幂等、
                                              未布防释放不碰相机、释放后拒绝布防
HalconNeutralFramesTests.cs            12 例  Gray8 / Gray16 / BGR 交错 / 不支持布局（5 组）/ 预算 / 取消 /
                                              null / 中立图像独立于设备帧存活
HalconStreamFaultsTests.cs             31 例  抓取超时、通用超时不算、许可证码分类、同区间非许可证码不被误判、
                                              区间端点、设备码分类、两类可区分、兜底分类、缺消息仍可读
HalconTriggerMappingTests.cs            4 例  保持当前不写触发参数（且不等于 'false'）、自由运行关闭外部触发、
                                              外部触发打开、软件触发明确拒绝
HalconAcquisitionProviderPluginTests.cs +5 例 私有配置 triggerSource / grabTimeoutMilliseconds 默认值与解析、
                                              非法输入（超时非正/非整数、触发源非字符串）
```

**实测**：`DP.Vision.Halcon.Tests` **102 例 0 失败**（25 → 102，双 TFM）；`DP.Vision.sln`
**960 例 0 失败**（6 个工程 × 双 TFM，12 个运行条目，0 错误）。

**变异验证**（20 项，撤销后全部复绿；"红灯数"含双 TFM）：

| 改坏的行为 | 精确变红 |
|---|---|
| 会话：停止后仍然交付帧 | 1 例 |
| 会话：释放不等待采集线程退出 | 1 例 |
| 会话：循环返回时不释放设备帧 | 5 例 |
| 会话：抓取超时被当成故障 | 1 例 |
| 会话：布防失败不关交付口 | 2 例 |
| 会话：释放时关闭设备 | 4 例 |
| 会话：交付不检查停止 | 3 例 |
| 设备：已停止的布防不允许重新布防 | 1 例 |
| 设备：释放先关设备再停流 | 1 例 |
| 设备：布防失败时关闭设备 | 1 例 |
| 设备：布防期间允许单次采集 | 1 例 |
| 设备：布防触发模式恒为保持当前 | 2 例 |
| 中立帧：跳过预算校验 | 2 例 |
| 中立帧：BGR 交错写成 RGB | 2 例 |
| 中立帧：不支持的通道数按三通道处理 | 1 例 |
| 中立帧：去掉取消检查 | 1 例 |
| 故障：许可证按 2xxx 区间判断（复现原始错误） | 8 例 |
| 故障：通用超时也当成抓取超时 | 1 例 |
| 触发：保持当前写成关闭外部触发（复现原始缺陷） | 1 例 |
| 触发：软件触发静默按自由运行 | 1 例 |

> 变异验证还抓出一处**死代码**：会话里原本照抄 V1-D 写了一段"等 `_inFlight` 归零"的等待循环，
> 但在"交付与采集循环同线程"的结构下它永远不会生效（改坏后测试仍然全绿）。按发现**删除代码**
> 而不是弱化断言，并把这条结构依赖写成注释。
>
> 另有一项变异会让用例永久阻塞（把"释放时中止抓取"换成"释放时关闭设备"，假相机不会唤醒阻塞中的
> 抓取）——变异脚本因此加了子进程超时兜底与"挂起"判定。**续跑必须跳过基线**，否则会把未还原的
> 变异当成基线。

#### 真实相机现场验收（未做）

以下只能在装有 HALCON 运行时与真实相机（且具备外部触发接线）的现场签署，**不能用假相机替代**：

- 目标采集接口**是否真的支持 `do_abort_grab`**（不支持时停止耗时会等于 `grab_timeout`）；
- `grab_image_async` 的真实取流频率上限与 CPU 占用；
- 停流时序：`AbortGrab` 之后在途帧的实际数量与耗时；
- 断线、重连与 `H_ERR_FG*` 的实际取值；
- 外部触发脉宽/极性/触发源，以及曝光时间对最大触发频率的限制；
- 长时间吞吐下的内存与租约回收（`HObject` 是否真的全部释放）。

### V1-F：运行审计与长期验证

1. 将模式、Epoch、ReceivedSequence和设备序号写入运行Trace/Artifact。
2. 增加Inbox与丢帧指标。
3. 执行长时间外触发、突发触发、断线、重连和关闭测试。

## 14. 自动化测试清单

至少覆盖：

1. OnDemand行为完全回归。
2. 回调先到，Capture立即领取。
3. Capture先等待，回调后完成。
4. 三帧按FIFO顺序领取。
5. 两个并行Claim确定性拒绝其中一个。
6. 取消等待不吞掉下一帧。
7. 取消与回调同时发生时帧只有一个所有者。
8. Inbox达到容量后Source进入Faulted且新帧被释放。
9. 超龄帧被释放，不能成功返回。
10. 新Epoch不能领取旧Epoch帧。
11. Nested准备不清空父Epoch。
12. 根运行所有权冲突明确失败。
13. DeviceSequence原样进入Metadata。
14. Stream异常完成后所有等待者失败。
15. Dispose等待在途Publish退出。
16. Dispose后不接受新Capture或回调。
17. 每个被拒绝、超龄和未领取帧最终只Dispose一次。
18. Provider设备关闭后，已返回Frame仍可读。

当前覆盖（V1-E 完成时）：

| 项 | 状态 | 覆盖用例 |
|---|---|---|
| 1 OnDemand 完全回归 | 已覆盖 | 既有 56 例 + 5参数绑定/6参数元数据向后兼容用例 |
| 2 回调先到，Capture 立即领取 | 已覆盖 | `CallbackBeforeCapture_IsClaimedImmediately` |
| 3 Capture 先等待，回调后完成 | 已覆盖 | `CaptureBeforeCallback_CompletesWhenFrameArrives` |
| 4 三帧按 FIFO 顺序领取 | 已覆盖 | `ThreeFrames_AreClaimedInFifoOrder`、`Claim_ReturnsFramesInArrivalOrder` |
| 5 两个并行 Claim 确定性拒绝其一 | 已覆盖 | `ParallelClaims_RejectExactlyOneDeterministically` |
| 6 取消等待不吞掉下一帧 | 已覆盖 | `CancelledClaim_DoesNotSwallowNextFrame` |
| 7 取消与回调同时发生时帧只有一个所有者 | 已覆盖 | `CancelRacingCallback_LeavesFrameWithSingleOwner` |
| 8 容量满后 Source 进入 Faulted 且新帧被释放 | 已覆盖 | `InboxOverflow_FaultsSourceAndReleasesFrames`、`Enqueue_RejectsWhenCapacityReached`、`Enqueue_RejectsWhenByteBudgetExceeded` |
| 9 超龄帧被释放且不能成功返回 | 已覆盖 | `ExpiredFrame_IsReleasedAndNeverReturned`、`Claim_DropsExpiredFramesInsteadOfReturningThem` |
| 10 新 Epoch 不能领取旧 Epoch 帧 | 已覆盖 | `PreviousRunFrames_DoNotEnterNextRun`、`Claim_DoesNotReturnFramesFromAnotherEpoch`、`Claim_RejectsStaleEpochEvenWhenInboxWasNotDrained`、`DropStaleEpochs_KeepsOnlyCurrentEpoch` |
| 11 Nested 准备不清空父 Epoch | 已覆盖 | `处置子流程不清空父运行的待领取队列`（真实宿主 + 真实采集运行时：嵌套运行后第二个采集节点仍能领到父运行布防期间到达的第二帧，且布防次数保持 1）、`恢复子流程的准备请求声明嵌套作用域并携带父节点ID`（引擎路径一次都不解析 `IWorkflowRunScopeOwner`） |
| 12 根运行所有权冲突明确失败 | 已覆盖 | `SecondRootRun_ConflictsWithHolderIdentity` |
| 13 DeviceSequence 原样进入 Metadata | 已覆盖 | `Stream_DeliversFramesInArrivalOrder`、`CaptureMetadata_BufferedExternalPreservesReceivedFacts` |
| 14 Stream 异常完成后所有等待者失败 | 已覆盖 | `StreamCompletion_FailsWaitersImmediately` |
| 15 Dispose 等待在途 Publish 退出 | 已覆盖 | `Stream_DisposeWaitsForInFlightCallback` |
| 16 Dispose 后不接受新 Capture 或回调 | 已覆盖 | `Stream_NoDeliveryAfterStreamDisposed`、`Device_DisposeEndsStream`、`RuntimeDisposal_StopsStreamEvenWithoutLeaseRetirement`、`RunEnd_StopsStreamWhileRuntimeStaysAlive` |
| 17 每个被拒绝、超龄和未领取帧只 Dispose 一次 | 已覆盖 | `EveryFrame_IsDisposedExactlyOnce`（观察真实 `IImageSource`）、`Drain_ReturnsAllEntriesWithoutReleasingThem` |
| 18 Provider 设备关闭后已返回 Frame 仍可读 | 已覆盖 | `ReturnedFrame_RemainsReadableAfterDeviceDisposed` |
| 19 队列高水位可观测 | 补充 | `HighWatermarks_TrackPeakOccupancy`、`Enqueue_AssignsMonotonicSequenceEvenWhenRejected` |
| 20 宿主在首节点前取得 Source 所有权（端到端） | 已覆盖（V1-C） | `回调早于采集节点到达时采集节点直接领取`、`根运行退役后相机停流且不再交付回调`、`运行准备校验失败时设备没有被打开`、`已有根运行持有采集所有权时本轮在首节点前失败` |
| 21 真实 Adapter 的布防/停止设备侧顺序 | 已覆盖（V1-D 软件结构） | `Arm_OpensCameraAppliesParametersAndStartsGrab`、`DeviceDispose_StopsStreamBeforeClosingCamera` |
| 22 真实 Adapter 的回调边界纪律 | 已覆盖（V1-D 软件结构） | `Frame_IsDeliveredAsNeutralImageWithDeviceSequence`、`SinkThrow_DoesNotEscapeCallbackAndDoesNotEndStream`、`Dispose_WaitsForInFlightCallback`、`Dispose_LateInFlightFrame_IsRejectedNotForwarded`、`UnsupportedPixelFormat_CompletesStreamAndReleasesFrame` |
| 23 真实 Adapter 的设备复用与重复布防 | 已覆盖（V1-D 软件结构） | `SecondArm_ReusesOpenCameraInsteadOfOpeningAgain`、`SecondArm_WhileArmed_IsRejectedWithoutReplacingSink`、`ArmFailure_KeepsCameraOpenForRetry`、`CaptureAsync_WhileArmed_IsRejected` |
| 24 断线进入故障态 | 已覆盖（V1-D） | `StreamFailure_EndsStreamOnceWithReason`（适配器侧上报一次）+ `StreamCompletion_FailsWaitersImmediately`（运行时侧标为 `StreamFailure` 且是终态） |
| 25 真实 Adapter 的自建采集线程纪律（HALCON） | 已覆盖（V1-E 软件结构） | `Frame_IsDeliveredAsNeutralImageWithNullDeviceSequence`、`SinkThrow_DoesNotEscapeAndDoesNotEndStream`、`Dispose_RejectsFrameArrivingAfterStopAndReleasesIt`、`Dispose_IsIdempotent`、`Dispose_StopsStreamWithoutClosingCamera`、`ConversionFailure_EndsStreamAndReleasesFrame` |
| 26 真实 Adapter 的布防/停止设备侧顺序（HALCON） | 已覆盖（V1-E 软件结构） | `Arm_OpensConfiguresAndStartsGrabbing`、`DeviceDispose_StopsStreamBeforeClosingCamera`、`SecondArm_ReusesOpenCameraInsteadOfOpeningAgain`、`ArmFailure_KeepsCameraOpenForRetry`、`CaptureAsync_WhileArmed_IsRejected` |
| 27 HALCON 超时 / 许可证 / 设备故障三分（不能混） | 已覆盖（V1-E 软件结构） | `GrabTimeout_IsCountedAndDoesNotEndStream`、`GenericTimeout_IsNotAGrabTimeout`、`LicenseFault_IsReportedAsProviderUnavailable`、`DeviceFault_EndsStreamOnceWithReason`、`NonLicenseCodesInTheSameRange_AreNotClassifiedAsLicense` |
| 28 `KeepCurrent` 不写触发参数（不得退化为关闭外部触发） | 已覆盖（V1-E 软件结构） | `KeepCurrent_LeavesTheDeviceTriggerSettingUntouched`（断言 `'default'` 且 `AreNotEqual("false", …)`）、`Software_IsExplicitlyRejected` |

**仍未覆盖（属于 V1-D/V1-E 现场验收与 V1-F）**：真实相机回调下的断线、重连与停流时序；
DeviceSequence 在真实设备上的复位行为与可靠性；`ImageGrabbed` 的真实线程行为与吞吐上限；
HALCON 侧 `do_abort_grab` 在目标采集接口上是否真的支持、`grab_image_async` 的真实频率上限；
长时间内存与租约回收。这些只能在现场验收（§15）中签署，不能用假设备替代——V1-D/V1-E 的软件结构
验收只证明"接线与纪律正确"，不证明"真实相机上一定成立"。

## 15. 现场验收清单

自动化测试不能替代：

- 外部触发脉宽、极性和触发源；
- 相机SDK回调线程行为；
- DeviceSequence是否可靠及何时复位；
- 回调突发下的最大安全队列深度；
- 网络丢包、重传和帧丢失；
- 曝光时间对最大触发频率的限制；
- 停流、断线和重连；
- HALCON `do_abort_grab` 在目标采集接口上是否真的支持，以及不支持时的停止耗时（上界是 `grab_timeout`）；
- HALCON `grab_image_async` 的真实取流频率上限与 CPU 占用；
- HALCON许可证与Basler运行时部署；
- 长时间吞吐、内存和租约回收；
- 设备安全互锁和跨进程独占。

## 16. 完成定义

只有同时满足以下条件，才能声明BufferedExternal V1完成：

1. 两种时序（帧先到/节点先到）均通过自动化测试。
2. FIFO、容量、超龄、Epoch和所有权语义均有接口级测试。
3. Runtime关闭不再与活动Capture/Publish竞态。
4. 根运行与Nested的资源所有权完成结构性收口。（**采集侧已满足**：根宿主是唯一解析
   `IWorkflowRunScopeOwner` 的位置，Nested 在类型上拿不到；AR-01 阶段 3 的其余运行级状态迁移不在本版范围。）
5. 至少一个真实Provider完成长连接外触发回调现场验收。（**未满足**：Basler（V1-D）与 HALCON（V1-E）
   的**软件结构验收均已完成**，但两个 Provider 的真实相机现场验收都待做——本机无相机，
   且验收必须现场签署，不能用假相机替代。）
6. 文档明确列出另一个Provider未验收的状态。（**已满足**：本节与 §13 V1-D/V1-E 的标题、现场验收清单
   均标注"软件结构验收已完成 · 现场验收待做"。）
7. 没有把FrameInbox暴露为Workflow公共帧仓。
8. 没有引入TriggerId、Broadcast或厂商SDK公共类型。
