# DP.Vision.Halcon

独立的 HALCON SDK 边界，只依赖 DP.Vision / Algorithms，不引用 Workflow、标签业务或旧 MachineVision。

## 构建与部署

目标 `net48;net8.0-windows`，沿用底座 x64 配置。通过 `HALCONROOT/bin/dotnet35/halcondotnet.dll` 或显式 MSBuild `HalconDotNetPath` 引用本机合法安装的 SDK；部署必须提供对应 HALCON runtime、采集接口、设备驱动及有效许可证。

没有 SDK 也可构建：`HalconCameraCapture.IsSdkEnabled == false`，采集明确抛出 `PlatformNotSupportedException`，绝不生成模拟帧。此属性只描述构建能力，不证明设备在线、原生 DLL 或许可证可用。宿主不应注册不可用的实现。

## 相机

工作流侧通过中立采集入口使用本Provider，不直接接触 `ICameraCapture`：

```csharp
var captured = await acquisition.CaptureAsync(
    new VisionSourceReference("Camera.Top"),
    new VisionCaptureRequest(TimeSpan.FromSeconds(5), exposureMicroseconds: 1500),
    new VisionAcquisitionOwner(runId, tokenId, operationId),
    token);
```

`ICameraCapture` 仍是本程序集内部的设备适配细节（`HalconAcquisitionDevice` 用它打开设备并复制中立图像），已不再是工作流能力契约：

```csharp
ICameraCapture camera = new HalconCameraCapture(grabTimeoutMilliseconds: 5000);
using var image = await camera.CaptureAsync("GigEVision2|设备标识", new CameraCaptureOptions(), token);
```

- CameraId 是 `HALCON接口名|设备名`，例如 GigEVision2、USB3Vision、GenICamTL 的设备；不自动选择第一台相机。该格式现在只出现在Provider私有配置里，不进入工作流文档。
- 本版每次请求独立打开/关闭 HFramegrabber，不是长连接采集管理器。并发打开同一设备是否允许由驱动决定，不保证高帧率吞吐。
- Exposure 非零写入 `ExposureTime`（GenICam 微秒）；Gain 非零写入 `Gain`（设备单位）；零不写参数。
- Triggered 传入外部触发开关，触发线路/Source 需预先正确配置。接口不支持这些参数时直接失败，不猜参数别名或切换后端。
- SDK 阻塞操作在线程池执行，协作取消在 SDK 调用边界检查；`grab_timeout` 限制采集等待，但不能保证中断设备打开或不遵守超时的驱动。宿主必须等请求退出再释放关联资源。

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
{ "bindings": { "top-camera": { "interfaceName": "GigEVision2", "deviceName": "cam-top", "serialNumber": "DEMO0001" } } }
```

  未知字段、缺失字段、重复绑定身份和非法类型一律拒绝，不静默忽略；`serialNumber` 缺省时无法报告规范资源键。
- 插件实现 `IVisionAcquisitionProviderHealth`：SDK未部署时报告不可用并给出带Provider身份的诊断，使采集节点在首节点执行前失败，而不是等到采集时才失败。

## 像素边界

`HalconImageSource.CopyFrom`（SDK 构建中提供）复制一张 HObject 的完整像素矩阵：byte 灰度、uint2 灰度、byte RGB→BGR。拒绝其他通道数和位深；不接管调用者 SDK 对象，返回独立 IImageSource。默认输出像素上限 512MiB；转换峰值还包含 SDK、临时及目标缓冲区。HALCON domain 不在此接口导入，应另外保存精确 Region。

测试覆盖真实 SDK 灰度/16位/RGB 像素、借用对象释放边界、取消、预算和非法格式；没有真实相机硬件验收，也不代表曝光、触发精度或现场吞吐已验证。
