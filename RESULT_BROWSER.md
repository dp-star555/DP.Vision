# 视图浏览器

Vision只负责 **视图集合 → 图像与图层**，不负责节点、流程运行或执行状态。

```text
视图集合
├─ 原图：图像 + ROI图层
├─ 二值图：图像 + Region图层
└─ 结果视图：图像 + 轮廓、标记等图层
```

两种原生控件：`DP.Vision.Winform.ResultBrowserControl`、`DP.Vision.WPF.ResultBrowserControl`。
界面为 **[视图下拉] [图层多选] [适应窗口]**；图层提供全选、全不选、恢复默认。单视图时隐藏多余选择栏，状态栏显示视图名称。WPF不嵌套WinForms。

## 接入

下面的`browser`可以是任一种原生控件，输入均为已有的`IImageSource`和同帧几何快照：

```csharp
using DP.Vision;

bool accepted = browser.SetViews(new[]
{
    new VisionView("raw", "原图", rawFrameId, rawSource, rawOverlay),
    new VisionView("binary", "二值图", binaryFrameId, binarySource, binaryOverlay),
});

// 此时浏览器已保留需要的独立源租约，客户可释放自己持有的源。
// accepted只表示显示集合被接受，不表示业务OK。
```

- `VisionView`是借用描述，不Retain、不复制像素、不需要Dispose。输入源须保持有效直到SetViews返回。
- 同一视图是一张底图加属于该图坐标系的图层；没有底图的业务状态不是视图。没有可显示图像时交空集合。
- `SetViews`完整替换集合，不做增量拼接。视图键须唯一，顺序就是下拉顺序；名称不作为身份。
- 同一稳定视图键仍存在时保留选择，否则选择首个视图。
- 图层名称使用`CanvasLayer.Name`，省略时为Id；默认显隐使用Visible。
- 局部裁图、旋转或配准后的几何必须由调用方显式变换，不能仅改FrameId就拼到另一底图上。

业务侧只需知道中立的`IViewDisplaySink.SetViews(IEnumerable<VisionView>)`，无需引用原生UI程序集。

## 与流程层的职责分离

节点选择、运行身份、执行状态、更新版本及迟到结果过滤由外层管理。例如外层选中某个节点后，将它对应的视图集合交给Vision；Vision不知道这些图像来自哪个节点。

**SetViews是线程安全的集合替换，不是异步结果排序器。** 并发调用按入口接受顺序生效。外层须在同一个串行调度或宿主锁中完成“判断结果是否仍有效”和“提交显示”，避免先检查、后切换、再迟到提交的竞态；排队期间也必须持有所需源租约。Vision不接收RunId或业务Revision，不暗中推断最新业务结果。

内部只保留显示需要的身份：

- **ViewId / LayerId**：稳定选择键，显隐按二者组合记忆。如果外层希望隔离不同来源的偏好，请提供不同视图键。
- **FrameId**：不可变底图内容身份，像素变化必须更换；由提供者保证，不计算像素哈希。
- **界面Version及呈现Sequence**：用于合并刷新和画布呈现。切回旧视图也产生新呈现序号，不是流程更新版本。

集合替换后，旧界面的下拉/复选事件会被忽略，即使新旧视图键相同。这只是防止UI操作作用于错误集合，不代替外层的异步结果过滤。

## 选择与清空

```csharp
browser.Results.SelectView("binary");
browser.Results.SetLayerVisible("roi", false);
browser.Results.SetAllLayersVisible(false);
browser.Results.ResetLayerVisibility();
browser.ClearViews(); // 等价于SetViews(Array.Empty<VisionView>())

// 通常由内部定时器刷新；在UI线程需要立即呈现时：
browser.RefreshResults();
```

图层偏好不修改原始几何或CanvasLayer.Visible，替换/清空集合后仍按稳定键记忆，达到有限容量时淘汰最早记录。恢复默认只影响当前视图。

只切图层不运行算法、不使底图缓存失效；同FrameId、同尺寸/格式的叠加更新也保留底图缓存。切换视图或换像素内容时重新适应，不保存每个视图独立缩放位置。现有显示复制链没有改动，不是零复制优化。

控件每33ms检查版本并合并刷新，不为每次提交堆积UI回调；这不是实时帧率保证。

## 预算与生命周期

```csharp
var options = new DP.Vision.UI.ResultBrowserOptions(
    previewBytes: 256L * 1024 * 1024,
    maximumViews: 128,
    maximumGeometryElements: 2_000_000,
    maximumPreferences: 4096);
var browser = new DP.Vision.Winform.ResultBrowserControl(options);
```

- 视图容量1～256；累计几何默认上限200万，轮廓按点、Region按游程、其他几何计4，空几何至少计1。沿用底座每视图128层/10000显示项等限制。
- 数量或几何超限拒绝整次替换并返回false，旧集合不变；源保留途中异常回滚临时租约。
- 像素按每个保留视图的Info.ByteLength重复求和，共享源也重复记账。超预算优先保护当前选择，再淘汰较早使用的视图像素；单图超预算不获取源租约。
- 被淘汰的视图保留名称和图层元数据，明确显示“预览未保留”。切回时清空底图，不重跑算法、不沿用旧图；外层重新提交完整集合才可能重新保留像素。
- 偏好容量128～16384条，默认4096，至少容纳一个完整视图的全部图层。
- ClearViews释放会话保留的源，下一次UI刷新释放画布旧源；Dispose最终释放两者。已关闭入口SetViews返回false。
- **预算不是进程总内存上限**：不含客户独立源、提交/捕获临时租约、未刷新画布租约、几何和文本对象、原生位图及显示缓存。

控件创建、界面访问、最终释放在UI线程；SetViews和ClearViews允许后台调用。`browser.Results`为借用会话，客户不要单独Dispose。WinForms随控件释放；WPF的Unloaded仅暂停刷新，宿主最终关闭时仍必须Dispose。外层自行取消或等待自己拥有的工作任务。

## 本次接口收敛

彻底删除NodeDisplayResult、ENodeDisplayState、INodeDisplaySink及节点/运行选择入口，无兼容转发；NodeDisplayView由VisionView替代。控件名称ResultBrowserControl暂保留，含义为视觉结果视图浏览，不承担流程职责。调用方须重新编译并改用SetViews。没有自动修改现有Workflow或标签工作台的页面布局，旧发布包没有覆盖。

源码：中立描述在`src/DP.Vision/03.Display/Results/`，共享会话在`src/DP.Vision.UI/Results/`，两种控件在各原生程序集的`Results/`。

## 演示与验证

在DP.Vision目录运行：

```powershell
dotnet run --project samples/DP.Vision.Demo -c Release -f net8.0-windows -- --results
dotnet run --project samples/DP.Vision.Demo -c Release -f net8.0-windows -- --results --wpf
```

演示仅含原图、二值图、原图与叠加三个视图及重新加载按钮。合成示例不代表真实检测或准确率验收。

`verify.ps1`运行两框架、两种原生UI的视图下拉、图层弹出框、实际渲染像素、底图缓存、清空及Dispose回池回归，检查不存在节点选择器。共享测试覆盖集合原子替换、过期UI事件、容量/几何/像素预算及租约。物理键鼠路由、真实相机吞吐仍未验证。
