# 算法插件使用与扩展

2026-10-03新增[节点内模板制作与资源匹配](../DP.WorkFlow/docs/nodes/vision-template-authoring.md)：OpenCV登记配套制作入口和两个模型匹配实现；模板清单管理中立参考定义与引擎私有文件。旧图像绑定模式保留，新资源在运行前捕获内容并使用共享串行租约。不同修订的相同内容可共享，模型像素身份与坐标语义分开。WinForms/WPF通过匹配节点PropertyGrid的模板按钮弹出独立制作模型，应用时发布不可变版本；HALCON匹配尚未实施。下文早期数量和验证数字为历史记录。

2026-10-02。基础设施、原有13个算法节点、结构化诊断、资源路径、显式配置升级已接入。独立条码、水平单行OCR及几何8节点包已投放两套示例，合计29种视觉节点。纯托管几何契约IGeometryMeasurer由managed.geometry提供；平移和姿态定位统一提供IVisionCoordinateResult，点/直线/距离证据保存帧与坐标来源。见[几何测量说明](../DP.WorkFlow/docs/nodes/vision-geometry-measurement.md)和[人工复核](../DP.WorkFlow/docs/plugins/vision-plugin-review.md)。原有其余6节点为相机/目录采集或直接几何运算。

## 部署目录

```text
应用目录/
  DP.Plugins.Runtime.dll
  DP.Vision.dll
  DP.Vision.Algorithms.dll
  DP.Vision.Algorithms.Runtime.dll
  plugins/
    contracts/                        # 新能力的共享接口 DLL
      Company.Template.Contracts.dll
    company.nodes/                    # Workflow 节点模块，运行模块可不提供 Manifest
      Company.Template.Nodes.dll
    company.engine/                   # 一个包一个私有加载上下文
      Company.Engine.dll
      Company.Engine.deps.json         # 现代 .NET 原生/私有依赖解析
      私有托管依赖.dll
      runtimes/win-x64/native/...
      models/...
```

每个直接子目录是一个包；根目录散放 DLL 兼容为同一个包。新增已有能力只需引擎包；新增能力需要共享契约和实现它的引擎；增加对应的 Workflow 节点再部署节点模块。界面平台模块仍使用原有 `plugin.json` 的 studio/winForms/wpf 分组，避免把另一平台模块当作运行入口。更新需要重启。

共用契约由宿主提供；扩展契约放在 `plugins/contracts/`，启动时统一注册。允许引擎包附带相同身份、相同内容的契约副本，但不能携带不兼容版本或不同内容的同身份程序集。不要把所有私有依赖放进 contracts。

net8 按包隔离私有托管依赖，net48 对同进程依赖执行统一版本约束。独立构建的 EngineV1/EngineV2 验收插件分别携带同名第三方库的两个版本：net8 可同时调用，net48 拒绝第二个包并保留诊断。原生 SDK 不承诺多版本隔离。

## 启动装配

```csharp
var session = new PluginLoadSession();
// 先创建所有发现适配器，使内置共享契约在任何扫描前完成注册。
var workflowLoader = new WorkflowPluginLoader(session);
var driverLoader = new VisionAcquisitionDriverModuleLoader(session);
var algorithmLoader = new VisionAlgorithmModuleLoader(session);
session.RegisterSharedAssembly(typeof(IWorkflowVisionAlgorithmNode).Assembly);
session.RegisterSharedContracts(pluginDirectory);

var algorithms = algorithmLoader.Load(pluginDirectory,
    new[] { new ManagedVisionAlgorithmModule() });
var runtime = new VisionAlgorithmRuntime(algorithms);
// 每次准备捕获当前配方和机器资源目录；函数不读取活动编辑器。
VisionAlgorithmResourceContext Resources() => new(recipeDirectory, machineResourceDirectory);
var bindings = new WorkflowVisionAlgorithmBindings(runtime, existingFramePreparation, Resources);

services.Add<IWorkflowVisionAlgorithmBindings>(bindings)
    .Add<IWorkflowNodeCapabilityProvider>(bindings)
    .Add<IWorkflowRunPreparationService>(bindings);
// 原有帧/采集运行所有权仍独立注册，不被算法服务覆盖。
services.Add<IWorkflowRunResourceOwner>(frameScope);
```

节点、采集和算法加载器必须使用同一个 session。算法登记只贡献描述与工厂；不会加载模型。检查算法 `Diagnostics`、Workflow `DiscoveryFailures` 和采集 `Failures`，不要把部署失败藏成“未安装”。示例程序用 Trace 报告启动诊断。Workflow 原有 Manifest 的依赖、重复包和非法分组错误仍按原先规则抛出；直接 Runtime 扫描及算法扫描按 DLL 保留失败并继续发现其他入口。

示例程序在构建后将 OpenCV、ZXing、PP-OCR 包及其依赖投放到 plugins。三个引擎启用 CopyLocalLockFileAssemblies，把第三方托管及原生依赖带入包；宿主对引擎的项目引用只负责构建，不参与编译或根目录部署。模型按配方选择，示例不自动下载模型。

## 实现选择与运行流程

每个节点算法槽位保存以下配置：

```json
{
  "implementationId": "opencv.cnn-patch",
  "settingsVersion": 1,
  "settings": {
    "backbonePath": "C:\\VisionModels\\backbone.onnx",
    "scale": "2"
  },
  "dependencies": {}
}
```

`Settings` 使用 invariant 格式字符串，工厂负责解释版本、未知字段和合法范围；检测阈值等每次调用参数仍使用中立接口的强类型输入。资源配置属于节点/配方，不能按实现 Id 存成机器级单例。依赖也要明确选择，例如 `ppocr.recognize` 的 `preprocessor` 槽位可选 `opencv.text-preprocess`；运行时不猜默认依赖。

节点槽位可声明 RequiredFeatures，准备阶段检查实现是否具备这些附加特征。

旧配方缺少 Algorithm 字段时，明确保留原实现，映射如下；初始化配置保存到 Algorithm，检测阈值和输入绑定仍留在原节点属性中。

| 节点 | 原实现/默认选择 |
|---|---|
| LoadFile、LoadFolder | opencv.image-read |
| AnalyzeBlobs | opencv.blob |
| AnalyzeColor | managed.color |
| MeasureEdges | opencv.edges |
| LocateTemplate | opencv.template |
| PreprocessImage | opencv.preprocess |
| ThresholdRegion、MorphRegion | opencv.region |
| SelectBlobs | managed.blob-select |
| MeasureCaliper | managed.caliper |
| FitRobustLine | managed.robust-line |
| LocateTemplatePose | opencv.template-pose |

新增节点的默认值由节点作者显式规定；空选择直接报错，不按发现顺序选实现。没有机器默认实现表；模型支持绝对路径、相对于已保存配方目录的路径，以及 `resource:` 机器资源引用。许可/设备环境由引擎工厂验证，不属于检测阈值。

## 检查、准备与配置升级

`VisionAlgorithmInspection.Analyze` 汇总每个绑定及依赖的问题，不创建实例、不加载模型。它检查明确选择、契约、附加特征、配置规则、描述中的类型/数值范围、资源存在性、依赖槽位及循环。`IVisionAlgorithmConfigurationValidator` 必须是轻量配置检查；引擎仍在 `PrepareAsync` 校验许可和真正加载模型。文件存在不等于有效模型，登记成功不等于本配方资源就绪。

运行准备先完整检查，再捕获资源快照和创建。错误包含 Code、Phase、BindingKey、ImplementationId、DependencyPath、Message、Detail。调用方用 `VisionAlgorithmExceptionDiagnostics.Read(error)` 读取，不解析异常文本；旧 `InvalidOperationException` 类型保持兼容。Workflow 将位置映射为子流程/节点/槽位，列表双击定位，详情保留原异常。准备失败不会提交候选；静态错误阻止运行，历史初始化失败保留诊断并允许修复环境后重试。

WinForms/WPF 示例都提供“插件与算法”页：显示实现、引擎、版本、能力、特征、入口程序集来源；支持检查配方资源、取消、显式升级所选节点配置和导出 JSON 复核报告。手动检查只准备/归还候选计划，不运行节点、不打开采集作用域、不退役上一轮帧。取消不强制终止厂商原生调用；资源租约继续保护未完成工作。编辑配方后的迟到检查/准备结果不会覆盖当前配置状态。

配置升级由 `IVisionAlgorithmConfigurationMigrator` 显式提供。`VisionAlgorithmFactory<T>.WithConfigurationPolicy` 可组合验证和升级委托，不改变原工厂。升级先深复制所有槽位，再检查升级后的元数据与依赖；全部成功才提交可撤销的节点修改。版本未知且无升级规则时明确拒绝，不静默丢弃字段，不自动把其他引擎的设置转换过来。

示例从宿主旁的可选 `algorithm-environment.json` 读取机器资源目录：

```json
{ "settingsVersion": 1, "resourceDirectory": "VisionResources" }
```

机器目录相对于此配置文件解析；默认是宿主旁的 VisionResources。配方中的 `resource:Models/recognition.onnx` 解析到机器目录内部，拒绝 `..` 越界。普通相对模型路径依赖当前已保存配方的目录；未保存时明确报错。原配方引用不会被重写成绝对路径。宿主通过 `VisionAlgorithmResourceContext` 传给运行时与检查器，不用当前工作目录猜测资源。保留无上下文的旧运行时入口时，相对路径继续遵循旧调用方的行为；新宿主应始终提供上下文。

新增独立节点：`DP.WorkFlow.Nodes.Vision.Barcode` 登记 `Vision.ReadBarcode`，默认 zxing.code；范围含 ROI、掩码或定位时要求 masked 特征。完整排除区域返回 not_decoded，禁止回退整图；多码保留 ambiguous，不直接生成产品 NG。`DP.WorkFlow.Nodes.Vision.Ocr` 登记 `Vision.RecognizeTextLine`，默认 ppocr.recognize + opencv.text-preprocess 依赖，必须由配方提供模型。OCR 仅处理水平单行轴对齐矩形，拒绝面积掩码/定位，不承诺文字检测、旋转矫正或外观检查。

两个节点包均只引用中立契约和已有 Nodes.Vision 桥接，无引擎工程引用；示例的节点包/引擎包引用均是 build-only。外部节点通过 `IWorkflowVisionFrameFact` 提供同帧结果说明，通过公开 `WorkflowVisionFrameScope.Stage` 参与正常输出提交和预览回撤，无需宿主列举插件结果类型。

兼容旧宿主的全局算法注入：只有选择原实现、版本 1、初始化设置及依赖为空时才使用旧入口；任何其他选择必须注册算法绑定服务，否则明确失败。两个示例已经使用绑定服务，不再全局注册算法实例。制作界面用 WorkflowVisionImageFileReader 从运行时准备指定解码器，读取任务结束后归还计划租约。

文件夹仍由 WorkflowVisionAcquisitionSession 冻结清单及维护游标；算法绑定只负责解码。会话提供 IWorkflowVisionAlgorithmFolderSource 的读取回调，Handler 在回调中调用节点选择的解码器，成功解码后推进，失败可重试同一个文件。已有 NextAsync(nodeId, token) 保留旧行为；自定义文件夹来源接入现代绑定宿主时须实现回调接口。每轮根运行归零、嵌套准备不重置根游标的语义保持不变。

一次根运行经过：

1. 编译并冻结节点配置及子计划路径，校验通用宿主能力。
2. 为每个槽位确定实现、附加特征和依赖；校验版本、资源及依赖循环。
3. 工厂准备并捕获模型快照，按资源键申请/创建实例；OpenCV 在准备阶段探测原生运行库，CNN/ONNX 模型在准备阶段加载。
4. 算法候选成功后，继续已有采集/帧准备、旧资源退役和本轮作用域取得。任何失败都会归还算法候选租约。
5. 发布完整候选绑定，执行节点。Handler 通过 bindings 的 Invoke/InvokeAsync 调用，不能把异步任务当作同步返回结果来提前释放资源。
6. 结束或停止后退役本轮计划。最后计划引用、工厂创建和在途调用全部退出后才销毁资源。

缓存键包含实现/版本、工厂规范化的资源身份和依赖绑定。CNN 使用模型内容哈希及缩放；PP-OCR 使用读取快照的 SHA256 及预处理依赖。相同内容可共享实例，不同内容/初始化不会仅因路径相同而误共享。首版无空闲缓存，最后使用者退出即释放；非协作式原生创建/调用只能等待完成，不能强行中断。

工厂可声明独占、共享串行或共享并发安全。串行锁覆盖整个依赖闭包，多个包装算法不能绕过同一个推理会话的串行约束。`ReleaseFailures` 可供宿主检查资源销毁、取消回调和取消后后台创建的异常。

动态输入依然可能在执行时失败。例如模板由上游帧提供，尺寸与帧身份必须在调用时检查；CNN 参考模型也保留特征来源/缩放兼容检查。准备不能保证所有检测输入都合法。

## 新节点如何接入

节点只引用中立能力接口，声明 `IWorkflowVisionAlgorithmNode.GetAlgorithmSlots()`，Handler 通过 `IWorkflowVisionAlgorithmBindings` 调用其槽位。新增算法接口使用 `[VisionCapability]`；引擎提供公开无参 `IVisionAlgorithmModule`，显式登记 descriptor 和 factory。无需修改运行时的能力白名单。

参数描述支持字符串、布尔、数字、枚举、默认值、数值范围、说明及文件路径。两个桌面宿主注册 `WorkflowVisionAlgorithmProperties.CreateProvider(catalog)` 后，按节点槽位生成实现候选、配置版本、完整初始化/依赖编辑，并按已选实现生成专有参数条目；普通属性提交、撤销和重做保持可用。缺失或不兼容的选择保留在候选中并标注，准备阶段阻断运行。未知字段继续保存在完整配置中，工厂最终决定接受或拒绝。编辑回调每次获取节点的当前选择，避免失败回滚或撤销替换配置对象后仍修改旧对象。

仅声明槽位的节点可由上述提供者自动生成选择项；需要保留公开属性代理时，实现选择下拉框使用通用键：

```csharp
[WorkflowPropertyEditor(WorkflowPropertyEditorKeys.VisionAlgorithmPrefix + "company.template")]
public string ImplementationId { get; set; }
```

节点应把这个属性映射到其 Algorithm.ImplementationId，避免重复持久化两份选择。既有模板节点使用 JsonIgnore 代理属性，完整 Algorithm 保存一次。切换实现前需清空旧专有设置/依赖；当前不自动迁移另一引擎的未知参数。CNN/PP-OCR 工厂首版限制单份模型文件不超过 256 MiB。

完整的“新增契约 + 引擎 + 节点”示例在相邻 DP.WorkFlow 的 `tests/PluginFixtures/Intensity.*`。测试宿主以 `ReferenceOutputAssembly=false` 构建插件，通过目录和 JSON 文档使用节点，编译期不引用新契约/实现。

## 嵌套依赖的可视化配置（2026-10-02）

主属性面板和双击打开的节点详情窗口都按工厂的 GetDependencies 展开依赖分组。每个依赖显示能力名称及槽位，下拉框只列出兼容实现；缺失的原选择继续显示并保留。缺少选择时不会自动选第一个候选，必须由配方作者明确选择。选择后展示该实现的参数、版本及下一层依赖。依赖声明可以随配置变化；声明消失时原数据作为未知槽位保留，由作者修正。

“清空配置以切换实现”开启一次，会清空该层参数及全部下级依赖，恢复参数版本 1，保留该层实现身份；操作可以撤销。随后再选择新实现。高级初始化参数/依赖编辑保留完整 JSON，用于修复未知槽位、缺失插件和未被参数描述覆盖的字段。界面不会自动迁移不同引擎的设置。

打开或刷新属性只读取描述、轻量配置验证和依赖声明，不调用 PrepareAsync、不加载模型或校验许可。依赖循环、重复声明、描述异常及未知槽位显示配置提示。单槽位投影最多 512 个选择、64 层依赖；超预算时停止展开，保留高级配置入口。文件真实性、原生依赖及许可仍由“检查配方资源”和正式运行准备验证。

详情窗口使用隔离节点副本：取消丢弃未应用的修改，应用一次作为正式节点的一次可撤销修改。主面板的候选值和领域属性扩展自动传到详情窗口；通用 Workflow UI 仍不引用算法契约。撤销、重做和失败回滚后，嵌套编辑项按槽位路径重新读取当前选择图。旧实现的参数条目不能写入已切换的新实现。

## 当前边界与验证

- 13 个算法节点均通过 Invoke/InvokeAsync 使用准备好的绑定。两个桌面示例已移除具体算法实例创建，编译期不引用 OpenCV 类型；其余采集与几何节点没有额外算法槽位。
- 本轮 Workflow 验证：Nodes.Vision 73 项、Windows/UI 362 项、UI.Shared 63 项，共 498 项通过。覆盖旧配方缺字段、新选择及依赖往返、同能力两个节点不同实现、缺引擎/版本错误在读文件前失败、旧宿主拒绝忽略选择、文件夹回调/失败重试、选择编辑回滚/撤销，以及真实预处理/Region/Blob/颜色、卡尺/直线、边缘和两类模板定位。两个示例构建为 0 警告、0 错误。
- 引擎包改为携带依赖后，算法运行时回归在 net48 和 net8 各 16 项通过；本轮合计 530 项自动测试通过。
- 独立进程的无引擎编译引用宿主验证：从示例实际生成的 OpenCV/ZXing/PP-OCR 包发现实现，完成文件→预处理→Blob→颜色调用；OpenCV 保持在包加载上下文，未进入默认上下文。此验证使用仅含算法包的目录，不包含硬件厂商包或识别模型。
- 块异常两种真实实现已经登记并在两个框架上验证模型加载、共享和兼容拒绝；没有重新引入已删除的 Workflow 旧异常检测节点。
- 绑定按运行准备身份、子计划路径、NodeId、槽位区分。同名节点的绑定测试通过；既有帧预览仓仍按 NodeId 寻址，并继续拒绝根/子文档里重复的视觉预览节点 Id。这是预览层的独立限制。
- 新目录会话不支持热替换/卸载。扩展契约冲突属于全局部署错误；先修复 contracts 再启动。
- HALCON 目前只有采集入口，没有虚构 HALCON 算法实现；PP-OCR 注册单行识别，没有把检测概率图工具登记为完整文字检测能力。
- HALCON 实机许可、相机出图和真实 PP-OCR 识别模型验收仍依赖实际部署环境；本次没有对应实机/识别模型验证。
