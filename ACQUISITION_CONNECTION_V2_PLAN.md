# 图像采集连接架构 V2：长连接、整图交付与双节点模型

状态：**已实施完毕，本文转为设计归档**（V2-0..V2-9、V2-11 均已完成）  
适用范围：面阵相机、一次返回整图的线扫相机、相机自带采集及采集卡通道。  
目标：保留当初的连接生命周期、Plugin发现、机器配置、Workflow节点和性能优化的设计决策，供追溯"为什么长成这样"。

> **本文是计划，不是现状。** 各阶段的实际落地、提交号、测试证据与偏离记录见
> [ACQUISITION_CONNECTION_V2_STATUS.md](ACQUISITION_CONNECTION_V2_STATUS.md)。
> 需要"当前系统长什么样"请看 [ACQUISITION_STATUS.md](ACQUISITION_STATUS.md)。

> 本文采用已经确认的RunScoped语义：根运行结束时释放本Epoch未领取的完整图像。
> 例如本Epoch收到两张、只领取一张，另一张在`EndEpoch`时释放并计入诊断；不会交给下一根运行。

## 1. 固定决策

```text
设备连接属于软件生命周期；
取图属于节点或完整帧回调行为；
Workflow只接收完整图像。
```

1. 软件启动、Plugin和机器配置加载完成后打开物理设备。
2. 一台物理相机或一个采集卡通道只对应一个`VisionResourceSession`。
3. 一个Device Adapter只拥有一个内部SDK设备对象。
4. Workflow节点不得打开、关闭、重连或释放物理设备。
5. 面阵和线扫都只返回一张已经完成的图像。
6. 不在公共层暴露Line、Block、GrabResult、HObject、SDK句柄或裸指针。
7. 设备只在软件退出、配置停用或受控重配置时关闭。
8. 取图流是否在连接后立即启动由机器配置决定。
9. FrameInbox只保存完整`VisionProviderFrame`。
10. Epoch只属于FrameInbox的运行隔离，不是相机连接状态。
11. 根运行结束时清理本Epoch未领取帧；清理必须有计数和诊断，不能静默发生。
12. Provider失败不自动切换到另一个Provider。

## 2. 总体结构

```text
受信任Plugin目录
        │ 自动发现Driver Module
        ▼
VisionAcquisitionTypeCatalog
        │
        ├─ AreaScan类型
        └─ LineScan类型
        │
        ▼
机器相机配置（由管理界面维护）
        │
        ▼
不可变VisionAcquisitionComposition
        │
        ▼
VisionAcquisitionRuntime
        │
        ├─ ResourceSession：Camera.Top
        │   └─ Basler.Pylon.Camera
        │
        ├─ ResourceSession：Camera.Side
        │   └─ HFramegrabber
        │
        └─ ResourceSession：Camera.Line
            └─ FrameGrabber Board/Channel
        │
        ▼
CaptureAreaFrameNode / CaptureLineScanFrameNode
        │
        ▼
完整VisionCapturedImage → WorkflowVisionFrameScope
```

禁止依赖：

```text
厂商Acquisition Adapter → DP.WorkFlow
Workflow Kernel → DP.Vision
公共采集契约 → 厂商SDK
机器配置 → CLR程序集完整类型名
节点 → 物理设备Open/Close
```

## 3. 工程划分

```text
DP.Vision.Acquisition.Abstractions
    中立采集Interface、类型Descriptor、请求、结果、状态和错误。

DP.Vision.Acquisition.Runtime
    TypeCatalog、Composition、唯一ResourceSession、连接/取流/Inbox生命周期。

DP.Vision.Acquisition.UI                 （新增）
    设备发现、物理绑定、配置编辑、试拍、验证、发布和回滚。

DP.Vision.Basler
DP.Vision.Halcon
DP.Vision.Hikvision                      （未来）
DP.Vision.Dahua                          （未来）
DP.Vision.Euresys                        （未来）
    厂商Driver Module、设备配置、发现、Device Adapter和完整帧转换。

DP.WorkFlow.Nodes.Vision
    面阵/线扫两个强类型NodeModel、Handler和Workflow桥接。
```

不创建第二套相机Runtime；现有`DP.Vision.Acquisition.*`继续演进。

## 4. 身份模型

| 身份 | 示例 | 所有者 | 用途 |
|---|---|---|---|
| PluginId | `dp.vision.basler` | Plugin包 | 部署、版本和审计 |
| AcquisitionTypeId | `dp.acquisition.basler.area` | Driver Module | 配置类型、能力和设备工厂 |
| SourceId | `Camera.Top` | 机器配置/Workflow | 工艺使用的逻辑相机 |
| ResourceKey | `camera:serial:40123456` | Provider验证后发布 | 唯一物理设备及互斥域 |
| CaptureId | GUID | Runtime | 一张完整采集结果的身份 |
| ReceivedSequence | 递增整数 | Runtime | 完整帧回调顺序和诊断 |
| DeviceSequence | SDK帧号 | Provider | 可选设备序号和跳号诊断 |
| Epoch | 递增整数 | FrameInbox | 根运行帧隔离 |

正常机器配置采用：

```text
一个SourceId
→ 一个ResourceKey
→ 一个ResourceSession
→ 一个Device Adapter
→ 一个SDK设备对象
```

V2不实现Source别名。

## 5. Plugin自动发现与类型贡献

### 5.1 目标行为

```text
扫描受信任Plugin目录
→ 找到实现IVisionAcquisitionDriverModule的入口类型
→ 创建Module
→ Module贡献一个或多个AcquisitionType
→ 完整验证
→ 一次冻结TypeCatalog
```

机器相机配置不负责加载DLL，也不包含程序集路径。

如果部署包保留Manifest，它只能是构建生成的包索引，用于跳过SDK依赖DLL和记录部署版本；操作人员不编辑它，Camera配置也不通过它传入Plugin。

### 5.2 Module Interface

目标形状：

```csharp
public interface IVisionAcquisitionDriverModule
{
    string ExtensionId { get; }

    void Contribute(
        IVisionAcquisitionTypeContributionBuilder builder);
}
```

一个Module可以贡献多个Type：

```text
DP.Vision.Basler.dll
├─ dp.acquisition.basler.area
└─ dp.acquisition.basler.line

DP.Vision.Euresys.dll
└─ dp.acquisition.euresys.line
```

### 5.3 Type Descriptor

每个Descriptor至少声明：

```text
AcquisitionTypeId
PluginId / Version
AcquisitionKind：AreaScan或LineScan
DeviceSettingsVersion
DeviceSettings解析和迁移
DeviceSettings验证
设备发现能力
Device Adapter工厂
支持的触发/参数能力
是否支持完整帧回调
规范ResourceKey生成规则
```

重复`AcquisitionTypeId`、空工厂、未知配置版本或不一致能力必须在Catalog冻结前失败。

## 6. 机器相机配置

JSON只是系统内部持久化格式；设备管理员通过`DP.Vision.Acquisition.UI`维护。

### 6.1 面阵示例

```json
{
  "sourceId": "Camera.Top",
  "acquisitionType": "dp.acquisition.basler.area",
  "settingsVersion": 1,
  "required": true,
  "connection": {
    "openOnApplicationStart": true,
    "transferStart": "PerRequest"
  },
  "deviceSettings": {
    "serialNumber": "40123456",
    "pixelFormat": "Mono8",
    "triggerSource": "Line1"
  }
}
```

### 6.2 采集卡线扫示例

```json
{
  "sourceId": "Camera.LabelLine",
  "acquisitionType": "dp.acquisition.euresys.line",
  "settingsVersion": 1,
  "required": true,
  "connection": {
    "openOnApplicationStart": true,
    "transferStart": "OnConnect"
  },
  "inbox": {
    "capacity": 8,
    "byteBudget": 536870912,
    "maximumFrameAgeMilliseconds": 2000
  },
  "deviceSettings": {
    "boardSerialNumber": "FG10001",
    "channel": 0,
    "cameraModel": "LineCamera-X"
  }
}
```

公共层只解释`sourceId`、`acquisitionType`、连接/取流策略和Inbox限制；`deviceSettings`由对应Plugin解释。

不要求人员填写`ProviderBindingId`或`ResourceKey`。Descriptor验证配置后生成内部Binding和规范ResourceKey。

### 6.3 管理界面

必须支持：

```text
已安装AcquisitionType列表
设备发现
逻辑Source与物理设备绑定
强类型PropertyGrid
连接测试
单张试拍
配置验证
候选发布
历史修订与回滚
配置导入/导出
```

普通操作员只读；换班不修改机器配置。

## 7. Workflow节点模型

V2使用两个强类型NodeModel，只为参数绑定、显示和能力校验分开；执行主干和输出保持统一。

### 7.1 面阵节点

```text
NodeType：Vision.CaptureAreaFrame
Model：CaptureAreaFrameNodeModel
```

典型属性：

```text
SourceId
Timeout
ExposureMicroseconds
GainDecibels
TriggerMode
```

Source下拉只显示`AcquisitionKind.AreaScan`。

### 7.2 线扫节点

```text
NodeType：Vision.CaptureLineScanFrame
Model：CaptureLineScanFrameNodeModel
```

典型属性：

```text
SourceId
Timeout
ExposureMicroseconds
GainDecibels
实际整图采集需要的线扫工艺参数
```

具体参数以真实设备需求为准；V2不预设Line/Chunk接口。Source下拉只显示`AcquisitionKind.LineScan`。

### 7.3 PropertyGrid

两个模型使用强类型属性和现有`WorkflowProperty`元数据，由共享Studio反射生成WinForms/WPF属性界面。

不使用：

```text
object CaptureOptions
程序集限定$type
厂商SDK配置对象
超级CameraNode基类
```

少量共同字段允许重复；公共执行逻辑放在无状态内部辅助方法中。

### 7.4 文档迁移

现有`Vision.CaptureFrame`按以下规则迁移：

1. 已绑定Source明确为AreaScan时迁移到`Vision.CaptureAreaFrame`。
2. 已绑定Source明确为LineScan时迁移到`Vision.CaptureLineScanFrame`。
3. 离线或Source未知时不猜测，保留旧节点并给出迁移诊断。
4. 原始未知字段继续保真。
5. 不根据Provider名称推断Area/Line，必须读取已发布Type Descriptor。

## 8. 统一整图执行Interface

### 8.1 Workflow入口

Workflow继续只依赖一个深Interface：

```csharp
public interface IVisionAcquisition
{
    ValueTask<VisionCapturedImage> CaptureAsync(
        VisionSourceReference source,
        VisionAcquisitionRequest request,
        VisionAcquisitionOwner owner,
        CancellationToken cancellationToken);
}
```

请求使用强类型闭合集合：

```text
VisionAreaCaptureRequest
VisionLineScanCaptureRequest
```

两者最终都返回`VisionCapturedImage`。

### 8.2 Provider设备

目标契约要求`OpenAsync`返回时真实设备已经连接：

```csharp
public interface IVisionAcquisitionProvider
{
    ValueTask<IVisionAcquisitionDevice> OpenAsync(
        ValidatedVisionDeviceBinding binding,
        CancellationToken cancellationToken);
}
```

`IVisionAcquisitionDevice`：

```text
拥有唯一SDK设备对象
报告真实设备Identity
在既有连接上重复CaptureAsync
DisposeAsync时真正关闭设备
```

`CaptureAsync`不得创建第二个Camera/HFramegrabber，也不得在成功返回后关闭物理设备。

### 8.3 完整帧回调

需要回调的设备可选实现：

```text
IVisionStreamingAcquisitionDevice
IVisionAcquisitionStream
IVisionProviderFrameSink
```

`Publish`只能交付完整`VisionProviderFrame`。线扫设备或采集卡内部如何形成整图由Adapter负责，不进入公共模型。

## 9. Runtime启动与唯一实例

新增明确的站点生命周期：

```text
Construct
→ StartAsync
→ Ready
→ StopAsync/DisposeAsync
```

### 9.1 StartAsync

```text
1. 关闭新运行入口。
2. 遍历不可变Composition中的启用Source。
3. 按ResourceKey建立唯一ResourceSession候选。
4. 创建Provider实例。
5. 调用Provider.OpenAsync真正打开设备。
6. 校验设备报告的CanonicalResourceKey。
7. 发布Device到ResourceSession。
8. TransferStart=OnConnect时立即注册回调并StartGrab。
9. 所有Required Source就绪后Runtime进入Ready。
```

同一ResourceKey第二次创建Device必须在打开SDK前失败。

### 9.2 Required与Optional

- Required设备打开失败：Runtime不得进入Ready。
- Optional设备打开失败：Runtime可进入Degraded，但对应Source不可用并保留完整诊断。
- 任一失败都不得自动选择其他Provider或其他枚举设备。

### 9.3 节点执行

```text
节点CaptureAsync
→ 查找已有ResourceSession
→ 检查Connected/Transfer状态
→ 使用唯一Device对象采集或Claim
→ 返回完整图像
```

设备未连接时立即失败；节点不得隐式Open或Reconnect。

## 10. 状态模型

### 10.1 ConnectionState

```text
Created
Connecting
Connected
Faulted
Disconnecting
Disposed
```

### 10.2 TransferState

```text
Stopped
Starting
Running
Stopping
Faulted
```

两组状态分开维护。例如：

```text
Connected + Stopped
Connected + Running
Faulted + Stopped
```

不公开`NoOwner/OwnedByRun/Releasing`这类相机状态。

### 10.3 FrameInbox Epoch

Epoch是FrameInbox内部状态：

```text
ActiveEpoch?
```

它不决定设备是否连接或流是否运行。

## 11. 取图流策略

V2先实现两种：

### 11.1 PerRequest

```text
设备在软件启动时已Connected
→ 节点CaptureAsync
→ 必要时StartGrab
→ 软件触发/取一张完整图
→ 必要时StopGrab
→ 保持设备Connected
```

适合节点主动获取。

### 11.2 OnConnect

```text
设备Connect成功
→ 注册完整帧回调
→ StartGrab
→ 软件生命周期内保持Running
```

适合外部触发、节点前回调和降低首帧延迟。

外部触发Source必须使用`OnConnect`，否则只有连接没有布防，仍可能漏掉触发。

同一ResourceSession不能同时运行两套互不知情的Grab对象。

## 12. FrameInbox与RunScoped语义

FrameInbox属于ResourceSession，但每张可领取帧必须属于一个根运行Epoch。

### 12.1 BeginEpoch

```text
根运行准备通过
→ 取得Buffered Source独占消费权
→ FrameInbox.BeginEpoch(epoch)
→ 清退更早Epoch遗留
→ Engine执行首节点
```

取图流在`OnConnect`模式下已经运行；BeginEpoch不Open、Close、Start或Stop设备。

### 12.2 回调

```text
完整帧回调
→ Runtime取得所有权
→ 没有ActiveEpoch：释放并计数RejectedWithoutEpoch
→ 有ActiveEpoch：标记Epoch并入有界FIFO
```

### 12.3 Claim

```text
节点CaptureAsync
→ 只能领取当前Epoch FIFO头帧
→ 原子移除
→ 转交VisionCapturedImage
```

同一Source同一时刻只允许一个等待中的Claim；第二个请求确定性冲突。

### 12.4 EndEpoch

```text
根运行结束
→ 阻止本Epoch新Claim
→ 取消本运行仍在等待的Claim
→ Drain本Epoch未领取完整帧
→ Dispose每个图像租约
→ 记录UnclaimedAtEpochEnd
→ 释放消费权
```

选定语义：

```text
本Epoch收到两张，只领取一张
→ 另一张在EndEpoch释放
→ 不进入下一根运行
```

这不是静默丢弃：运行Trace和Source诊断必须记录数量、字节数和序号范围。严格模式下`UnclaimedAtEpochEnd > 0`可使运行产生告警或失败，由运行监管策略决定。

### 12.5 容量与过期

FrameInbox必须有界：

```text
Capacity
ByteBudget
MaximumFrameAge
```

V2默认溢出策略仍为`FaultSource`，不实现静默DropOldest。

## 13. 节点参数应用

### 13.1 PerRequest Source

节点参数在现有连接上应用：

```text
验证Request
→ 必要时写Exposure/Gain/Trigger
→ 采集完整图像
```

写参数不得通过关闭并重新打开设备实现。

### 13.2 OnConnect Buffered Source

已经回调入队的图像不能再由节点参数改变。因此：

1. 运行准备阶段收集本根运行对该Source的采集参数。
2. 同一Source在同一根运行中要求不兼容参数时，准备失败。
3. Runtime在`BeginEpoch`之前应用确定参数。
4. 如果参数变更要求停流，执行“停止交付→StopGrab→写参数→StartGrab→BeginEpoch”。
5. 参数应用前产生的帧不得进入新Epoch。
6. 节点执行时只Claim，不再次改设备参数。

暂不实现一个Epoch内多套相机参数动态切换。

## 14. 所有权

| 对象 | 所有者 | 释放时机 |
|---|---|---|
| Provider实例 | Runtime | Runtime停止 |
| ResourceSession | Runtime | Runtime停止或配置停用 |
| SDK设备对象 | Device Adapter | Device Dispose |
| Stream/Grab句柄 | Device Adapter | StopTransfer/Device Dispose |
| SDK回调缓冲 | 厂商SDK | 回调结束；跨边界前必须复制/安全保留 |
| 未领取完整帧 | FrameInbox | Claim、过期、溢出故障、EndEpoch或Runtime停止 |
| 已领取ImageFrame | Workflow FrameScope | 运行结果退役 |
| Preview租约 | UI/FrameScope | 替换或查看期结束 |

Provider拥有硬件；Workflow运行只拥有已经领取的图像。

## 15. 故障与恢复

必须区分：

```text
Plugin不可用
配置无效
设备发现失败
连接失败
真实身份不一致
参数不支持
StartGrab失败
Capture超时
流意外结束
SDK回调转换失败
Inbox溢出
帧超龄
并行Claim冲突
Runtime正在停止
```

默认规则：

- Source故障后拒绝新采集。
- 不自动切换Provider。
- 不由节点自行重连。
- 同一Provider/设备的重连只能由Runtime的显式恢复动作执行。
- 重连前先停止流、等待回调退出、关闭原SDK对象，再创建一个新对象。
- 恢复成功必须生成新的连接修订和诊断事件。

## 16. 受控重配置

机器配置变化不能直接修改正式Composition：

```text
读取候选配置
→ Plugin解析
→ 完整验证
→ 计算差异
→ 阻止新运行
→ 复用未变化ResourceSession
→ 停止并关闭变化设备
→ 打开候选设备
→ 全部Required设备成功
→ 一次发布新Composition
```

同一物理设备不能在旧Session未关闭时被候选Session再次打开。

候选打开失败时尝试恢复旧配置；恢复也失败则Runtime进入NotReady，不能伪装成继续可用。

活动Device/Stream Lease期间不承诺Plugin热卸载。

## 17. 关闭顺序

```text
1. Runtime关闭接受门。
2. 拒绝新的根运行和Capture。
3. EndEpoch并清理未领取帧。
4. 注销SDK回调入口。
5. StopGrab/StopStream。
6. 等待已进入的回调退出。
7. 等待在途Capture退出或达到受控停止上限。
8. Drain FrameInbox。
9. 关闭唯一SDK设备对象。
10. Dispose Device Adapter。
11. Dispose Provider。
```

顺序是契约，不是建议。不能在活动回调期间释放SDK对象。

## 18. 诊断与审计

每个Source至少报告：

```text
SourceId
AcquisitionTypeId
PluginId / Version
ResourceKey
ConnectionState
TransferState
ConnectionRevision
ActiveEpoch
FramesReceived
FramesClaimed
FramesExpired
FramesRejectedWithoutEpoch
FramesRejectedOverflow
UnclaimedAtEpochEnd
InboxCount / InboxBytes / HighWatermark
DeviceSequenceGaps
LastFailureKind / LastFailureMessage
```

每次运行制品至少记录：

```text
WorkflowCompositionId
AcquisitionCompositionId
机器配置Revision
Source到AcquisitionType映射
Plugin版本清单
相机配置摘要（敏感字段脱敏）
Epoch
本轮Claim的CaptureId/ReceivedSequence/DeviceSequence
本轮EndEpoch未领取帧计数
```

## 19. 当前实现与目标差距

截至本方案建立时：

- 已提交Runtime已有Provider组合、ResourceSession、有界FrameInbox、Epoch和根RunScope桥接。
- 已提交Basler具备真实完整帧回调长连接Adapter，但OnDemand仍是每次创建/Open/Close相机。
- HALCON流式Adapter存在并发未提交修改，不能视为稳定基线。
- HALCON OnDemand仍是每次创建/释放`HFramegrabber`。
- BufferedExternal当前主要在根运行Begin/End时布防/停流；V2目标是将连接和可选OnConnect流提升到应用生命周期，Epoch只控制接收/领取。
- 当前Plugin加载仍由Manifest和私有配置驱动；尚未形成自动AcquisitionTypeCatalog。
- 当前Source绑定仍在样例宿主中手工构造，SourceCatalog仍有重复投影。
- 当前只有通用`Vision.CaptureFrame`节点，尚未拆成面阵/线扫两个强类型模型。
- 相机管理UI、设备发现闭环和机器配置修订发布尚未完成。

实施期间必须保护当前并发未提交修改，不得覆盖或误判为已提交能力。

## 20. 分阶段实施

### V2-0：冻结证据与合并在研改动

1. 完成或隔离当前HALCON流式分支。
2. 记录DP.Vision/DP.WorkFlow HEAD、工作区差异和测试基线。
3. 不在未确认的并发修改上重写同名文件。
4. 为现有OnDemand和BufferedExternal建立行为基线测试。

完成条件：工作区来源清楚、无误覆盖风险。

### V2-1：AcquisitionTypeCatalog与自动Module发现

1. 增加`EVisionAcquisitionKind.AreaScan/LineScan`。
2. 增加Driver Module、Type Descriptor和候选Builder。
3. 自动发现Module并构建候选Catalog。
4. 校验重复TypeId、版本、工厂和能力。
5. 完整验证后一次Freeze。
6. 为HALCON/Basler各贡献AreaScan Type；LineScan先允许测试Type。

完成条件：不读取机器相机配置也能列出已安装AcquisitionType。

### V2-2：机器相机定义与不可变Composition

1. 定义版本化CameraDefinition文档。
2. Plugin解析自己的`deviceSettings`。
3. 生成Validated Binding和规范ResourceKey。
4. Composition直接投影Workflow SourceCatalog。
5. 私有配置摘要进入CompositionId。
6. 删除样例中的手工`sourceBindings`和重复SourceCatalog构造。

完成条件：一份机器配置生成唯一Composition和SourceCatalog。

### V2-3：应用级连接生命周期

1. 增加Runtime`StartAsync/Ready/StopAsync`。
2. Provider`OpenAsync`改为真正打开设备。
3. 一个ResourceKey只创建一个Device Adapter和一个SDK对象。
4. OnDemand改为复用已连接对象。
5. 增加ConnectionState和连接诊断。
6. 宿主在进入可运行状态前启动Runtime。

完成条件：连续执行多个Capture，真实OpenCount始终为1，直到Runtime停止才Close一次。

### V2-4：TransferPolicy与Epoch解耦

1. 增加`PerRequest/OnConnect`策略。
2. OnConnect流在Runtime Start阶段启动。
3. BeginEpoch只开放本Epoch接收/领取，不启动设备。
4. EndEpoch清理本Epoch未领取帧，但不停流、不关设备。
5. 无ActiveEpoch完整帧释放并计数。
6. Runtime Dispose按固定顺序停流和关设备。

完成条件：第二根运行不重新Open、不重新创建SDK对象；Epoch隔离仍成立。

### V2-5：面阵/线扫双节点模型

1. 增加`CaptureAreaFrameNodeModel/Handler`。
2. 增加`CaptureLineScanFrameNodeModel/Handler`。
3. Source候选按AcquisitionKind过滤。
4. 共享无状态执行与输出提交逻辑。
5. 增加旧`Vision.CaptureFrame`迁移器。
6. WinForms/WPF PropertyGrid回归。

完成条件：两个节点都输出相同`ImageFrame`，无厂商类型进入Workflow。

### V2-6：Basler迁移

1. OnDemand和Callback共用同一个已连接`Camera`。
2. 删除每次Capture的`new Camera/Open/Close`。
3. PerRequest和OnConnect不能同时创建两个StreamGrabber状态。
4. 保留回调边界中立像素复制。
5. 验证关闭顺序和断线状态。

### V2-7：HALCON迁移

1. `HalconAcquisitionDevice`持有唯一`HFramegrabber`。
2. OnDemand重复使用同一句柄。
3. OnConnect流复用同一句柄和采集线程。
4. 修正KeepCurrent、Software和Gain单位语义。
5. 不覆盖当前并发在研实现，先做差异合并。

### V2-8：线扫与采集卡首个Adapter

1. 选择一个真实线扫SDK或采集卡SDK。
2. Plugin贡献LineScan AcquisitionType。
3. Adapter只向上返回SDK完成的整张图。
4. 不引入Line/Chunk公共模型。
5. 完成Node PropertyGrid和请求映射。

### V2-9：Acquisition UI、审计和运行优化

1. 设备发现、绑定、试拍、发布、回滚。
2. 连接/取流/Inbox监控面板。
3. Composition和配置修订进入运行制品。
4. 长时间运行、断线、重连和关闭测试。
5. 为后续中立像素到HObject/Mat表示缓存记录转换耗时和字节数。

## 21. 自动化验收

至少覆盖：

1. DLL Module自动发现且顺序确定。
2. 重复AcquisitionTypeId拒绝发布。
3. 未安装Type时机器配置保真且Source不可用。
4. 同一ResourceKey只创建一个Device。
5. Runtime Start真实Open一次。
6. 多次OnDemand Capture不重复Open/Close。
7. 节点无法直接关闭Device。
8. Required设备失败时Runtime不Ready。
9. Optional设备失败时Source不可用但Runtime可Degraded。
10. OnConnect在首根运行前已经StartGrab。
11. 无Epoch回调被释放并计数。
12. BeginEpoch后回调可早于采集节点入队。
13. FIFO顺序领取。
14. 同一Source并行Claim确定性冲突。
15. EndEpoch释放所有未领取帧并记录数量。
16. 下一Epoch不能领取上一Epoch帧。
17. EndEpoch不停流、不关闭Device。
18. 第二根运行复用同一个SDK对象。
19. Runtime Dispose等待在途回调和Capture退出。
20. Shutdown事件顺序满足停流先于关设备。
21. 面阵节点只显示Area Source。
22. 线扫节点只显示Line Source。
23. 两种节点都输出完整ImageFrame。
24. 线扫Provider不会把Line/Block对象越过公共Interface。
25. Source切换和旧节点迁移不丢配置。
26. Provider私有配置变化会改变CompositionId。
27. 未领取帧、超龄帧、拒绝帧全部只Dispose一次。
28. 已返回ImageFrame在设备关闭后仍然可读。

## 22. 现场验收

自动化不能替代：

```text
真实设备长连接稳定性
SDK许可证和原生运行时
外部触发脉宽、极性和接线
连接后立即开流是否会改变设备行为
首帧时间与持续吞吐
设备序号是否可靠及何时复位
断线、重连、停流和关闭时序
采集卡通道是否可以独立并行
线扫SDK返回整图的尺寸、方向和完整性
长期内存、Inbox高水位和图像租约回收
跨进程设备独占和安全互锁
```

每个Provider分别签署；不能用Fake、合成图或另一家设备代替。

## 23. 非目标

V2暂不实现：

```text
Line/Chunk公共流
平台LineAssembler
StartLineScan/CompleteLineScan跨节点Operation
多消费者Broadcast
自动Provider故障切换
跨进程帧队列
活动Device Lease期间热卸载Plugin
一个Epoch内多套相机参数动态切换
HObject/Mat直接进入Workflow
原生表示缓存（只预留观测数据，另案设计）
```

## 24. 完成定义

只有同时满足以下条件，才能声明V2完成：

1. 所有Required设备在软件Ready前只打开一次。
2. 多次节点采集和多根运行都复用同一SDK对象。
3. 节点、FrameInbox和根运行都不能关闭物理设备。
4. PerRequest与OnConnect均有接口级自动化测试。
5. Epoch只控制完整帧接收/领取，EndEpoch清退未领取帧但不停流。
6. 面阵和线扫两个强类型节点完成双平台PropertyGrid验证。
7. 至少一个面阵Provider和一个线扫/采集卡Provider通过真实设备整图验收。
8. Runtime关闭顺序通过并发测试和现场验证。
9. Composition、Plugin版本和机器配置修订进入运行制品。
10. 没有厂商SDK类型越过采集Adapter seam。
