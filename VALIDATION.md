# 验证记录

## 最新：视图浏览器移除流程概念

按职责边界收敛为`VisionView`、`IViewDisplaySink.SetViews`及视图集合浏览。彻底删除节点描述、节点执行枚举、RunId/BeginRun、节点选择和节点容量；不保留别名或转发。异步结果的有效性由宿主判定，内部只处理显示序号和旧界面事件保护。

共享测试调整为 **115项/框架**，覆盖完整集合原子替换、选择及偏好、预算拒绝、保留失败回滚、并发提交、清空/关闭，以及公开程序集不再包含旧节点契约的架构检查。算法 **57**、HALCON边界 **5**、标签 **219** 项也在两个框架通过，共 **792项**。Vision构建0警告/0错误；标签旧MSTEST0032提示仍存在。

WinForms/WPF两框架原生探针验证无节点选择器、视图切换、图层实际像素和底图缓存、集合清空与Dispose源回池；原有ROI演示及新视图演示均通过。示例仅含原图、二值图、原图与叠加三个视图，截图已检查。输出：`artifacts/verify-20260910-073352/`。

标签完整OCR、物理外观、DB发现及原生工作台回归通过：52行Python/CTC一致52、人工精确51，375字符/370比较/5缺失/1超限，37个作者候选，原始证据政策未改。

日志：两项目`artifacts/view-only-verification.log`及Vision侧`view-only-tests.log`、`view-only-format-check.log`。没有修改外层Workflow业务代码或历史发布包；物理输入、相机吞吐仍未验证。当前接入见[RESULT_BROWSER.md](RESULT_BROWSER.md)，以下多节点设计只作历史记录。

## 历史：多节点、多视图、图层选择浏览器

完成中立节点预览描述、共享运行会话和WinForms/WPF原生`ResultBrowserControl`，接入说明见[RESULT_BROWSER.md](RESULT_BROWSER.md)。新增26项共享测试，验证运行/版本隔离、并发提交、节点/视图选择、跨运行图层偏好、像素及元数据预算、保留失败回滚、过期UI事件、空图状态及关闭后预览拒绝。

两框架分别通过：共享 **118**、算法 **57**、现有HALCON边界 **5**、标签 **219**，共 **798项**。Vision构建0警告/0错误；标签构建通过，旧`EnumContractTests.cs`仍各框架产生一次MSTEST0032提示，没有修改该历史测试。

原生探针实际打开图层弹出框，触发节点/视图下拉、复选及全选/全不选/默认按钮；检查渲染像素、切回旧节点、同底图更新叠加及图层开关不重复ReadTile、失败清图、运行替换和带活跃底图Dispose后的源回池。两框架、两种UI的ROI原演示和新增浏览器演示均通过；界面及弹出列表截图已目视检查，最新输出目录`artifacts/verify-20260910-001110/`。

完整标签OCR/物理外观/DB候选发现和原生工作台回归再次通过：52行中Python/CTC一致52、人工精确51；375字符/370比较/5缺失/1超限、37个作者候选，原始识别差异及无配置ROI→NG政策未改。

日志：`artifacts/result-browser-verification.log`、`artifacts/result-browser-tests.log`、`artifacts/result-browser-format.log`及标签侧`artifacts/result-browser-verification.log`。没有改动现有业务页面布局、算法或显示复制链；没有重新发布历史包。WPF物理输入路由、相机现场吞吐仍不在本轮证据范围。

## 历史：删除图像源的通用裁剪

按客户要求删除源接口、内部存储和示例中的Crop，以及13项裁剪专用测试；保留并调整边缘图块独立性测试。ROI由算法内部处理，源只保留像素读取、显示分块和租约管理。

两框架共享测试各 **81通过**、算法各 **46通过**、标签各 **219通过**，共692项。两套解决方案构建通过，原生WinForms/WPF画布及示例回归通过；本轮未重复模型相关OCR回归，上一次记录见下节。日志：`artifacts/remove-crop-verification.log`及标签侧`artifacts/remove-crop-build.log`、`artifacts/remove-crop-tests.log`。

## 历史：统一图像源与分支生命周期

客户创建、采集/文件读取、缓冲池发布、通用算法及原生画布统一使用`IImageSource`；`ImageBuffer/MemoryImageSource`不再导出。增加后台排队前保留源、直接显示源和清空画布的入口。按客户要求删除统一源的通用裁剪接口及实现，ROI处理由算法负责。两个标签工作台输入也已迁移；业务快照/报告仍显式复制，不宣称全链零复制。详见[统一图像源](UNIFIED_IMAGE_SOURCE.md)。

两框架共享测试各 **94通过**，算法各 **46通过**，标签各 **219通过**，共718项。真实WinForms/WPF探针验证客户释放后仍可绘制、换图回池、旧序号拒绝及清空待显示源；完整OCR/物理外观/DB候选发现和工作台回归通过。日志`artifacts/source-verification.log`、标签侧同名日志；格式检查`artifacts/source-format.log`。标签旧枚举测试在重新编译时可能产生MSTEST0032提示，不是源迁移失败。

这是破坏性公开类型变更，宿主及自定义源/算法须重新编译；旧发布包没有更新。WPF物理输入和真实相机吞吐仍未验证。

## 历史：单类型文件、功能目录与枚举E前缀

原有128个C#文件重组为297个，新增3个枚举回归文件后共300个。独立类型分别存放，36个嵌套辅助实现保留partial外层作用域；19个自定义枚举统一E前缀，成员名称、数值和Flags语义不变。原有296项类型/程序集及成员指纹比较通过。

双框架共享测试各 **75通过**，算法各 **27通过**，标签各 **213通过**；原生控件、真实OCR/外观/发现工作流通过，构建零警告/错误。日志`artifacts/structure-integrity.log`、`artifacts/structure-verification.log`及标签侧`artifacts/structure-verification.log`。入口见[结构规范](STRUCTURE.md)与[源码索引](SOURCE_INDEX.md)。旧发布副本不更新；外部宿主须适配新的枚举类型名并重编译。WPF物理输入验证边界不变。

## 历史：ROI移入UI层

`DP.Vision.UI`承接全部ROI定义、编辑器、编辑文档/XML和原生控件接口；Core/Algorithms及标签Runtime/Adapter没有UI依赖。新增确认到Region的像素中心采样、孔洞保留、快照独立性、开放轮廓/隐式填充拒绝、越界/禁用/预算/取消及程序集边界回归。

共享测试两框架各 **68通过**，算法各 **24通过**；两套原生控件和示例验证通过，Release零警告/错误。日志 `artifacts/roi-ui-layer-verification.log`。标签各 **203通过**，真实OCR与原生UI完整验收通过，日志 `../DP.LabelInspection/artifacts/roi-ui-layer-verification.log`。WPF物理键鼠路由仍未验证。

## 历史：工程收敛

实际DB检测器已迁入 `DP.Vision.OnnxDetection`，无标签业务依赖；旧标签四个算法工程已删除，宿主统一引用 `DP.LabelInspection.Runtime`。完整日志 `artifacts/project-consolidation-verification.log`：core两框架各60、算法各24通过，原生控件通过；标签两框架各203及实际DB/OCR/原生UI验收通过。WPF物理输入仍未验证。

下列批次为历史验证记录。

## 构建与回归

- `DP.Vision.sln` Release：0警告/0错误。
- `DP.Vision.Tests`：net48 **60通过**；net8.0-windows **60通过**，均0失败/跳过。
- `DP.Vision.Algorithms.Tests`：net48/net8各**24通过**，明确状态、实际OpenCV、证据租约、坐标及不支持输入；完整日志 `artifacts/roi-migration-verification.log`。
- 标签SDK兼容回归：net48/net8各**197通过**，原有OpenCV、WinForms、WPF、真实OCR/单字/发现/字库流程通过。
- 独立控件与通用Core不引用HALCON、OpenCV或DP.LabelInspection。兼容Adapter仅反向引用Core。

## 原生控件探针

`tools/DP.Vision.Probe`在STA线程创建真正的WinForms控件和真正的WPF控件，分别运行于net48和net8。

已检查：

1. Region孔洞、分离部分、原始像素成员和显示颜色。
2. 亚像素XLD可见描边。
3. 分层隐藏/显示后恢复。
4. 共享视图缩放/平移与显示像素一致。
5. 新帧无覆盖层时不沿用旧证据；旧序号不能把新帧覆盖回去。
6. Gray8、Gray16、Bgr24、Rgb24、Bgra32、Rgba32六种输入布局的实际显示颜色。
7. 8K/16K按需图块源的两套原生显示，缓存记账不超8MiB配置预算。
8. 当前显示进程不加载HALCON和标签业务程序集。

图像证据只来自控件缓冲区；不截取桌面其他应用。

### 修复过的真实问题

WPF分块在非整数缩放处出现细缝，白色原图的分块接缝像素被混入背景颜色。先加入接缝像素断言，复现`Tile seam altered uniform image pixels.`，再使用共享边缘device guideline对齐修复。修复后在原始和缩放/平移视图均通过。另外复现并修复了视口完全远离图像时，两个负向范围相乘导致误报图块预算超限的问题；日志`artifacts/offimage-red.log`，空交集现在直接返回零图块。

日志：`artifacts/seam-red.log`；最终图像/记录：`artifacts/native-net48/`、`artifacts/native-net8/`。

## Core行为覆盖

- 输入所有权、独立Retain、已释放句柄不可使用。
- 池预算、耗尽背压、读者未释放时禁止复用、池关闭不破坏活动读者、发布后禁止写入。
- 最新预览释放旧租约、并发顺序、水位线和帧/结果身份校验。
- 半开游程、孔洞/离散像素、空Region/空Contour、旋转ROI、多边形/轮廓语义区别。
- LOD默认关闭、1:1原始点、端点保留、原始点不变、闭合轮廓不简化、误差量化及工作预算回退。
- 灰度不膨胀、Gray16只映射显示、RGBA顺序、非法容差/复制范围。
- 16K图块规划、边缘tile数据、LRU预算与淘汰释放、鼠标锚点数学。

## ROI编辑（0.1.0-preview.2）

- 后续9项顶点编辑测试：投影插入、开放/闭合边、重复首尾闭合维护、点数下限、单轮廓和总预算拒绝、Undo/Redo、仅选中配置可编辑、工具输入和非法容差保护。
- 首批20项新增Core测试：预览/提交隔离、取消/空拖动、旋转缩放、圆约束、顶点编辑、多点形状、历史分支/容量、元数据、Region整数平移、XML往返/未知格式/DTD拒绝、容量及非法坐标的事务拒绝。
- WinForms发送实际控件Windows鼠标/键盘消息，验证矩形创建、移动、Esc取消、Enter结束多边形及只读证据隔离。
- WPF调用与原生处理器共用的`ProcessRoiPointer`，验证同一坐标路径、文档事务和原生渲染/Dispatcher。**物理鼠标/键盘路由未验证通过**：系统窗口命中结果与目标WPF HWND不一致，停止全局注入；不能据程序化Seam成功推断物理捕获或双击成功。
- 两端验证编辑期间latest预览暂停取帧、取消后恢复。并不暂停检测，也不丢弃检测任务。
- 两种Demo×两个框架均完成ROI工具栏/列表/元数据/XML往返烟测，并从控件缓冲区保存截图。
- 日志：`artifacts/vertex-verification-final.log`、`artifacts/vertex-label-verification.log`；当前原生及Demo图像见`artifacts/vertex-final/`（`net48/`、`net8/`及`demo-*.png`）；未指定OutputDirectory时，verify会新建带时间戳的输出目录。

详见[ROI_EDITOR.md](ROI_EDITOR.md)。新增IVisionCanvas成员要求自定义实现同步更新；原RoiDefinition四参数构造保留。

## 性能实测

最终40个独立进程：`artifacts/comparison-final/`。

详见[PERFORMANCE.md](PERFORMANCE.md)。包括不利结果：10万点频繁重建时开启LOD更慢。8K/16K按需源是不同测试口径，不与完整图像管线混算。

## 仍未验证/实现

- ROI高级编辑：多选对齐、Region笔刷/复杂拓扑；WPF物理输入/捕获/双击还需独立桌面验证。
- 所有ROI布尔运算、标定世界坐标、全HALCON/XLD属性与子类型。
- 大文件异步分块解码/预取；慢I/O不能直接进入当前同步ReadTile。
- GPU/Direct2D实现、WPF同口径40进程性能矩阵、长期耐久/泄漏压力测试。
- 真实16K完整输入＋相机＋算法连续生产流水线性能。
- 标签工作台已替换图像/证据绘制后端，保留原矩形ROI业务编辑逻辑；全量业务UI重写、任意形状配方迁移与dist重发未实施。见`../DP.LabelInspection/VISION_WORKBENCH_MIGRATION.md`。

## 复现

```powershell
powershell -ExecutionPolicy Bypass -File verify.ps1
```

默认验证不需要HALCON。只有与HALCON做性能对照的可选工具需要该运行环境及许可。
