# DP.Vision 0.1.0-preview.2

独立于标签检测业务、HALCON/OpenCV类型的通用视觉底座。当前包含真实的原生WinForms/GDI+与原生WPF控件；WPF不是WindowsFormsHost。

通用视觉算法按 [ALGORITHM_MODULES.md](ALGORITHM_MODULES.md) 隔离：真实分割/配对、单字及整段质量、固定/空白、读码/1D/QR质量和ONNX识别已接入标签侧逐ROI流程；范围、验证与尚未迁移的辅助能力见 [ALGORITHM_MIGRATION.md](ALGORITHM_MIGRATION.md)。

## 工作流通用能力接入

`DP.Vision.Algorithms/Calibration` 提供独立仿射标定、旋转中心拟合和坐标变换，中心化/尺度归一并拒绝退化观测；RMS不是产品合格判定。Workflow已直接使用新版强类型标定、文件/文件夹采集、Blob、RGB均值、线圆边缘拟合、平移定位和原生双平台ROI页面，旧Workflow视觉兼容已删除。相机通过独立 [DP.Vision.Halcon](src/DP.Vision.Halcon/README.md) 直接调用SDK并复制中立图像，不经过旧设备Adapter；当前每次请求打开/关闭，不宣称长连接或高帧率。

采集已经按插件组织：`DP.Vision.Acquisition.Abstractions` 定义中立契约，`DP.Vision.Acquisition.Runtime` 负责不可变Provider组合、机器级逻辑Source绑定、设备生命周期和按物理ResourceKey互斥，`DP.Vision.Halcon` 与 `DP.Vision.Basler` 各自发布 `plugin.json` 并由宿主扫描插件目录发现，`ICameraCapture` 已退回为 DP.Vision 内部设备适配细节。工作流文档只保存逻辑SourceId，换机器只改机器配置。公共契约、插件组合、Source绑定、设备生命周期和分阶段验收见[图像采集Provider实施基线](../DP.WorkFlow/docs/vision-acquisition-providers.md)：阶段A–E已完成（两个真实厂商Provider可在同一进程组合并按SourceId路由），阶段F（RunScope与高级共享模式）受外部依赖阻塞。真实SDK像素测试不代替相机现场验收。既有Workflow接入记录见 `../DP.WorkFlow/docs/dp-vision-integration-plan.md`。

### 模板定位坐标系

`DP.Vision.Algorithms/Coordinates/LocatedCoordinateSystem` 提供模板定义身份、模板像素签名、本帧身份、正反矩阵、连续ROI正反转换及精确范围栅格化。Blob/卡尺/直线可同时表达原图点和模板局部点；Region保留原图游程，形态学/筛选保留定位来源。没有全局可变矩阵或上一帧回退。Workflow接入及限制见[定位坐标系与ROI随动](../DP.WorkFlow/docs/nodes/vision-coordinate-systems.md)。

### 文件与区域分析

- `ImageFrame`：图像内容身份与独立租约，图像修改必须换身份。
- `IImageFileReader` / `OpenCvImageFileReader`：真实文件解码，保持Gray8/BGR/BGRA/Gray16。
- `IBlobAnalyzer` / `OpenCvBlobAnalyzer`：灰度闭区间、4/8连通、面积过滤，返回原图精确Region及像素中心质心；正常空结果成功。
- `IColorAnalyzer` / `RgbColorAnalyzer`：明确RGB通道均值，Gray8复制三通道，Alpha不加权；不做产品判定。
- `InspectionMask`：精确包含/排除组合；Blob/颜色可在矩形内使用Region掩码，保留孔洞。
- `IEdgeMeasurer` / `OpenCvEdgeMeasurer`：Canny边缘上的正交线/代数圆拟合，不宣称亚像素卡尺。
- `ITemplateLocator` / `OpenCvTemplateLocator`：固定方向/尺度的平移定位，明确分数与正常空结果。
- `IImagePreprocessor` / `OpenCvImagePreprocessor`：显式灰度、反相、Gaussian/中值、固定增益/偏置及Gray16→Gray8。
- `IRegionProcessor` / `OpenCvRegionProcessor`：闭区间分割、精确掩码、零背景形态学与四连通背景填孔。
- `BlobObservation.Features` / `BlobSelector`：栅格周长、圆度、面积矩等效椭圆及确定性筛选。
- `ICaliperMeasurer` / `CaliperMeasurer`：双线性带采样、灰度剖面、梯度峰抛物线亚像素插值及极性/间距控制。
- `IRobustLineFitter` / `RobustLineFitter`：确定性RANSAC＋正交TLS，内点索引/RMS和退化拒绝。
- `ITemplatePoseLocator` / `OpenCvTemplatePoseLocator`：有效掩码上的离散旋转/尺度搜索，独立姿态正反变换；不是连续形状模型。

这些算子已接入Workflow的8个新增节点与双宿主。用法和准确边界见[算子说明](../DP.WorkFlow/docs/nodes/vision-operators.md)。相机实机工作延期，不以合成边缘测试冒充现场精度验收。
- `ICameraCapture`：宿主设备实现返回IImageSource，设备Adapter不进入图像核心。
- 分析使用8位输入，越界/Gray16明确拒绝；测量和模板定位只接受声明的矩形范围，不自动取任意ROI外接框或降位深。

## 视图浏览器

两种原生 `ResultBrowserControl` 只管理 **视图集合 → 图像与图层**。通过 `VisionView` 和 `SetViews` 提交完整集合，提供视图下拉、图层多选/全选/全不选/默认、有限预览保留及源租约回收；图层切换不重跑算法、不使底图缓存失效。节点、运行批次、执行状态和异步结果是否过期都由外层管理，Vision不承载这些流程概念。接入与演示见 [RESULT_BROWSER.md](RESULT_BROWSER.md)。

示例追加 `--results`，WPF再追加 `--wpf`；不改变原ROI演示。控件可独立接入宿主，没有自动改写现有Workflow节点编辑页面或标签工作台布局。

## 工程边界

| 工程 | 目标 | 内容 |
|---|---|---|
| `src/DP.Vision` | netstandard2.0 | 图像布局/只读租约/有界缓冲池、ROI/Region/XLD几何、图层、帧身份、预览邮箱、分块规划、LRU、显示LOD |
| `src/DP.Vision.Algorithms` | netstandard2.0 | 中立采集、Blob/颜色、测量/定位、标定及既有业务算法契约 |
| `src/DP.Vision.Acquisition.Abstractions` | netstandard2.0 | 采集公共契约：Provider/设备/逻辑源/请求/结果/错误、插件入口；只引用 `DP.Vision` |
| `src/DP.Vision.Acquisition.Runtime` | netstandard2.0 | 不可变Provider组合、机器级逻辑源绑定、设备生命周期与按ResourceKey互斥、`plugin.json` 插件加载 |
| `src/DP.Vision.OpenCv` | net48 / net8.0-windows，x64 | 文件、Blob、测量、定位及既有业务算法的真实OpenCV实现 |
| `src/DP.Vision.Halcon` | net48 / net8.0-windows，x64 | 独立SDK相机采集和Gray8/Gray16/RGB像素复制；可选SDK构建；发布 `plugin.json` 作为采集Provider插件 |
| `src/DP.Vision.Basler` | net48 / net8.0-windows，x64 | Basler pylon 相机采集（官方 NuGet 包 `Basler.Pylon.NET.x64`，免费）；显式像素格式映射；发布 `plugin.json` 作为采集Provider插件 |
| `src/DP.Vision.UI` | netstandard2.0 | ROI编辑、画布接口、共享结果浏览会话与呈现器 |
| `src/DP.Vision.Winform` | net48 / net8.0-windows，x64 | `VisionCanvasControl`及`ResultBrowserControl`，GDI+ |
| `src/DP.Vision.WPF` | net48 / net8.0-windows，x64 | `VisionCanvasControl`及`ResultBrowserControl`，WriteableBitmap/原生DrawingContext |
| `samples/DP.Vision.Demo` | 两框架 | 可运行的WinForms/WPF示例，分层、拾取、LOD开关、4MP/8K/16K演示 |

ROI定义、编辑器、编辑文档和`IVisionCanvas`已移至共享UI层 **DP.Vision.UI**，WinForms/WPF共同引用。底座保留Region、轮廓、图像与纯几何栅格化，不再依赖编辑对象；确认的ROI可通过`ToRegion`生成独立后台快照。详见 [ROI_EDITOR.md](ROI_EDITOR.md)。

控件共享`DP.Vision.UI.IVisionCanvas`，图像和几何不包含GDI/WPF/厂商类型。未来Direct2D可在同一Seam实现，当前尚未实现。

## 已实现的标准模型

- `ImageInfo` / `EPixelLayout`：Gray8、**无符号小端Gray16**、Bgr24、Rgb24、Bgra32、Rgba32；四通道采用直通Alpha，不是预乘。
- `PointD` / `RectD`：有限double原图坐标、半开矩形。
- `RectangleGeometry`：轴对齐/旋转矩形；角度为图像坐标中的顺时针弧度。
- `EllipseGeometry`：圆/旋转椭圆。
- `ContourGeometry`：点、线、开放/闭合亚像素轮廓、偶奇填充多边形；允许保留空XLD对象。`Closed`不自动意味着`Filled`。
- `RegionGeometry` / `RegionRun`：精确游程，保留孔洞、分离部分、单像素与空对象；面积、原始像素成员查询。
- `RoiDefinition`：独立的配置身份、Include/Exclude用途、Enabled及Circle/AxisAligned约束。
- `RoiDocument` / `RoiEditor` / `RoiDocumentXml`：共享的ROI创建/控制点编辑、事务、撤销重做及版本化XML持久化；不改检测证据。
- `Visual` / `CanvasLayer` / `GeometryOverlay`：Region、XLD、ROI、提示、交互层，独立顺序/可见性/颜色；覆盖层必须绑定图像身份。

### 坐标约定必须明确

DP.Vision统一采用**原图像素边界坐标**：像素(column,row)占据`[column,column+1) × [row,row+1)`，中心是`(column+.5,row+.5)`。RegionRun结束列不包含在内。

HALCON/旧CanvasGeometry的XLD整数坐标表示像素中心；适配时Column/Row→X/Y，并加`.5`，反向减`.5`。这是坐标原点转换，不是取整或丢失亚像素。HALCON包含式游程结束列需加1。

相机标定、毫米坐标、图像裁剪原点、旋转/配准变换需要适配器显式处理。没有宣称完整HALCON对象语义、全部XLD子类型或所有ROI布尔算子已经实现。

## 图像缓冲的实际用法

客户统一使用`IImageSource`：通过`VisionImage.CopyFrom`、文件读取器或`FrameWriter.Publish`取得源。`Retain()`保留独立生命周期，不复制原图；`Dispose()`只释放自己的使用权。`ImageBuffer`和`MemoryImageSource`已转为内部实现，不再由客户创建。完整规则及迁移说明见[统一图像源](UNIFIED_IMAGE_SOURCE.md)。

`FrameBufferPool`按固定分辨率/布局限定槽位数和字节预算，懒分配、复用。`FrameWriter.Publish()`把写权限永久移交给只读图像，发布后再写会抛异常。算法/画布任何一方仍持有租约，槽位都不会被重用。

```csharp
var info = new ImageInfo(2544, 1608, EPixelLayout.Gray8);
using var pool = new FrameBufferPool(info, capacity: 3,
    byteBudget: 3L * info.ByteLength);

if (!pool.TryRent(out var writer))
    throw new InvalidOperationException("处理背压：不能默默丢弃检测任务");

using (writer)
{
    writer!.Write(0, cameraPixels, 0, cameraPixels.Length);
    using var source = writer.Publish();
    canvas.PostImage(source, "capture-001", 1); // 需要using DP.Vision.UI，内部保留显示租约。

    // 仅演示后台读取，不代表执行完整检测；RunAsync在排队前保留独立源。
    byte firstPixel = await ImageProcessing.RunAsync(source, (input, token) =>
    {
        var bytes = new byte[1];
        input.CopyTo(0, bytes, 0, 1);
        return Task.FromResult(bytes[0]);
    });
    // 算法要改图时，另用自己的工作缓冲，不能修改共享原图。
}
```

厂商适配不强求零拷贝：算法不能借用该布局/生命周期时，适配器仍然复制。Pool释放后，已经发布的读者仍有效；最后一个读者释放后不再保留该槽位。

**预览与算法队列分离**：`LatestFrameMailbox`容量1，只保留最新预览；被替换的预览立即释放自己的租约，不影响算法持有的租约。`PostFrame`线程安全，其余控件方法在UI线程调用。序号在一个控件会话内严格递增。

## 图像/几何更新与缓存

- `CanvasFrame.FrameId`是**不可变图像内容身份**。改变像素必须换FrameId；同一图像只更新叠加层时，可保持FrameId但提高Sequence，从而复用图块。
- `GeometryOverlay.FrameId`必须与图像一致；不匹配直接拒绝。
- 更换图像不再无条件重建几何；复用相同不可变Geometry引用即可复用路径/掩码。新几何对象会重建相应显示缓存。
- Gray8在GDI+中用灰度索引位图，在WPF中用Gray8 WriteableBitmap，不转整幅BGR24。
- Gray16通过`CanvasOptions(gray16Low:..., gray16High:...)`映射显示；原始16位值与格式不变。
- 图块与Region掩码LRU共用指定的总预算，分别使用一半；按满块32位载荷保守记账。这个预算**不是整个进程内存上限**，不含源图像、原始几何、路径对象、WPF合成器或驱动副本。
- 同尺寸/同格式图块可直接覆盖已经存在的Bitmap/WriteableBitmap。新帧依据修订号刷新像素，不能读旧缓存。
- Region采用分块0/255成员掩码叠加原始ARGB颜色；不把区域替换成外接矩形。level 0精确保留原始像素；粗层按最近邻采样，微小缺陷可能在缩略视图中不可见，复核必须放大。

## XLD显示LOD

默认关闭。开启：

```csharp
canvas.Options = new CanvasOptions(contourLod: true,
    maximumScreenError: 0.5, tileCacheBytes: 64L * 1024 * 1024);
```

- 只简化**开放轮廓**；闭合/填充轮廓保留全部点，避免改变拓扑。
- 使用有工作量上限的迭代RDP，保留首尾点；超预算回退原始点。
- 容差根据缩放保守量化，几何偏差不超过指定屏幕误差；WPF计算时纳入设备DPI。
- 到达1:1及以上自动使用原始点；这个误差不是抗锯齿颜色误差，也不是尺寸检测公差。
- 原始`ContourGeometry.Points`不改变；面积、拾取、持久化应始终使用原始几何。
- 每条轮廓只保留当前显示级别路径，避免无限累积LOD版本。

**适用场景是静态/少变化轮廓的浏览。** 实测10万点缓存重绘从约30.14ms降到8.86ms，但每次更换10万点并重新简化的场景，LOD重建＋绘制约68.60ms，反而比关闭LOD的45.81ms慢。不能把LOD当成所有场景默认加速；后台预计算尚未实现。

## 8K/16K大图

`IImageSource.ReadTile(level,x,y,size)`提供独立拥有的图块。全图浏览请求粗层，放大读取局部level 0。原图不需要先变成巨大BGR Bitmap或完整金字塔。

- 内部`MemoryImageSource`持有原图只读租约，按需采样图块；客户通过统一工厂取得源，**原始图像本身仍占内存**。源不提供通用裁剪入口；算法自行处理ROI，局部显示由视口和图块规划负责。
- 示例的8K/16K图像源是按需生成的棋盘图，不分配整图，用于证明请求/显示缓存边界，不是相机或JPEG/TIFF解码性能。
- TIFF/大文件分块解码、磁盘/网络异步预取适配器尚未实现。当前ReadTile在渲染线程同步调用，provider应返回已在内存或预取好的块，不能把慢I/O直接放进OnPaint/OnRender。
- 检测算法需要整图还是分块，与画布显示策略分开决定。

## 运行

```powershell
dotnet build DP.Vision.sln -c Release
.\start-winforms.cmd
.\start-wpf.cmd
# 或运行net8版本：
dotnet samples/DP.Vision.Demo/bin/Release/net8.0-windows/DP.Vision.Demo.dll --wpf
```

示例提供分层开关、LOD开关、滚轮缩放、中键/右键平移、Home/适应、1:1、原始几何拾取，以及ROI工具选择、控制点、列表、启用/排除、撤销重做和XML保存/加载。详见[ROI_EDITOR.md](ROI_EDITOR.md)。已完成轮廓支持顶点插入/删除及独立撤销；复杂Region拓扑编辑、多选等高级功能仍未交付。

## 与标签检测的关系

兼容适配器位于`../DP.LabelInspection/src/DP.LabelInspection.Adapter.Vision`，方向仅为标签业务→DP.Vision。提供图像复制和旧Region/XLD转换；已移除后台对UI ROI定义的转换依赖。

旧`DP.LabelInspection.ImageViewerControl`及公共CanvasGeometry保留兼容，但其内部图像/几何/证据绘制已接入DP.Vision；标签WPF主图也已使用原生DP.Vision.WPF。标签整数矩形ROI的业务编辑逻辑仍保留，未强行换成任意形状配方。详见[工作台迁移记录](../DP.LabelInspection/VISION_WORKBENCH_MIGRATION.md)。本文性能不是包含OCR/报告的工作台实测吞吐，兼容图像复制也不等于新缓冲池生产路径。

## 验证

见[VALIDATION.md](VALIDATION.md)、[PERFORMANCE.md](PERFORMANCE.md)。

- 共享模块每框架75项测试，算法每框架27项测试，两框架通过。
- 采集Provider：`DP.Vision.Acquisition.Tests`、`DP.Vision.Halcon.Tests`、`DP.Vision.Basler.Tests`、`DP.Vision.Acquisition.Integration.Tests` 两框架合计286项通过，覆盖契约边界、组合/路由/并发、插件目录加载、插件包自包含厂商依赖、两个真实Provider并存、Basler像素格式映射与绑定选择器、缺运行时诊断。**不含真实相机出图验收。**
- 原生WinForms/WPF×net48/net8：图层/孔洞/分离岛/XLD、缩放平移、旧帧拒绝、6种像素格式、8K/16K分块缓存探针通过。
- WPF分块细缝经过失败复现与修复，均匀像素边界检查通过。
- ROI编辑：WinForms实际Windows消息检查；WPF统一指针接口＋原生渲染/Dispatcher检查。WPF物理输入在本机命中其他窗口，因此未标记为物理鼠标路由已验证。两框架Demo工具栏烟测通过。
- Release构建零警告/错误；标签SDK回归每框架213项及原有原生/OCR工作流通过。
- 同机40进程新旧/原生性能对比已完成。不是长期耐久、所有相机兼容或工业精度认证。

## 代码排版与中文参数说明

见[READABILITY.md](READABILITY.md)：中文注释及格式规则。

## 功能目录与类型命名

已完成一份文件一个类型实现、19个枚举统一E前缀，以及源码/测试/工具/示例的功能目录分类。私有嵌套辅助类型通过独立partial文件保留原作用域，命名空间和资源键不变。

- [结构规范与枚举改名表](STRUCTURE.md)
- [按功能目录排列的类型入口](SOURCE_INDEX.md)

枚举类型名变化需要外部宿主更新引用并重新编译；不提供旧类型别名。
