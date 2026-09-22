# 文件、类型与功能目录规范

本规范同时用于`DP.Vision`和`DP.LabelInspection`。完整类型入口见[源码索引](SOURCE_INDEX.md)和[标签源码索引](../DP.LabelInspection/SOURCE_INDEX.md)。

## 一份文件对应一个类型实现

- 独立的类、接口、结构体、枚举分别存放，文件名与类型名一致。
- 原有128个C#文件已整理为297个文件，另新增3个枚举契约测试文件，共300个；不包含生成文件和历史发布副本。
- 私有或内部嵌套辅助类放到功能目录下的`Internal/所属类型/辅助类型.cs`；测试辅助类放到`TestDoubles/测试类型/`。
- 嵌套辅助文件中保留必要的`partial`外层声明，只有一个实际类型实现。这样仍可访问原来的私有成员，不会把内部实现改成公共类型，也不引入额外服务接口。
- 已有按职责划分的同类型分部文件使用`类型名.职责.cs`，例如`Program.RoiTools.cs`。不把各分部合回大文件。
- 命名空间保持原样，文件夹用于功能导航；不要仅因移动文件改变命名空间。

## 枚举命名

全部19个自定义枚举采用`E`加原名称，例如：

| 之前 | 现在 |
|---|---|
| `PixelLayout` | `EPixelLayout` |
| `LayerKind` | `ELayerKind` |
| `AlgorithmStatus` | `EAlgorithmStatus` |
| `GlyphBinarization` | `EGlyphBinarization` |
| `QualityFindingKind` | `EQualityFindingKind` |
| `RoiTool`、`RoiPurpose` | `ERoiTool`、`ERoiPurpose` |
| `RoiPointerAction`、`RoiConstraint`、`RoiHandleKind` | `ERoiPointerAction`、`ERoiConstraint`、`ERoiHandleKind` |
| `RegionKind`、`BarcodeKind` | `ERegionKind`、`EBarcodeKind` |
| `ImagePixelFormat`、`BindingSource` | `EImagePixelFormat`、`EBindingSource` |
| `InspectionMode`、`AlignmentMode` | `EInspectionMode`、`EAlignmentMode` |
| `InspectionVerdict`、`InspectionCapabilities`、`RoiStageState` | `EInspectionVerdict`、`EInspectionCapabilities`、`ERoiStageState` |

这是公开类型名的破坏性变更，宿主需更新引用并重新编译；不保留旧枚举别名或类型转发。枚举成员名称、数值、底层整数类型及能力枚举的`Flags`属性保持不变。现有JSON/XML配方往返、报告导出和原生UI回归已验证。

两个项目的`.editorconfig`已加入枚举E前缀规则。

## Vision功能目录

| 工程 | 功能目录 |
|---|---|
| `DP.Vision` | `Imaging`：图像、租约、池和分块源；`Geometry`：点、矩形、轮廓、Region及栅格化；`Display`：显示规划、视口、图层、缓存及显示像素 |
| `DP.Vision.Algorithms` | `Common`、`Codes`、`Surfaces`；`Text/Recognition`、`Detection`、`Segmentation`、`Matching`、`GlyphComparison`、`Quality` |
| `DP.Vision.UI` | `Canvas`、`Roi/Definitions`、`Roi/Editing`、`Roi/Persistence` |
| `DP.Vision.Acquisition.Management` | 采集配置/发现/监控快照与呈现模型（`AcquisitionManagementPresenter` 等），平台中立、不引用 UI 套件 |
| `DP.Vision.Acquisition.WinForms` | `AcquisitionManagementControl`；只依赖 `Acquisition.Management` |
| `DP.Vision.OpenCv` | `Imaging`、`Surfaces`、`Codes/Linear`、`Codes/Qr`及文字相关目录 |
| `DP.Vision.Onnx` | `Recognition` |
| `DP.Vision.OnnxDetection` | `Detection` |
| `DP.Vision.Zxing` | `Codes` |
| `DP.Vision.Winform`、`DP.Vision.WPF` | `Canvas`及其内部渲染辅助类型 |

不设置一个不断膨胀的全局`Enums`或`Models`目录；枚举和数据模型与所服务的功能放在一起。

## 测试与工具

- 测试按`Architecture`、`RoiEditing`、`Workflow`、`Configuration`、`Codes`、`Text`、`Libraries`、`Geometry`、`Integration`归类。
- 工具和示例按`Runner`、`Interaction`、`Rendering`、`RoiEditing`等职责归类。
- 历史GDI基线代码置于画布基准工程的`Baseline`，与实际迁移实现分离；只移动文件，不改变基线算法。
- 项目文件和依赖方向未变，SDK自动包含功能子目录中的源码；资源目录未改名，资源键和字符串字面量未修改。

## 验证

- 296项原有类型/程序集声明和成员指纹比较通过：只对枚举类型前缀及必要的`partial`标记做等价归一后比较。
- 300个C#文件均满足单一类型实现规则；36个嵌套辅助文件保留作用域；19个枚举均使用E前缀。
- 新增覆盖全部19个枚举成员/数值的回归及能力Flags检查。
- 双框架：Vision共享测试各75项、算法各27项，标签各213项；原生WinForms/WPF及实际OCR/外观/候选发现回归通过，构建零警告、零错误。
- 完整日志：`artifacts/structure-integrity.log`、`artifacts/structure-verification.log`及标签侧`artifacts/structure-verification.log`。文件移动映射位于`artifacts/structure-moves.json`。

此整理没有将标签矩形检查升级为任意Region检测，没有改变算法阈值，也没有新增WPF物理输入或工业准确率声明。旧`dist`和历史发布副本不会被覆盖；使用最新源码构建。
