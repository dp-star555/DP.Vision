# 统一图像源与所有权

## 客户只管理自己取得的源

`VisionImage.CopyFrom(info, pixels)`、`IImageFileReader.ReadAsync`及`FrameWriter.Publish()`统一返回`IImageSource`。相机像素不从这里进入：真实采集统一走`DP.Vision.Acquisition`的中立契约与Provider插件体系，旧`ICameraCapture`已删除。内部`ImageBuffer`、`MemoryImageSource`不再导出，没有旧公开类型的兼容别名；宿主及自定义算法需要重新编译。

通用OCR、条码、分割、字形及表面质量接口现在接受`IImageSource`。带原图身份的`ImageFrame`和带叠加证据的`CanvasFrame`仍是元数据/证据包，内部持有源，不是另一套像素存储入口。Blob、测量等已有带身份契约保持其证据语义，不删除帧身份。

## 最小用法

```csharp
using DP.Vision;
using DP.Vision.UI;

using var source = VisionImage.CopyFrom(info, pixels);
canvas.SetImage(source, "capture-001", 1); // UI线程调用，画布内部保留。
var bytes = new byte[source.Info.ByteLength];
source.CopyTo(0, bytes, 0, bytes.Length);
```

创建、`Retain`、`ReadTile`返回的每个源由取得者独立释放。变量赋值不是保留。像素不可修改；`CopyTo`只写客户拥有的目标数组。

- `Retain`共享存储，新增独立租约。
- `ReadTile`按采样级别和固定网格生成独立源，仅边缘可能小于常规正方形；低分辨率按间隔取样，不能代替原图缺陷复核。
- `CopyTo`的偏移和数量单位为字节，不是像素。

图像源不提供通用裁剪入口。算法内部自行处理ROI及所需像素；窗口显示局部范围由视口和图块规划负责，不要求客户先裁图。

## 显示分支

`SetImage(source, frameId, sequence, overlay)`和`PostImage(...)`是`DP.Vision.UI`中的扩展方法：内部创建临时预览包、调用原生画布并释放临时租约。客户不必同时管理源和预览包。

- `SetImage`在UI线程使用。
- `PostImage`允许生产者线程提交；返回前已经保留，拒绝旧序号时不遗留租约。
- 帧身份随原图内容改变，序号在画布会话内递增；同帧证据必须匹配身份，不以自动生成的新身份掩盖证据错配。
- 画布换图、`ClearImage()`或释放时交还自己的源。清空也释放调用时已排队的预览和显示缓存，不重置预览序号水位。并发生产者随后提交的新帧仍可进入；停止预览应先停止生产者。
- 最后显示的一帧会继续占用池化槽位，这是有意的所有权保证。缓存限制不是整个进程的内存限制。

## 后台处理分支

```csharp
Task<byte> task;
using (var source = VisionImage.CopyFrom(info, pixels))
{
    canvas.PostImage(source, "capture-002", 2);
    task = ImageProcessing.RunAsync(source, (input, token) =>
    {
        // 仅演示像素读取；真实算法同样借用input，不得释放传入句柄。
        var data = new byte[1];
        input.CopyTo(0, data, 0, 1);
        return Task.FromResult(data[0]);
    }, cancellationToken);
}
byte firstPixel = await task; // 原始客户源已释放，任务自己的租约仍安全。
```

`RunAsync`同步保留输入，再排队；不能在后台启动后才Retain。处理返回的任务必须覆盖全部图像访问，不得脱离任务启动后台读取。结果若仍持有原图，结果本身须另行保留，调用方再按结果契约释放。

不将取消标记传给`Task.Run`：排队期间取消也必须进入清理路径，不能跳过委托而泄漏已保留租约。入口在开始处理前检查取消，处理中由算法协作响应；不强行提前释放仍在读取的源。同步算法直接调用时只借用输入，客户维持有效期到调用结束；需要独立后台生命周期时使用此入口。

## 缓冲池

`FrameWriter.Publish()`不复制像素，返回源后写句柄永久失去写权限。发布后的writer.Dispose不会归还源；最后一个源租约结束才回池。未发布时writer.Dispose直接归还槽位。

池仍懒分配、保留清零及容量上限：局部Write后也能Publish，因此不能仅因同尺寸而删除清零。Write仍复制外部数组，发布及Retain不复制；没有零分配、全链零复制或吞吐保证。

## 标签检测与工作台

引用`DP.LabelInspection.Adapter.Vision`并导入同名命名空间后，`IInspectionEngine`及具体引擎可直接使用源扩展入口：

```csharp
using DP.LabelInspection.Adapter.Vision;

Task<InspectionReport> task;
using (var source = VisionImage.CopyFrom(info, pixels))
{
    canvas.PostImage(source, "capture-003", 3);
    task = engine.InspectAsync(source, recipe,
        reference: referenceSource,
        cycleId: "cycle-003",
        cancellationToken: cancellationToken);
}
var report = await task;
```

实际图和可选参考图在返回任务前都已保留，成功、失败、取消后释放内部租约。引擎仍必须存活到任务结束。正式ROI分阶段执行、原始读取、质量判定和阻断语义未改变。

两个标签工作台的`SetActualImage`及`SetReferenceImage`也改为接收源；返回后客户可释放输入。**工作台及检测适配内部仍复制为标签不可变业务快照**，支持Gray8/Bgr24、单边最大12000、总像素不超过1600万。Gray16等布局显式拒绝，不静默降位深。

`DP.LabelInspection.Contracts.ImageFrame`仍用于配方资源、字库和报告持久化，以及低层业务快照请求；不是需要客户再管理的租约。未重写存储格式，也未宣称标签链路全程共享原始数组。业务快照到通用算法的转换仍有复制，后续性能优化需要单独验证，不以修改证据所有权换取速度。

## 扩展实现迁移

自定义`IImageSource`除了Info、Retain、Dispose，现在还需提供CopyTo；ReadTile返回类型改为IImageSource。各读取方式必须表示同一份不可变原始内容，支持并发读取和安全保留。自定义图像源若需要慢I/O，不得在画布同步渲染期间执行阻塞网络访问。

旧`ImageBuffer.CopyFrom`调用改为`VisionImage.CopyFrom`；旧`new MemoryImageSource(buffer)`不再需要。通用算法声明中的ImageBuffer改为IImageSource。返回图像的结果仍保留原有独立租约规则。

## 验证与边界

- 边缘采样图块像素正确，原图释放后图块仍独立有效。
- 同一池化源的预览与后台独立存活；后台异常、运行中取消及排队后立即取消不泄漏槽位。
- 双输入标签源保留、实际图保留失败的参考图清理、不支持位深的释放和真实引擎接入。
- 真实WinForms/WPF探针覆盖客户释放后绘制、换图归还、旧序号拒绝及清空待显示帧，两个框架均运行。
- 仍有ImageInfo/int数组容量限制；不因统一源接口就获得超过2GiB逻辑图像、文件按需解码或相机吞吐保证。
- WPF物理输入路由仍未验证；此次不发布新安装包，旧artifacts/dist中的包不是新接口版本。
