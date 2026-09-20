# 算法下沉与逐ROI接入记录

源码预览，未覆盖旧dist。最新默认流程见 `../DP.LabelInspection/CURRENT_INSPECTION_FLOW.md`。

## 当前工程收敛（不保留旧程序集）

已从解决方案和src删除四个旧工程：`DP.LabelInspection.Barcode.Zxing / Ocr.Onnx / Vision.OpenCv / Vision.OnnxDetection`。业务装配与标签契约转换归入唯一 `DP.LabelInspection.Runtime`；无旧程序集、旧命名空间或类型转发。下面早期迁移描述中的“旧入口保留”已被本次工程级破坏性变更取代。

DB检测器的真实模型加载、预处理、推理和后处理已移至独立 `DP.Vision.OnnxDetection`，通过中立 `ITextRegionDetector` 输入ImageBuffer、输出原图PixelBounds；不引用标签业务。真实模型仍产生37个冻结候选。该工程实际需要ONNX和OpenCV，没有给纯OCR工程增加OpenCV依赖。

标签两框架各203项测试及完整真实OCR/原生UI验收通过；当前日志 `../DP.LabelInspection/artifacts/project-consolidation-verification.log`。

## 实际工程

- **DP.Vision.Algorithms / netstandard2.0**：图块坐标、物理分割、独立字符配对、单字比较、整段文字质量组合、固定/空白质量、OCR/CTC、读码、一维码/QR质量契约。结果显式区分完成、未完成、不支持、未请求。
- **DP.Vision.OpenCv / net48 + net8.0-windows**：真实字符分割与制库候选补切、单字比较、固定/空白、1D/QR印刷质量、OCR图像预处理。
- **DP.Vision.Onnx / net48 + net8.0-windows**：真实模型校验、CPU推理及CTC识别；不依赖标签业务或OpenCV。
- **DP.Vision.Zxing / netstandard2.0**：实际解码与网格证据提取。
- 标签旧入口以适配方式调用上述实现；业务引导值、字库发布和固定修订、ROI编排仍归标签层。

底座不引用标签业务或UI。没有为了工程矩阵创建空HALCON项目。

## 两级文字质量替换

`TextQualityInspector(ICharacterSegmenter, ICharacterMatcher, IGlyphComparer)` 组合实际分割、配对和比较；默认Ordinal配对大小写敏感、不替换O/0。

`ITextQualityInspector` 也可整体替换，声明是否需要识别和参考。替代策略能返回整段质量finding，不必伪造字符切片/归一化差值；`Completed=false` 即使没有finding也不能通过。当前未附送已训练的深度质量模型。

```csharp
var ink = new DP.Vision.OpenCv.OpenCvInkInspector();
using var backend = DP.LabelInspection.Runtime.OpenCvInspectionBackend
    .WithQualityAlgorithms(
        new DP.LabelInspection.Runtime.RegionQualityAlgorithms(ink, ink),
        textQuality: hostTextQuality,
        matcher: hostMatcher);
```

注入实现由宿主拥有。算法选择通过装配API，不是自动插件扫描或阈值互换。

## 接入业务流程

内置后端实现 `IRoiWorkflowBackend / IRoiInspectionSession`，由Core按ROI执行前检→必要读取→引导比较→已选印刷质量。当前ROI失败不停止其他ROI；保留执行轨迹和完整细节。内置旧 `Analyze` 入口也走同一路径，旧混合文字/读码质量方法已删除。

WinForms/WPF提供独立数据/质量项目设置，配方持久化；原始OCR证据在后续质量结果缺少识别字段时也不会丢失。

## 保留的语义与所有权

- 单字112画布/80长边、±2对齐、原容差过滤不变；迁移不等于直接差分或新阈值标定。
- 固定/空白保留原像素阈值、连通面积、忽略掩码与保护带。
- 中立网格使用像素边缘坐标；ZXing原中心坐标通过明确±0.5转换。整数旧报告拒绝非整数或越界缺陷框。
- 图像租约显式释放；兼容层复制图像后释放中立证据。无零拷贝/吞吐提升声明。
- 各算法支持的像素格式不相同；底座支持格式不等于所有算法支持，不能静默截断Gray16。

## 最新验证

- 标签net48/net8：各 **197** 通过，0失败/跳过。
- Vision core：各 **60**；算法：各 **24**，0失败/跳过。
- 两解决方案Release零警告/错误；锁文件恢复及完整verify通过。
- 原生OpenCV、真实OCR、WinForms/WPF和字库流程通过；OCR52行与原Python/CTC52/52，人工真值51/52。物理字符375、比较370、缺参考5、超阈值1保持冻结证据口径。
- 日志：`artifacts/roi-migration-verification.log`；`../DP.LabelInspection/artifacts/roi-workflow-verification.log`。

## 边界

- 第三方旧非分阶段 `IInspectionBackend` 仍有历史兼容通道；需实现新的session接口才能获得阶段门控保证。
- 标签运行时仍包含整图ECC的标签参考/忽略区装配及图像codec/证据绘制；文字候选检测器算法现已下沉到DP.Vision.OnnxDetection。
- HALCON算法后端、通用OCR接口的非CTC实现、通用插件/模型选择UI仍未提供；当前ONNX是明确的PP-OCR识别实现。
- 不证明工业准确率、ISO评级、WPF物理输入路由或实际16K相机吞吐。
