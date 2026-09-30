# 算法插件化改造方案

目标：**只往运行目录放入引擎 DLL，系统就自动识别它提供的算法**；同一个算法接口允许多个引擎实现并存，由配置明确选择；功能分组（坐标、预处理、像素处理……）是逻辑分类，不决定 DLL 划分。

本方案不改变现有 DLL 划分，不改变任何算法接口的签名，第一阶段对宿主完全兼容。

## 1. 现状与问题

| 现状 | 证据 | 后果 |
|---|---|---|
| 算法层没有发现和登记机制 | `DP.Vision.Algorithms` 只有接口 | 宿主必须知道具体类名并手写装配 |
| WorkFlow 按接口类型登记实现 | 示例程序 `Form1.cs`：`.Add<IBlobAnalyzer>(new OpenCvBlobAnalyzer())` | 加引擎要改宿主代码 |
| 同一接口登记两次会静默覆盖 | `WorkflowServiceProvider.Add`：`_services[typeof(TService)] = service;` | A、B 两个引擎无法并存，也不会报错 |
| LabelInspection 直接 `new` 具体类 | `OpenCvInspectionBackend`、`LabelInspectionHost`、`LabelInspectionControl`、`AnomalyTrainingSession` 引用了 9 个具体实现类 | 换引擎要改多个业务文件 |
| 采集层已有可复用的机制 | `VisionAcquisitionDriverModuleLoader`、`VisionAcquisitionPluginAssemblyLoader`（`Assembly.LoadFrom` + 按程序集身份查重） | 算法层可以直接复用，不必重新设计 |

## 2. 目标结构

```
DP.Vision.dll               核心：图像、几何；插件程序集加载器（从采集层下沉，两边共用）
DP.Vision.Algorithms.dll    全部算法契约（按功能分目录）+ 标记接口、特性、算法目录
engines/                    放入即识别
    DP.Vision.OpenCv.dll
    DP.Vision.Zxing.dll
    DP.Vision.PPOcr.Onnx.dll
    DP.Vision.Halcon.dll    （以后）
```

DLL 数量 = 1 个契约 DLL + N 个引擎 DLL，不随功能数量增长。`DP.Vision.Algorithms` 里的纯托管实现（卡尺、直线拟合、颜色等）作为"托管引擎"参与识别，不另建 DLL。

依赖方向不变：引擎 → Algorithms → 核心。Algorithms 不新增任何第三方包依赖。

## 3. 契约层新增内容（`DP.Vision.Algorithms`）

### 3.1 标记接口与特性

```csharp
namespace DP.Vision.Algorithms;

/// 所有可被目录识别的算法接口都继承它；本身没有成员。
public interface IVisionAlgorithm { }

/// 标在算法接口上：能力身份与功能分组。
[AttributeUsage(AttributeTargets.Interface, Inherited = false)]
public sealed class VisionCapabilityAttribute : Attribute
{
    public VisionCapabilityAttribute(string id, string category, string displayName);
    public string Id { get; }            // 稳定身份，写入配置和流程文档，如 "code.read"
    public string Category { get; }      // 功能分组，如 "读码"
    public string DisplayName { get; }
}

/// 标在实现类上：实现身份与引擎。
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class VisionImplementationAttribute : Attribute
{
    public VisionImplementationAttribute(string id, string engine);
    public string Id { get; }            // 全局唯一，如 "zxing.code"
    public string Engine { get; }        // 如 "ZXing"
    public string? DisplayName { get; set; }
    public bool Shared { get; set; } = true;   // 见 3.4 生命周期
}

/// 实现的私有配置，原样传给实现，由实现自己解析和校验（与采集层私有设备配置的做法一致）。
public sealed class VisionAlgorithmSettings
{
    public VisionAlgorithmSettings(string? text);
    public string? Text { get; }
}
```

给已有接口加一个**无成员**的基接口，对已编译的调用方和实现方都是二进制兼容的。

### 3.2 算法目录

```csharp
public sealed class VisionAlgorithmCatalogOptions
{
    public IList<string> PluginDirectories { get; }               // 扫描目录，如 "engines"
    public IList<Assembly> Assemblies { get; }                    // 宿主已直接引用的引擎程序集
    public IDictionary<string, string> Defaults { get; }          // 能力Id → 默认实现Id
    public IDictionary<string, string?> Settings { get; }         // 实现Id → 私有配置文本
}

public sealed class VisionAlgorithmCatalog : IDisposable
{
    public static VisionAlgorithmCatalog Build(VisionAlgorithmCatalogOptions options);

    public IReadOnlyList<VisionCapabilityInfo> Capabilities { get; }        // 含 Category，供工具箱分组
    public IReadOnlyList<VisionImplementationInfo> ImplementationsOf(string capabilityId);
    public VisionAlgorithmLoadReport Report { get; }                        // 扫描了什么、识别了什么、跳过了什么及原因

    public T Resolve<T>(string? implementationId = null) where T : class, IVisionAlgorithm;
    public bool TryResolve<T>(string? implementationId, out T? algorithm, out string? reason)
        where T : class, IVisionAlgorithm;

    public void Dispose();   // 释放目录创建的共享实例
}
```

目录在 `Build` 之后冻结，运行期不增删。更换引擎需要重启程序。

### 3.3 识别规则

1. **扫描范围**：`PluginDirectories` 下的 `*.dll`，以及 `Assemblies` 中显式给出的程序集。
   - 原生库（HALCON、pylon、OpenCV 原生 DLL）抛 `BadImageFormatException`，静默跳过，与采集层一致；
   - 托管 DLL 加载失败时，记入 `Report`，不中断其他 DLL 的识别。
2. **能力**：接口继承 `IVisionAlgorithm`，并且带 `[VisionCapability]`。
3. **实现**：公开、非抽象的类，带 `[VisionImplementation]`，并且实现了至少一个能力接口。
   - 一个类实现多个能力接口时，在每个能力下各登记一次，使用同一个实现 Id。
   - 实现了 `IVisionAlgorithm`，但缺少特性的类：记入 `Report`（"未标注身份，已跳过"），不报错。
4. **冲突**：
   - 实现 Id 重复：`Build` 失败，报告两个来源的程序集；
   - 能力 Id 重复（两个接口使用同一个 Id）：`Build` 失败。
5. **程序集只加载一份**：复用采集层的 `Assembly.LoadFrom` 加按程序集身份查重的做法，下沉到核心作为共用的加载器。契约 DLL 在进程里只有一份，不会出现"同名不同类型"。

### 3.4 创建实例

**选择构造函数：** 使用参数都能满足的公开构造函数，只接受以下三类参数：

| 参数类型 | 来源 |
|---|---|
| 其他能力接口（继承 `IVisionAlgorithm`） | 从目录按默认规则解析（组合实现） |
| `VisionAlgorithmSettings` | `Settings[实现Id]`，没有配置时 `Text` 为 null |
| 带默认值的参数 | 使用默认值 |

有多个构造函数满足条件时，选参数最多的那个。一个都不满足时，该实现记为"不可创建"，原因写入 `Report`。组合实现之间出现循环依赖时，`Build` 失败。

**生命周期：**
- `Shared = true`（默认）：每个目录中每个实现类只创建一个实例，同一个类实现的多个接口共用这个实例；目录 `Dispose` 时释放。**共享实现必须线程安全。**
- `Shared = false`：每次 `Resolve` 创建新实例，由调用方负责释放。适用于持有非线程安全原生对象的实现（例如 OpenCV DNN 的 `Net`）。

实例在首次 `Resolve` 时才创建。例如 ONNX 模型只在真正用到时才加载，模型缺失不会影响其他算法的识别。

### 3.5 解析规则

`Resolve<T>(implementationId)` 的判定顺序：

1. 指定了 `implementationId`：必须存在，并且实现了 `T`，否则报错；
2. `Defaults[能力Id]` 有值：使用该实现；
3. 这个能力只有一个实现：使用它；
4. 没有实现：报"缺少能力 X"；有多个实现又没有默认值：报"能力 X 有多个实现（列出 Id），请在配置中指定"。

**不按加载顺序偷偷选一个。**

## 4. 现有接口与实现的对应表

能力 Id 采用"分组.功能"的格式；实现 Id 采用"引擎.功能"的格式。

| 分组 | 接口 | 能力 Id | 实现（实现 Id） | 备注 |
|---|---|---|---|---|
| 图像输入 | `IImageFileReader` | `image.file-read` | `OpenCvImageFileReader`（`opencv.file-read`） | |
| 预处理 | `IImagePreprocessor` | `preprocess.image` | `OpenCvImagePreprocessor`（`opencv.preprocess`） | |
| 区域处理 | `IRegionProcessor` | `region.process` | `OpenCvRegionProcessor`（`opencv.region`） | |
| 区域处理 | `IBlobAnalyzer` | `region.blob` | `OpenCvBlobAnalyzer`（`opencv.blob`） | |
| 区域处理 | `IBlobSelector` | `region.blob-select` | `BlobSelector`（`managed.blob-select`） | 托管引擎 |
| 颜色 | `IColorAnalyzer` | `color.analyze` | `RgbColorAnalyzer`（`managed.rgb`） | 托管引擎 |
| 测量 | `IEdgeMeasurer` | `measure.edge` | `OpenCvEdgeMeasurer`（`opencv.edge`） | |
| 测量 | `ICaliperMeasurer` | `measure.caliper` | `CaliperMeasurer`（`managed.caliper`） | 托管引擎 |
| 测量 | `IRobustLineFitter` | `measure.line-fit` | `RobustLineFitter`（`managed.line-fit`） | 托管引擎 |
| 定位 | `ITemplateLocator` | `locate.template` | `OpenCvTemplateLocator`（`opencv.template`） | |
| 定位 | `ITemplatePoseLocator` | `locate.template-pose` | `OpenCvTemplatePoseLocator`（`opencv.template-pose`） | |
| 定位 | `ITranslationRegistrar` | `locate.translation` | `OpenCvTranslationRegistrar`（`opencv.translation`） | |
| 读码 | `IBarcodeReader` | `code.read` | `ZxingBarcodeDecoder`（`zxing.code`） | 通过子接口实现 |
| 读码 | `IMaskedBarcodeReader` | `code.read-masked` | `ZxingBarcodeDecoder`（`zxing.code`） | |
| 读码 | `ILinearBarcodeQualityInspector` | `code.quality-linear` | `OpenCvBarcodePrintInspector`（`opencv.code-quality-linear`） | |
| 读码 | `IQrQualityInspector` | `code.quality-qr` | `OpenCvQrPrintInspector`（`opencv.code-quality-qr`） | |
| 文字 | `ITextLinePreprocessor` | `text.line-prepare` | `OpenCvTextLinePreprocessor`（`opencv.text-line`） | |
| 文字 | `ITextLineRecognizer` | `text.recognize` | `OnnxTextLineRecognizer`（`ppocr.recognize`） | 组合实现 + 私有配置 + 需释放 |
| 文字 | `ITextRegionDetector` | `text.detect` | **无实现** | 见 7.1 |
| 文字 | `ICharacterSegmenter` | `text.segment` | `OpenCvCharacterSegmenter`（`opencv.segment`） | 一个类实现两个接口 |
| 文字 | `IGlyphCandidateSegmenter` | `text.segment-candidates` | `OpenCvCharacterSegmenter`（`opencv.segment`） | |
| 文字 | `ICharacterMatcher` | `text.match` | `OrdinalCharacterMatcher`（`managed.ordinal-match`） | 托管引擎 |
| 文字 | `IGlyphComparer` | `text.glyph-compare` | `OpenCvGlyphComparer`（`opencv.glyph-compare`） | |
| 文字 | `ITextQualityInspector` | `text.quality` | `TextQualityInspector`（`managed.text-quality`） | 组合实现 |
| 表面 | `IBlankQualityInspector` | `surface.blank` | `OpenCvInkInspector`（`opencv.ink`） | 一个类实现两个接口 |
| 表面 | `IFixedQualityInspector` | `surface.fixed` | `OpenCvInkInspector`（`opencv.ink`） | |
| 异常检测 | `IPatchAnomalyDetector` | `anomaly.patch` | `OpenCvPatchAnomalyDetector`（`opencv.patch`）；`OpenCvCnnPatchAnomalyDetector`（`opencv.cnn-patch`） | **已经是一个能力两个实现**；CNN 版需要私有配置并需要释放 |
| 异常检测 | `IGroupedPatchAnomalyTrainer` | `anomaly.patch-grouped-train` | `OpenCvPatchAnomalyDetector`（`opencv.patch`） | |
| 异常检测 | `ICharacterAnomalyDetector` | `anomaly.character` | `OpenCvCharacterAnomalyDetector`（`opencv.character-anomaly`） | 组合实现 |

`IBarcodeQualityInspector` 是两个质量接口的公共基接口，不单独作为能力。`Calibration`、`Coordinates` 下的类型是纯数学工具，没有"换引擎"的意义，不进入目录。

## 5. 需要调整的实现

| 实现 | 调整 | 原因 |
|---|---|---|
| `OnnxTextLineRecognizer` | 增加构造函数 `(VisionAlgorithmSettings settings, ITextLinePreprocessor preprocessor)`，从配置解析模型路径和 SHA-256 | 现有构造函数要求传入字符串路径，目录无法提供；保留原构造函数 |
| `OpenCvCnnPatchAnomalyDetector` | 增加构造函数 `(VisionAlgorithmSettings settings)`，解析骨干网络路径和缩放比例；标记 `Shared = false` | 同上；DNN `Net` 不是线程安全的 |
| `OpenCvCharacterAnomalyDetector` | 无参构造函数和 `(IPatchAnomalyDetector)` 构造函数都保留；按 3.4 的规则，目录会选择后者，并注入默认的块异常检测实现 | 组合实现 |
| 全部共享实现 | 逐个核对线程安全（是否持有可变状态或非线程安全的原生对象） | `Shared = true` 的前提 |

所有现有构造函数都保留，直接 `new` 的写法继续可用。

## 6. 配置格式

目录本身只接收 `Defaults` 和 `Settings` 两个字典，不绑定任何配置文件格式，所以 Algorithms 不需要引入 JSON 依赖。宿主按自己的配置体系读取后传入。建议的 JSON 形式：

```json
{
  "visionAlgorithms": {
    "pluginDirectories": [ "engines" ],
    "defaults": {
      "code.read": "zxing.code",
      "anomaly.patch": "opencv.patch"
    },
    "settings": {
      "ppocr.recognize": "{ \"modelPath\": \"models/rec.onnx\", \"sha256\": \"...\" }",
      "opencv.cnn-patch": "{ \"backbonePath\": \"models/resnet.onnx\", \"scale\": 2 }"
    }
  }
}
```

## 7. 相关的清理项

### 7.1 `ITextRegionDetector`（孤儿接口）

PP-OCR 重构后，检测改用了 `PPOcrDetectionTask` 及自有的输入输出，这个接口已经没有实现。两种处理方式，需要决定：
- (a) 由 `DP.Vision.PPOcr.Onnx` 提供一个实现，把检测重新纳入契约；
- (b) 删除这个接口。

### 7.2 插件加载器下沉

把 `VisionAcquisitionPluginAssemblyLoader` 的逻辑移到核心 `DP.Vision`，作为采集层和算法层共用的公开加载器；采集层改为调用它。行为不变。

## 8. 宿主改造

### 8.1 WorkFlow

**阶段 A：替换手写登记，行为不变**
```csharp
var catalog = VisionAlgorithmCatalog.Build(options);   // 选项来自宿主配置
services.AddVisionAlgorithms(catalog);
```
- `AddVisionAlgorithms` 由 WorkFlow 提供：对目录里的每个能力，按 3.5 的规则解析默认实现，登记到 `WorkflowServiceProvider`。需要为 `WorkflowServiceProvider` 增加非泛型的 `Add(Type, object)` 重载。
- 节点的能力预检不变，仍然是 `Require<IBlobAnalyzer>()`。
- 同时把 `WorkflowServiceProvider.Add` 的重复登记改为报错，不再静默覆盖。

**阶段 B：节点可以选择实现**
- 算法节点模型增加可选字段 `ImplementationId`。空值表示使用默认实现；旧的流程文档没有这个字段，按默认实现处理，不需要迁移。
- 处理器改为 `catalog.Resolve<IBlobAnalyzer>(node.ImplementationId)`。
- 预检时校验指定的实现是否存在。
- 节点属性面板列出 `ImplementationsOf(能力Id)`；工具箱按 `Category` 分组。

### 8.2 LabelInspection

把 `OpenCvInspectionBackend`、`LabelInspectionHost`、`LabelInspectionControl`、`AnomalyTrainingSession` 中直接 `new` 的具体实现类，改为从目录解析。业务行为不变。

## 9. 实施阶段

| 阶段 | 内容 | 对宿主的影响 | 验收 |
|---|---|---|---|
| **1** | 契约层：标记接口、两个特性、`VisionAlgorithmSettings`、算法目录、加载器下沉；30 个接口和 25 个实现类加标注；第 5 节的实现调整 | 无，只新增 | 见第 10 节 |
| **2** | WorkFlow 阶段 A | 示例程序的装配代码变短，运行行为不变 | 现有 WorkFlow 测试全部通过；删掉某个引擎 DLL 后，预检报"缺少能力" |
| **3** | WorkFlow 阶段 B | 节点多一个可选字段 | 两个实现并存；不同节点各用一个；旧文档照常打开 |
| **4** | LabelInspection 改为从目录解析 | 无业务行为变化 | LabelInspection 回归测试全部通过 |
| **5** | 文档：更新 `ALGORITHM_MODULES.md`、`STRUCTURE.md`；处理 7.1 | 无 | — |

## 10. 阶段 1 的测试清单

- 目录能识别 OpenCv、Zxing、PPOcr.Onnx 和托管引擎中的全部实现，与第 4 节的表一致。
- 一个类实现两个接口时，登记两次，共享同一个实例。
- 构造一个测试引擎 DLL 放进临时目录，不修改任何代码即可被识别；移走后不再出现。
- 两个引擎实现同一个能力：
  - 没有默认值时，`Resolve` 报"有多个实现"；
  - 配置了默认值后，按默认值解析；
  - 显式指定实现 Id 时，按指定解析。
- 实现 Id 重复时，`Build` 失败，并报告两个来源。
- 原生 DLL 被静默跳过；缺少依赖的托管 DLL 进入 `Report`，不影响其他 DLL。
- 组合实现能注入依赖；出现循环依赖时报错。
- `Shared = false` 的实现每次 `Resolve` 得到新实例；共享实例在目录 `Dispose` 时被释放。
- 私有配置能原样传给实现；缺少必需配置时，报错信息指出具体的实现 Id。

## 11. 需要确认的决策

1. **扫描范围**：只扫描专门的 `engines/` 目录，还是也包含宿主直接引用的引擎程序集？（建议两者都支持：直接引用的通过 `Assemblies` 显式传入；放入即用的放在 `engines/`。）
2. **默认生命周期**：共享单例（性能好，但要求线程安全）还是每次新建？（建议默认共享，个别实现显式声明 `Shared = false`。）
3. **能力 Id 与实现 Id 的命名**：是否采用第 4 节的"分组.功能"和"引擎.功能"格式？这两个 Id 会写入配置和流程文档，定下之后不能再改。
4. **`ITextRegionDetector`**：按 7.1 选择 (a) 还是 (b)。
