# ROI编辑与配置持久化（0.1.0-preview.2）

## 已实现

`RoiEditor`现属于 **DP.Vision.UI**（netstandard2.0，共享UI编辑层），不依赖具体WinForms/WPF或标签业务。两个原生控件引用该层，通过相同事务模型和坐标转换接入。

## 层次与提交边界

- `DP.Vision`：图像、不可变几何、Region像素集合、轮廓及纯几何栅格化；不引用UI，不再定义ROI编辑类型。
- `DP.Vision.UI`：`RoiDefinition / ERoiPurpose / ERoiConstraint / RoiEditor / RoiDocument / RoiDocumentXml`，鼠标动作、控制点、撤销事件及 `IVisionCanvas`。
- `DP.Vision.Winform / WPF`：原生控件和物理输入接入。不存在从后台算法到共享UI层的引用。

```csharp
using DP.Vision;
using DP.Vision.UI;

// 在UI线程取得已提交的不可变定义；不要把活动editor传给后台。
RoiDefinition confirmed = editor.Document.Rois[0];
RegionGeometry region = confirmed.ToRegion(imageWidth, imageHeight);
// 后台接收原始图像 + region + 业务参数。
// 后续编辑、删除或撤销不改变本次region。
```

`ToRegion`调用底座的 `RegionRasterizer`，按原图像素中心采样。矩形使用半开范围，椭圆/填充多边形包含边界；已有Region保留游程与孔洞。超出图像、禁用的ROI、开放/未显式填充的轮廓、超工作量预算均明确拒绝，不自动裁剪、闭合或以外接框替代。可取消；大区域转换可由宿主在捕获快照后放到工作线程。

ROI文档XML是重新编辑所用配置，不是后台必须依赖的输入类型。Include/Exclude由宿主映射到业务检查/排除语义。

这是源码/API分层变更，没有保留旧 `DP.Vision.RoiEditor` 类型转发。标签侧旧矩形编辑手势，以及各算法从矩形范围扩展为任意Region输入，不包含在本次DP.Vision分层迁移中。

| 操作 | 支持 |
|---|---|
| 创建 | 轴对齐矩形、旋转矩形、圆、椭圆、多边形、开放折线、点 |
| 选择/移动 | 仅ROI配置文档；框内/线附近选择，整体移动 |
| 控制点 | 矩形/椭圆8点缩放、旋转柄、圆半径柄、原始折线/多边形顶点 |
| 轮廓顶点增删 | 选中配置后，选择插入/删除顶点工具并单击；一击一个可撤销事务 |
| 圆约束 | 保持等半径，半径拖动时中心不变 |
| 矩形约束 | 普通矩形保持轴对齐；旋转矩形保留旋转能力 |
| Region | 加载、显示、精确整像素平移；不提供自由笔刷或拓扑编辑 |
| 元数据 | Include/Exclude、Enabled，均可撤销 |
| 历史 | 有界Undo/Redo，一次拖动只有一次提交；默认最多100步 |
| 持久化 | 版本化`.roi.xml`，坐标系显式为`image-edges` |

`RoiDefinition.Constraint`默认None；Circle和AxisAligned在构造时验证，不能只是显示标签。

## 运行和交互

```text
DP.Vision/start-winforms.cmd
DP.Vision/start-wpf.cmd
```

示例增加工具选择、ROI列表、启用/排除开关、撤销/重做、删除及保存/加载按钮。

- 矩形/椭圆：左键拖出范围。
- 圆：按下位置为圆心，拖动距离为半径。
- 多边形/折线：逐点单击，Enter或双击结束；Backspace撤销最后一个待定点。
- 编辑：选择工具后拖动形状内部/线，或拖动控制点；旋转柄位于形状上方。
- Esc：取消未提交事务。
- Delete：删除整个选中ROI，不是删除某一个顶点。
- Ctrl+Z：撤销；Ctrl+Y / Ctrl+Shift+Z：重做。
- 中键/右键：平移；滚轮缩放；Home适应。内置视图操作会取消编辑事务；宿主直接修改Viewport前也应先调用Cancel。

插入/删除顶点前，先用选择工具选中配置轮廓，再切换相应工具。插入位置投影到鼠标附近的最近边，不把屏幕偏移写入轮廓；开放折线没有隐含闭合边。删除后至少保留开放折线2点、闭合轮廓3点。显式重复首尾点的闭合表示会保留，删除首点时同步更新重复尾点。点ROI请用Delete删除整个对象。

增删工具持续有效，不自动选择其他轮廓或检测证据；未命中、点数下限、单轮廓/总几何预算拒绝都不写历史，预算错误通过ValidationError报告。SDK也可调用`InsertVertex(point, tolerance)` / `DeleteVertex(point, tolerance)`，参数使用原图坐标。

操作完创建工具自动回到选择工具。配置ROI有自己的显示层和控制点；示例原有`rois`证据层仍然只读，不因为名字中有ROI就变成配置。

## 使用方式

```csharp
var editor = new RoiEditor(historyLimit: 100);
canvas.Editor = editor; // WinForms或WPF的IVisionCanvas
editor.DocumentChanged += (_, e) =>
{
    // e.After是已提交配置。PointerMove不会触发此事件。
    // 应由宿主决定何时更新算法配方，而不是让画布直接调用算法。
};
editor.Tool = ERoiTool.RotatedRectangle;

var xml = RoiDocumentXml.Serialize(editor.Document);
var restored = RoiDocumentXml.Deserialize(xml);
editor.Load(restored); // 完整校验完成后替换；清空旧Undo/Redo历史
```

`Document`始终是已提交快照；`DisplayLayer()`可以包含正在拖动的预览，但不能把它当成算法配置保存。移动/编辑创建新的Geometry，原始ROI快照和检测证据都不变。

`ProcessRoiPointer(action, clientPoint)`是鼠标/触笔/宿主自动化的统一输入Seam：WinForms使用控件像素，WPF使用DIP；控件统一换算为原图坐标。程序化输入由宿主管理捕获，不需要全局鼠标注入。

## 帧与编辑的关系

- `PostFrame`仍只保留最新预览。正在编辑时，控件暂缓从邮箱取帧，避免按下/松开对应不同图像；邮箱始终有界，并不是暂停检测算法。
- 提交或取消后，下一次UI轮询显示最新完整帧。
- 显式调用`Present`更换图像身份，会取消未提交事务。
- ROI配置可以跨同坐标布局的帧复用；**更换图像分辨率不会自动缩放ROI**。是否仍适用由宿主校验，不能静默改变检测范围。
- ROI编辑层不用XLD显示LOD，控制点始终对应原始配置几何。

## XML规则与限制

根元素：`<roi-document version="1" coordinates="image-edges">`。

保存ID、用途、启用状态、约束，以及矩形/椭圆参数、轮廓点/开闭/填充状态、Region半开游程。double使用InvariantCulture和往返格式，保持亚像素值。空Region/空Contour也能保留。

读取采用固定类型白名单，不接受运行时类型名。DTD/外部实体禁止，未知版本/坐标系/结构拒绝；XML最多16Mi字符。配置最多512个ROI、单轮廓4096顶点、总点/游程100000。交互容量或坐标拒绝通过`ValidationError`报告，保留已提交配置。

多边形允许自交，填充使用偶奇规则；没有自动修正或解交。形状允许位于原图外，算法适配器需要检查合法处理范围。Region移动按整像素取整，不把像素区域转换成亚像素掩码。

当前未提供：批量/多选对齐、Region笔刷/布尔运算、复杂拓扑编辑、标定世界坐标、图片本体与ROI打包、多人并发配置锁。示例文件保存是普通文件写入，不是带崩溃恢复的生产配方仓库。

## 验证口径

- Core测试覆盖事务、取消、移动、旋转缩放锚点、圆约束、顶点编辑、历史边界、Region平移、XML往返与DTD拒绝。
- WinForms：针对实际控件发送Windows鼠标/键盘消息，验证创建/移动/Esc/Enter/已完成轮廓顶点增删和撤销/只读证据隔离/预览冻结和恢复。
- WPF：验证**统一指针输入Seam＋真实原生控件渲染/Dispatcher**。本机物理输入测试的系统命中窗口与目标WPF窗口不一致，因此已停止全局注入；**没有把WPF物理鼠标/键盘路由、捕获和双击标记为已验证通过**。
- 两运行时均完成控件探针及带ROI工具栏/列表的Demo烟测。截图仅来自控件/窗口缓冲区，不捕获桌面其他应用。
