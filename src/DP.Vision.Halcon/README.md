# DP.Vision.Halcon

独立的 HALCON SDK 边界，只依赖 DP.Vision / Algorithms，不引用 Workflow、标签业务或旧 MachineVision。

## 构建与部署

目标 `net48;net8.0-windows`，沿用底座 x64 配置。通过 `HALCONROOT/bin/dotnet35/halcondotnet.dll` 或显式 MSBuild `HalconDotNetPath` 引用本机合法安装的 SDK；部署必须提供对应 HALCON runtime、采集接口、设备驱动及有效许可证。

没有 SDK 也可构建：`HalconStreamCameras.IsSdkEnabled == false`，造设备时明确抛出 `VisionProviderUnavailableException`，绝不生成模拟帧。此属性只描述构建能力，不证明设备在线、原生 DLL 或许可证可用。宿主不应注册不可用的实现。

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

## 长连接与外部回调（BufferedExternal）

机器配置把逻辑源声明为 `BufferedExternal` 时，本Provider用长连接会话接管设备：外部触发帧先进入 Runtime 的有界 FIFO，采集节点稍后领取最早未消费帧。

- 设备在两次布防之间**保持打开**，只有设备被释放时才关闭；上一次布防已停止时允许重新布防（宿主每根根运行都会重新布防）。
- **采集循环运行在自建线程上**：HALCON 没有 pylon 那样的 `ImageGrabbed` 事件，标准写法是 `grab_image_start` 激活持续取流、循环 `grab_image_async(-1)`（`MaxDelay` 为负表示停用"图像太旧就丢"）。线程所有权、停止等待与"停流后不得再交付"都由本Provider自己建立，不依赖 SDK 保证。
- 停止顺序：先关交付口 → 置停止位 → 尽力 `do_abort_grab` → 等采集线程退出 → 等已进入交付的帧退出。**停止等待的上界是布防时写入的 `grab_timeout`**；`do_abort_grab` 是否被目标采集接口支持取决于该接口，不支持时只是退化为等满超时。
- **抓取超时不是故障**：`H_ERR_FGTIMEOUT`(5322) 只记诊断后继续等下一轮。外部触发下"这一轮没有触发到来"是正常现象。
- **许可证故障单独一类**：`H_ERR_LIC_*` 报 `VisionProviderUnavailableException`，53xx 图像采集错误报 `VisionDeviceOfflineException`。许可证错误码**不构成连续区间**，判定按逐个列出的错误码集合 + 2300–2399 整段进行。
- **设备帧序号**：HALCON 通用采集层没有帧计数参数，因此中立帧的 `DeviceSequence` 上报空值。这是 Provider 能力差异，不是缺陷；需要设备序号时应先在现场确认所用接口是否暴露帧计数节点。
- 设备帧在回调边界内复制为中立图像，`HObject`/`HFramegrabber` 不越过 Provider Interface；停止后到达的帧同样被拒绝并释放。


## 采集 Provider 插件

本程序集同时是一个采集Provider插件包：随程序集输出 `plugin.json`，由宿主扫描插件目录发现，宿主不需要在编译期引用任何 HALCON 类型。

```json
{
  "manifestVersion": 1,
  "pluginId": "dp.vision.halcon",
  "version": "1.0.0",
  "modules": { "visionAcquisition": [ "DP.Vision.Halcon.dll" ] }
}
```

- `HalconAcquisitionProviderPlugin` 实现 `IVisionAcquisitionProviderPlugin`，只向公共层提交Provider候选工厂；`ProviderId` 为 `dp.vision.halcon`。
- 设备绑定属于Provider私有配置，公共配置只引用其绑定身份：

```json
{
  "bindings": {
    "top-camera": {
      "interfaceName": "GigEVision2",
      "deviceName": "cam-top",
      "serialNumber": "DEMO0001",
      "triggerSource": "Line1",
      "grabTimeoutMilliseconds": 5000
    }
  }
}
```

  未知字段、缺失字段、重复绑定身份和非法类型一律拒绝，不静默忽略；`serialNumber` 缺省时无法报告规范资源键。
  `triggerSource` 只有外部回调缓冲源进入外部触发模式时才需要（缺省表示"保持设备当前触发设置"，不猜物理接线）；
  `grabTimeoutMilliseconds` 是长连接的抓取等待上限，同时决定停止等待的上界，缺省 5000。
- 插件实现 `IVisionAcquisitionProviderHealth`：SDK未部署时报告不可用并给出带Provider身份的诊断，使采集节点在首节点执行前失败，而不是等到采集时才失败。

## 像素边界

`HalconImageSource.CopyFrom`（SDK 构建中提供）复制一张 HObject 的完整像素矩阵：byte 灰度、uint2 灰度、byte RGB→BGR。拒绝其他通道数和位深；不接管调用者 SDK 对象，返回独立 IImageSource。默认输出像素上限 512MiB；转换峰值还包含 SDK、临时及目标缓冲区。HALCON domain 不在此接口导入，应另外保存精确 Region。

真正的像素落地与布局/预算判定在 `HalconNeutralFrames`，主动单次采集与外部回调长连接**共用同一份实现**——两条路径各写一份通道排布，只会在现场以"偶发图像错位"的形式暴露。

测试覆盖真实 SDK 灰度/16位/RGB 像素、借用对象释放边界、取消、预算和非法格式；另有 129 例（含双 TFM）覆盖触发/曝光/增益参数决策、句柄复用、单次采集与取流的双向互斥、停止等待、回调边界纪律与错误码分类，全部由可控假设备驱动，不需要相机与许可证。**没有真实相机硬件验收**：`Software` 触发按 MVTec 官方示例实现（`[Consumer]trigger` + 抓取前 `[Consumer]trigger_software`），是否被现场接口接受、曝光/触发精度、`do_abort_grab` 支持情况与吞吐都必须现场确认。
