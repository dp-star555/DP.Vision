# DP.Vision.Basler

独立的 Basler pylon 采集 Provider。只依赖 DP.Vision / Acquisition.Abstractions 与官方 NuGet 包，不引用 Workflow、标签业务或旧 MachineVision。

这是图像采集 Provider 架构的**第二个真实厂商实现**：它的存在本身就是为了证明"多 Provider"不是只有一家能跑通的空壳。

## 构建与部署

目标 `net48;net8.0-windows`，沿用底座 x64 配置。

```xml
<PackageReference Include="Basler.Pylon.NET.x64" Version="10.3.2.636" />
```

pylon 的 .NET API 与相机软件套装（pylon Camera Software Suite）都是**免费**的；NuGet 包提供托管程序集，运行时需要安装 pylon 相机软件套装，或把运行时随程序一起部署。

两个构建陷阱已在 csproj 中处理，改动时请勿回退：

- 该包自带的 `build/Basler.Pylon.NET.x64.targets` **只在 `$(Platform)=='x64'` 时**才把 `lib/native/Basler.Pylon.dll` 加为引用。`$(Platform)` 由解决方案配置映射决定，直接依赖它会让本项目在 AnyCPU 配置下静默丢掉整个 `Basler.Pylon` 命名空间。因此本项目用 `GeneratePathProperty` 自己加引用（`ExcludeAssets="build;buildTransitive"`），结果与解决方案平台无关。
- 底座 `Directory.Build.props` 已设 `PlatformTarget=x64`，本包只提供 x64 程序集。

也可以在没有 pylon 的机器上构建：

```bash
dotnet build src/DP.Vision.Basler/DP.Vision.Basler.csproj -c Debug -p:BaslerSdkEnabled=false
```

此时 `BASLER_SDK` 未定义，采集明确抛 `VisionProviderUnavailableException`，绝不生成模拟帧。

## 相机

工作流侧通过中立采集入口使用本Provider，不直接接触任何 pylon 类型：

```csharp
var captured = await acquisition.CaptureAsync(
    new VisionSourceReference("Camera.Top"),
    new VisionCaptureRequest(TimeSpan.FromSeconds(5), exposureMicroseconds: 1500),
    new VisionAcquisitionOwner(runId, tokenId, operationId),
    token);
```

- 设备选择要求**唯一匹配**：绑定必须且只能给出 `serialNumber` 或 `userDefinedName` 之一。匹配到 0 台或 >1 台都明确失败，不回退到"第一台"。
- 主动采集（`OnDemand`）每次请求独立打开/关闭相机，不是长连接采集管理器。
- 曝光/增益**只有调用方给出数值时才写设备**（空表示保持设备当前设置），写之前先关闭对应的自动算法，避免"设置了但不生效"。
- 触发模式：`KeepCurrent` 不写任何触发参数；`FreeRun` 显式关闭触发；`Software` 显式切到软触发并发一次触发命令；`External` 要求私有配置声明 `triggerSource`，否则明确拒绝——不猜物理接线。
- pylon 的打开/抓图是阻塞调用，隔离到线程池；取消在调用边界检查；`RetrieveResult` 的超时由请求的 `Timeout` 驱动。

## 长连接与外部回调（BufferedExternal）

本 Adapter 同时实现 `IVisionStreamingAcquisitionDevice`，供"相机长期布防、外部触发回调进有界队列"的源使用。
工作流侧仍然只调 `IVisionAcquisition.CaptureAsync()`，长连接由宿主在**首节点之前**布防、运行结束时退役。

- **相机跨布防复用**：退役只停流，相机保持打开；只有设备被释放时才关闭。这样第二根根运行不会重新打开同一台相机
  （真实 SDK 上重复 Open 通常直接失败）。
- **回调边界**：`StreamGrabber.ImageGrabbed` 回调内立刻把像素复制为中立图像，之后交给接收方。
  `PylonGrabFrame.ForCallback` 的 `Dispose` 是空操作——pylon 在事件返回后自行释放抓图结果；
  主动采集路径用 `ForRetrieved`，由本侧释放。两者不能混用。
- **回调内绝不抛异常**：pylon 明确规定回调抛出的异常会向外传播，并且**事件通知在抛异常后停止**——
  一次逃逸就等于永久静默停流。所有异常都被吞掉并计入诊断。
- **停止**：先关交付口 → 摘钩子 → `StreamGrabber.Stop()` → 等已进入的回调退出。停流后仍在途的帧会被拒绝，
  不会漏给接收方。
- **布防参数来自机器配置**，不接受节点级曝光/增益覆盖（节点级覆盖会让正在出图的设备参数中途改变，
  已在运行准备阶段拒绝）。
- **断线**：`GrabSucceeded == false` 通过 `onFailure` 上报一次，接收会话随即结束并把源标记为故障，
  后续领取拿到带原因的诊断而不是等到超时。

`BaslerStreamContracts.cs` 里的 `IBaslerGrabFrame` / `IBaslerStreamCamera` 是**SDK 无关**的窄接口：
回调边界、像素落地与停止语义因此可以在没有相机、甚至没有 pylon 运行时的机器上用可控假相机完整验证
（逐帧推动，不用计时器）。真实实现 `PylonStreamCamera` / `PylonGrabFrame` 只在 `BASLER_SDK` 下编译。

从 pylon 包内 XML 文档核实、写进注释以免重复踩的事实：`ImageGrabbed` 在 `IStreamGrabber` 上；
`IGrabResult.ImageNumber` **从 1 开始且每次 `IStreamGrabber.Start` 复位**（不是跨运行单调的全局序号）；
`IGrabResult.Timestamp` 是相机私有刻度且部分相机返回 0，本版不换算，`CapturedAtUtc` 取回调边界观测时刻。

## 采集 Provider 插件

本程序集同时是一个采集Provider插件包：随程序集输出 `plugin.json`，由宿主扫描插件目录发现，宿主不需要在编译期引用任何 Basler 类型。

```json
{
  "manifestVersion": 1,
  "pluginId": "dp.vision.basler",
  "version": "1.0.0",
  "modules": { "visionAcquisition": [ "DP.Vision.Basler.dll" ] }
}
```

- `BaslerAcquisitionProviderPlugin` 实现 `IVisionAcquisitionProviderPlugin`；`ProviderId` 为 `dp.vision.basler`。
- 设备绑定属于Provider私有配置，公共配置只引用其绑定身份：

```json
{
  "bindings": {
    "top-camera": { "serialNumber": "40123456" },
    "side-camera": { "userDefinedName": "SideView" },
    "triggered-camera": { "serialNumber": "40123457", "triggerSource": "Line1" }
  }
}
```

  未知字段、缺失字段、重复绑定身份、非法类型和"两个选择器都给了/都没给"一律拒绝，不静默忽略；按 `userDefinedName` 选择时无法报告规范资源键。
- 插件实现 `IVisionAcquisitionProviderHealth`。与 HALCON 不同，这里的"缺 SDK"是**运行时**问题：pylon 托管程序集随 NuGet 包还原，编译期一定在；缺的是原生运行时。健康探测直接检查进程能否解析 `PylonBase_v10.dll`，不可用时报告带Provider身份的诊断，使采集节点在**首节点执行前**失败，而不是等到采集时抛原生 `SEHException`。

## 像素边界

`BaslerPixelFormats.Resolve` 是显式的格式映射表，**不依赖 SDK**，因此可以在没有相机、甚至没有 pylon 运行时的机器上被完整验证：

| 设备格式 | 中立布局 | 是否需要转换 |
|---|---|---|
| `Mono8` | `Gray8` | 否（直接复制语义） |
| `Mono16` | `Gray16` | 否 |
| `BGR8packed` | `Bgr24` | 否 |
| `RGB8packed` | `Rgb24` | 否 |
| `Mono*`（10/12/16 位） | `Gray16` | 是 |
| `Mono*`（其他位深） | `Gray8` | 是 |
| `Bayer*` / `RGB*` / `BGR*` / `YCbCr*` / `BiColor*`（8 位深度） | `Bgr24` | 是 |
| 其他 | 拒绝 | — |

映射之外的格式一律拒绝，不做"猜一个最接近的格式"：静默的位深或通道语义改变会让下游算法结果无法解释。特别地，**10/12/16 位彩色格式会被拒绝**而不是降位到 8 位。

像素复制统一走 pylon 的 `PixelDataConverter`（它同时处理行填充与格式转换），目标格式由映射表显式决定。转换结果尺寸与中立布局不一致时拒绝发布该帧，而不是发布一张尺寸可疑的图像。

测试覆盖映射表的全部分支、绑定与私有配置校验、Manifest 与插件入口一致性、缺运行时诊断、"同一组合内 HALCON 与 Basler 并存并按 SourceId 路由"，以及**长连接回调边界**（布防顺序、相机复用、回调内不抛异常、停流等待在途回调、断线上报、尺寸校验）。

**没有真实 Basler 相机硬件验收**：采集路径（打开设备、写参数、抓图、像素转换）与长连接时序（回调线程行为、
停流时在途回调的实际数量、断线/重连/丢帧的 `ErrorCode`、外部触发脉宽与最大触发频率、长时间吞吐）
只能在装有 pylon 与相机的现场验证，本仓库的自动化测试不能替代它。**长连接的软件结构验收已完成，
现场验收未做**——这两件事在文档里必须分开写。
