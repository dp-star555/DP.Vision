# 图像采集深化 V1：主动请求与外部回调 FIFO

状态：**V1-A、V1-B 已实施；V1-C 起待实现**
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

### V1-C：根运行Epoch接线

前置：完成AR-01根RunScope资源所有权契约。

1. 根运行在Engine首节点之前取得BufferedExternal Source所有权。
2. 建立新Epoch并清退旧Epoch帧。
3. Nested只继承，不重新布防和清理。
4. 根运行退役时停止/释放本Epoch。
5. 运行准备验证节点与Source模式兼容。

验收：回调可早于Capture节点，但上一根运行帧不会进入下一运行。

### V1-D：Basler真实回调Adapter

1. 将设备Open/Close移到真实长连接Session。
2. 接入pylon ImageGrabbed或等价连续取流机制。
3. 在回调边界复制到独立`IImageSource`。
4. 保留DeviceSequence、时间和像素格式。
5. 验证Stop后无回调、断线进入Faulted。

验收分为软件结构验收和真实相机现场验收，不能混写。

### V1-E：HALCON真实流式Adapter

1. 使用HALCON支持的异步Grab循环或明确的回调机制。
2. 不把HObject/HFramegrabber越过Provider Interface。
3. 明确External触发源与KeepCurrent语义。
4. 验证超时、停止和许可证故障。

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

当前覆盖（V1-B 完成时）：

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
| 11 Nested 准备不清空父 Epoch | 待 V1-C | 需要宿主侧运行所有权接线 |
| 12 根运行所有权冲突明确失败 | 已覆盖 | `SecondRootRun_ConflictsWithHolderIdentity` |
| 13 DeviceSequence 原样进入 Metadata | 已覆盖 | `Stream_DeliversFramesInArrivalOrder`、`CaptureMetadata_BufferedExternalPreservesReceivedFacts` |
| 14 Stream 异常完成后所有等待者失败 | 已覆盖 | `StreamCompletion_FailsWaitersImmediately` |
| 15 Dispose 等待在途 Publish 退出 | 已覆盖 | `Stream_DisposeWaitsForInFlightCallback` |
| 16 Dispose 后不接受新 Capture 或回调 | 已覆盖 | `Stream_NoDeliveryAfterStreamDisposed`、`Device_DisposeEndsStream`、`RuntimeDisposal_StopsStreamEvenWithoutLeaseRetirement`、`RunEnd_StopsStreamWhileRuntimeStaysAlive` |
| 17 每个被拒绝、超龄和未领取帧只 Dispose 一次 | 已覆盖 | `EveryFrame_IsDisposedExactlyOnce`（观察真实 `IImageSource`）、`Drain_ReturnsAllEntriesWithoutReleasingThem` |
| 18 Provider 设备关闭后已返回 Frame 仍可读 | 已覆盖 | `ReturnedFrame_RemainsReadableAfterDeviceDisposed` |
| 19 队列高水位可观测 | 补充 | `HighWatermarks_TrackPeakOccupancy`、`Enqueue_AssignsMonotonicSequenceEvenWhenRejected` |

**仍未覆盖（属于 V1-C/V1-D/E）**：宿主在首节点前取得 Source 所有权的端到端路径、
Nested 继承语义、真实厂商回调下的断线与停止。

## 15. 现场验收清单

自动化测试不能替代：

- 外部触发脉宽、极性和触发源；
- 相机SDK回调线程行为；
- DeviceSequence是否可靠及何时复位；
- 回调突发下的最大安全队列深度；
- 网络丢包、重传和帧丢失；
- 曝光时间对最大触发频率的限制；
- 停流、断线和重连；
- HALCON许可证与Basler运行时部署；
- 长时间吞吐、内存和租约回收；
- 设备安全互锁和跨进程独占。

## 16. 完成定义

只有同时满足以下条件，才能声明BufferedExternal V1完成：

1. 两种时序（帧先到/节点先到）均通过自动化测试。
2. FIFO、容量、超龄、Epoch和所有权语义均有接口级测试。
3. Runtime关闭不再与活动Capture/Publish竞态。
4. 根运行与Nested的资源所有权完成结构性收口。
5. 至少一个真实Provider完成长连接外触发回调现场验收。
6. 文档明确列出另一个Provider未验收的状态。
7. 没有把FrameInbox暴露为Workflow公共帧仓。
8. 没有引入TriggerId、Broadcast或厂商SDK公共类型。
