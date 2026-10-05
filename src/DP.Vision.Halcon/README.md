# DP.Vision.Halcon

独立的 HALCON SDK 边界，只依赖 DP.Vision / Algorithms，不引用 Workflow、标签业务或旧 MachineVision。

## 构建与部署

目标 `net48;net8.0-windows`，沿用底座 x64 配置。通过 `HALCONROOT/bin/dotnet35/halcondotnet.dll` 或显式 MSBuild `HalconDotNetPath` 引用本机合法安装的 SDK；部署必须提供对应 HALCON runtime、采集接口、设备驱动及有效许可证。

没有 SDK 也可构建：`HalconStreamCameras.IsSdkEnabled == false`，造设备时明确抛出 `VisionProviderUnavailableException`，绝不生成模拟帧。此属性只描述构建能力，不证明设备在线、原生 DLL 或许可证可用。宿主不应注册不可用的实现。

## 模板算法引擎

同一个`DP.Vision.Halcon.dll`现在提供`HalconVisionAlgorithmModule`算法入口，以及原有采集入口。发现阶段只登记描述，不检查许可或加载模型；未装配SDK时仍可查看算法元数据，显式制作/准备会报不可用。

| 匹配实现 | 制作实现 | 能力 |
|---|---|---|
| `halcon.template-ncc-model` | `halcon.template-ncc-model.build` | 灰度NCC，平移/旋转、精确制作掩码，尺度固定1 |
| `halcon.template-shape-model` | `halcon.template-shape-model.build` | 原生尺度形状匹配，平移/旋转/尺度、精确制作掩码 |

工厂提供金字塔、角度/尺度范围和步长、极性、对比度等制作参数；样图支持8位灰度/RGB/BGR，16位需先显式转换。模板只使用裁剪范围与有效掩码交集。资源通过共同`VisionTemplateStore`管理不可变版本、内容校验、样图和参考几何；原生序列化模型存放`variants/halcon/model.bin`，不能直接读取OpenCV模型或旧工程的shm/ncm。准备时解码并验证原生环境，同内容资源共享串行实例，最后租约退出后清理SDK模型句柄。

部署包包含`DP.Vision.Halcon.dll`、`halcondotnet.dll`、私有托管依赖，以及net8构建的`DP.Vision.Halcon.deps.json`；运行环境和许可由HALCON安装提供。宿主共享`DP.Vision.dll`、`DP.Vision.Algorithms.dll`、`DP.Vision.Acquisition.Abstractions.dll`，不重复投放。net48同进程依赖版本一致，框架已经提供的`System.ValueTuple.dll`不放入引擎包。共用加载器跳过仅依赖框架的SDK模块候选，避免为纯算法扫描HALCON的WPF/WinForms控件。

运行直接按节点角度/尺度上下限调用原生范围搜索。共同顺时针角度[a,b]转换为HALCON起始角度−b、跨度b−a；跨越±180°时拆成两段，整周范围保持360°。制作范围必须覆盖运行区间；NCC尺度须固定为1。模型制作采样及原生插值影响实际返回角度/尺度，运行不使用OpenCV采样步长。NCC和形状分数各有含义，阈值需分别确认。完整有效模板必须在搜索ROI中，保留排除孔洞；原生位置转换为共同像素边界坐标后构建业务坐标系。每段最多1024个检出，共同工作预算约束ROI验证，不估算HALCON内部计算量；取消不能中断单次原生调用。旧候选列表接口已删除，配置不迁移，宿主与引擎需同步重建。

## 相机

工作流侧只通过中立采集入口使用本Provider：

```csharp
var captured = await acquisition.CaptureAsync(
    new VisionSourceReference("Camera.Top"),
    new VisionCaptureRequest(TimeSpan.FromSeconds(5), exposureMicroseconds: 1500),
    new VisionAcquisitionOwner(runId, tokenId, operationId),
    token);
```

- **一个绑定一个设备适配器，一个设备适配器只持有一个 `HFramegrabber`**：主动单次采集（OnDemand）与外部回调长连接（BufferedExternal）共用同一句柄，句柄在设备释放时才关闭。先前的"每次请求独立打开/关闭设备"已删除——同一物理设备只有一条取流通道，反复打开既丢失设备侧设置，也让两条路径无法互斥。
- 绑定身份来自Provider私有配置（`interfaceName`/`deviceName`/`serialNumber`），例如 GigEVision2、USB3Vision、GenICamTL 的设备；不自动选择第一台相机。该格式不进入工作流文档。
- 参数写入只有一条路径（`HalconFramegrabberParameters` 决策 + `HalconFramegrabberCamera.WriteParameters` 执行）：先写 `grab_timeout`，再写曝光/增益，最后写触发。
- Exposure 给出时先关 `ExposureAuto` 再写 `ExposureTime`（GenICam 微秒）；Gain 给出时先关 `GainAuto` 再写 `Gain`（**分贝**，SFNC 约定的 dB 单位）。参数为 `null` 表示"不动设备当前设置"，显式 `0` 是真实取值、必须写入——两者语义不同，不再用 `> 0` 判断混淆。
- 触发模式四态都真实成立：`KeepCurrent` **一个触发参数都不写**（保持设备当前设置）；`FreeRun` 写 `external_trigger=false`；`External` 写 `external_trigger=true` + `TriggerSelector=FrameStart` + `TriggerSource`（私有配置未声明触发源时抛 `VisionSourceConfigurationException`，不猜物理接线）；`Software` 按 MVTec 官方示例实现——`[Consumer]trigger=Software` + `AcquisitionMode=Continuous`，每帧前 `[Consumer]trigger_software=1` 再 `grab_image`，不写 `external_trigger`。
- **两条取流通道双向互斥**：会话在布防中时按请求单次采集被拒绝（设备已结束则报 `VisionDeviceOfflineException`，否则报 `VisionSourceConfigurationException`）；相机句柄上已有单次采集在飞时布防被拒绝（`InvalidOperationException`）。同一物理设备一次只能有一种采集模式，因此不做静默排队。
- SDK 阻塞操作在线程池执行，协作取消在 SDK 调用边界检查；`grab_timeout` 限制采集等待，但不能保证中断设备打开或不遵守超时的驱动。宿主必须等请求退出再释放关联资源。

## 面阵与线扫两个 AcquisitionType

本插件贡献两个AcquisitionType，机器配置按 `acquisitionType` 引用：

| AcquisitionTypeId | Kind | 显示名 |
|---|---|---|
| `dp.acquisition.halcon.area` | `AreaScan` | HALCON 面阵相机 |
| `dp.acquisition.halcon.line` | `LineScan` | HALCON 线扫相机 |

- 两者的区别只有 `Kind`：它决定工作流节点按几何形态过滤Source——面阵节点只显示Area Source，线扫节点只显示Line Source。两个Type的能力声明、私有配置契约和设备适配器**完全相同**。
- **整图由SDK完成组装，适配器只向上交付整张图**：线扫相机在HALCON采集接口（`GigEVision2`/`USB3Vision`/`GenICamTL`，或采集卡的对应接口）下，`grab_image` 返回的就是采集接口/采集卡组装好的一张完整图像，因此本Provider不引入Line/Chunk公共模型，也不做行拼接、不做分块交付。一次请求对应一张完整图像，尺寸即设备侧整图尺寸。
- 两个Type共用同一个 `HalconDeviceSettingsParser`，线扫没有额外deviceSettings字段。**不要**为线扫另立一份会漂移的解析器：私有配置契约相同，另立一份只会让两边的校验逐渐分叉。

## 设备发现

`HalconAcquisitionProvider` 同时实现 `IVisionDeviceDiscovery`，供管理界面列出候选设备：

- HALCON **没有"列出已安装采集接口"的查询**，接口名由本Provider给出默认集合（`GigEVision2`/`USB3Vision`/`GenICamTL`），
  构造时可覆盖（`new HalconAcquisitionProvider(discoveryInterfaceNames: ...)`）——现场用采集卡通道时必须显式传入对应接口名。
- 逐个接口执行 `info_framegrabber(<接口>, 'info_boards', ...)`，把返回的 `|` 分隔 `token:value` 条目解析为候选：
  **只有 `device:` 是可回填进 `open_framegrabber` 的设备 ID**，`device_sn`/`user_name`/`model`/`vendor` 只用于展示与去重；
  不含任何 token 的纯字符串（或带冒号的裸串）按原样当作设备 ID。
- 输出的绑定身份与规范资源键与 `HalconDeviceSettingsParser` 一致：有序列号 → 绑定身份用序列号、资源键 `camera:serial:<serial>`；
  否则绑定身份为 `<接口>|<设备名>`、资源键 `halcon:camera:<接口>|<设备名>`。因此发现结果可以直接组成 `deviceSettings`。
- 去重与排序：同一台相机在多个接口下可见时按资源键去重；按资源键 ordinal 排序，两次枚举同序。
- **单个接口失败不等于"没有设备"**：失败经 `HalconStreamFaults` 分类后保留诊断文本并跳过该接口；
  **只有全部接口都失败时**才抛 `VisionProviderUnavailableException`。返回空列表的含义严格限于"接口都探测成功、且确实没有设备"。

现场确认项：

- 默认接口名集合（尤其采集卡接口的实际名称）与 `info_boards` 的条目形态。
- 同一台相机跨接口去重的实际效果，以及逐接口枚举的耗时（`info_framegrabber` 是阻塞调用，没有超时参数）。

## 长连接与外部回调（BufferedExternal）

机器配置把逻辑源声明为 `BufferedExternal` 时，本Provider用长连接会话接管设备：外部触发帧先进入 Runtime 的有界 FIFO，采集节点稍后领取最早未消费帧。

- 设备在两次布防之间**保持打开**，只有设备被释放时才关闭；上一次布防已停止时允许重新布防（宿主每根根运行都会重新布防）。
- **采集循环运行在自建线程上**：HALCON 没有 pylon 那样的 `ImageGrabbed` 事件，标准写法是 `grab_image_start` 激活持续取流、循环 `grab_image_async(-1)`（`MaxDelay` 为负表示停用"图像太旧就丢"）。线程所有权、停止等待与"停流后不得再交付"都由本Provider自己建立，不依赖 SDK 保证。
- 停止顺序：先关交付口 → 置停止位 → 尽力 `do_abort_grab` → 等采集线程退出 → 等已进入交付的帧退出。**停止等待的上界是布防时写入的 `grab_timeout`**；`do_abort_grab` 是否被目标采集接口支持取决于该接口，不支持时只是退化为等满超时。
- **抓取超时不是故障**：`H_ERR_FGTIMEOUT`(5322) 只记诊断后继续等下一轮。外部触发下"这一轮没有触发到来"是正常现象。
- **许可证故障单独一类**：`H_ERR_LIC_*` 报 `VisionProviderUnavailableException`，53xx 图像采集错误报 `VisionDeviceOfflineException`。许可证错误码**不构成连续区间**，判定按逐个列出的错误码集合 + 2300–2399 整段进行。
- **设备帧序号**：HALCON 通用采集层没有帧计数参数，因此中立帧的 `DeviceSequence` 上报空值。这是 Provider 能力差异，不是缺陷；需要设备序号时应先在现场确认所用接口是否暴露帧计数节点。
- 设备帧在回调边界内复制为中立图像，`HObject`/`HFramegrabber` 不越过 Provider Interface；停止后到达的帧同样被拒绝并释放。


## 采集 Driver Module

本程序集同时是一个采集 **Driver Module** 包：宿主**扫描插件目录**，在程序集里找实现
`IVisionAcquisitionDriverModule` 的公开类型即可发现，**不读取任何 Manifest**，
宿主不需要在编译期引用任何 HALCON 类型。

- `HalconAcquisitionDriverModule` 实现 `IVisionAcquisitionDriverModule`，以
  `dp.acquisition.halcon.area` 与 `dp.acquisition.halcon.line` 两个 AcquisitionType
  向 Type Catalog 注册候选工厂；插件身份常量 `PluginIdentity` 为 `dp.vision.halcon`。
- 同一个类实现可选接口 `IVisionAcquisitionDriverModuleHealth`：SDK 未部署时 `TryGetHealth`
  报告不可用并给出带插件身份的诊断。该结论在 Type Catalog 冻结时**按 Module 记录一次**，
  使机器配置里引用这些 Type 的逻辑源在**首节点执行前**就被标记为不可用，而不是等到采集时才失败。
  **未实现该接口的 Module 视为可用**——它是可选能力，不是"默认不可用"。
- 设备绑定属于该 AcquisitionType 的 `deviceSettings`，由 `HalconDeviceSettingsParser` 解析一次，
  结果装进插件私有 `ProviderState` 随公共绑定一路带到 `OpenAsync`；公共层只原样转交、不解释。
  **插件私有配置不再承载设备绑定**（非空即拒绝）：同一台相机绝不能在两处各写一遍。

`deviceSettings` 是**单设备扁平对象**：

```json
{
  "interfaceName": "GigEVision2",
  "deviceName": "cam-top",
  "serialNumber": "DEMO0001",
  "triggerSource": "Line1",
  "grabTimeoutMilliseconds": 5000
}
```

  `interfaceName` 与 `deviceName` 必填；未知字段、缺失字段和非法类型一律拒绝，不静默忽略。
  `serialNumber` 缺省时无法报告规范资源键。
  `triggerSource` 只有外部回调缓冲源进入外部触发模式时才需要（缺省表示"保持设备当前触发设置"，不猜物理接线）；
  `grabTimeoutMilliseconds` 是长连接的抓取等待上限，同时决定停止等待的上界，缺省 5000。

## 像素边界

`HalconImageSource.CopyFrom`（SDK 构建中提供）复制一张 HObject 的完整像素矩阵：byte 灰度、uint2 灰度、byte RGB→BGR。拒绝其他通道数和位深；不接管调用者 SDK 对象，返回独立 IImageSource。默认输出像素上限 512MiB；转换峰值还包含 SDK、临时及目标缓冲区。HALCON domain 不在此接口导入，应另外保存精确 Region。

真正的像素落地与布局/预算判定在 `HalconNeutralFrames`，主动单次采集与外部回调长连接**共用同一份实现**——两条路径各写一份通道排布，只会在现场以"偶发图像错位"的形式暴露。

测试覆盖真实 SDK 灰度/16位/RGB 像素、借用对象释放边界、取消、预算和非法格式；`DP.Vision.Halcon.Tests` 共 **127 例 × 双 TFM**，覆盖触发/曝光/增益参数决策、句柄复用、单次采集与取流的双向互斥、停止等待、回调边界纪律、错误码分类、两个AcquisitionType的Kind与整图交付、Driver Module 身份与 SDK 健康报告、**设备发现**（`info_boards` 权威条目解析、与配置解析一致的资源键、跨接口去重、全接口失败时明确抛不可用），全部由可控假设备驱动，不需要相机与许可证。**没有真实相机硬件验收**：`Software` 触发按 MVTec 官方示例实现（`[Consumer]trigger` + 抓取前 `[Consumer]trigger_software`），是否被现场接口接受、曝光/触发精度、`do_abort_grab` 支持情况与吞吐都必须现场确认；线扫还需现场确认整图高度由哪一侧决定（相机帧触发 vs 采集接口/采集卡参数）以及行频与整图尺寸的对应关系；设备发现还需确认默认接口名集合与 `info_boards` 条目形态。
