using System;
using System.Threading;
using DP.Vision.Acquisition;

namespace DP.Vision.Halcon;

/// <summary>
/// Provider 侧一帧 HALCON 原始图像的中立视图。
/// <para>
/// 与 Basler 侧刻意保持同构（都只用中立概念描述设备帧），但有一处**厂商差异必须显式保留**：
/// HALCON 的通用采集层不提供设备帧序号，因此这里没有 <c>ImageNumber</c>，
/// 中立帧的 DeviceSequence 只能上报空值。这属于 Provider 能力差异，不是缺陷；
/// 若某个具体接口暴露了帧计数节点，应在现场验收中单独确认后再引入。
/// </para>
/// <para>
/// 生命周期：设备帧只在回调/循环持有期间有效。使用方必须在结束前完成所有像素读取并调用
/// <see cref="IDisposable.Dispose"/> 声明"本帧不再使用"；底层图像到底由谁释放由实现决定。
/// </para>
/// </summary>
internal interface IHalconGrabFrame : IDisposable
{
    /// <summary>帧宽（像素）。</summary>
    int Width { get; }

    /// <summary>帧高（像素）。</summary>
    int Height { get; }

    /// <summary>HALCON 像素类型名，例如 <c>byte</c>、<c>uint2</c>。</summary>
    string PixelTypeName { get; }

    /// <summary>通道数；HALCON 的 <c>count_channels</c> 结果。</summary>
    int ChannelCount { get; }

    /// <summary>本帧被观测到的时刻。</summary>
    DateTimeOffset CapturedAtUtc { get; }

    /// <summary>
    /// 把第 <paramref name="channel"/> 个通道的原始像素复制到目标缓冲。
    /// <para>
    /// 三通道图像按 HALCON 的约定排序：0 为红、1 为绿、2 为蓝。
    /// 目标缓冲长度必须正好是 <c>Width * Height * 每像素字节数</c>（<c>byte</c> 为 1、<c>uint2</c> 为 2）；
    /// 这里只做逐通道搬运，布局判断与预算校验由 <see cref="HalconNeutralFrames"/> 统一负责。
    /// </para>
    /// </summary>
    /// <param name="channel">通道下标；单通道图像只接受 0。</param>
    /// <param name="destination">目标缓冲。</param>
    void CopyChannelInto(int channel, byte[] destination);
}

/// <summary>
/// 一台 HALCON 采集设备上全部设备侧动作的唯一入口；由 HALCON 实现或由测试替身实现。
/// <para>
/// 本接口代表<b>同一个 <c>HFramegrabber</c> 句柄</b>：主动单次采集与外部回调持续取流都走它，
/// 因此一台物理设备上只会存在一个设备对象与一条取流通道，两种模式不能同时进行。
/// 设备在 Device 释放之前保持打开，跨采集请求与跨布防复用——真实 SDK 上重复打开同一台相机
/// 通常直接失败，而且每次打开/关闭都会把采集参数重置回默认。
/// </para>
/// <para>
/// 与 pylon 的**结构差异**：HALCON 没有 <c>ImageGrabbed</c> 那样的事件回调，
/// 只有阻塞式的抓取，因此本接口只暴露"抓一帧"，
/// **采集循环、线程所有权、停止等待与"停流后不得再交付"全部由 <see cref="HalconStreamSession"/> 自建**，
/// 不依赖 SDK 提供任何保证。
/// </para>
/// <para>
/// 停止契约：<see cref="AbortGrab"/> 用于把一次阻塞中的抓取提前打断，使停止不必等满抓取超时；
/// 它只尽力而为——HALCON 的 <c>do_abort_grab</c> 是否被某个采集接口支持取决于该接口，
/// 不支持的接口会直接失败，调用方必须容忍并退化为"等满超时"。
/// </para>
/// </summary>
internal interface IHalconStreamCamera : IDisposable
{
    /// <summary>设备是否已打开。</summary>
    bool IsOpen { get; }

    /// <summary>
    /// 打开设备；已打开时为空操作。失败必须抛出，且不得留下半开状态。
    /// <para>
    /// 打开参数里的触发设置一律走"接口默认值"，真正的触发模式与触发源由
    /// <see cref="ApplyArmParameters"/> 在打开之后写入——这样两家 Provider 的
    /// "先打开、后写参数"顺序一致，厂商差异集中在参数写入方式上。
    /// </para>
    /// </summary>
    void Open();

    /// <summary>关闭并释放设备对象；未打开时为空操作。之后 <see cref="Open"/> 会重新创建一个设备对象。</summary>
    void Close();

    /// <summary>
    /// 把采集参数写到设备上；为空的物理量表示保持设备当前设置。
    /// <para>
    /// 主动单次采集与长连接布防共用这一条写入路径：两处各写一份会让"先关自动曝光再写手动值"
    /// 或"保持当前触发设置"这类语义各自漂移。参数到设备参数的翻译全部由
    /// <see cref="HalconFramegrabberParameters"/> 决定，本方法只负责执行。
    /// </para>
    /// <para>
    /// 与 <see cref="CaptureSingleFrame"/> 互斥：句柄正被一次单次采集占用时必须明确失败，
    /// 否则布防会把那次采集的设备设置改掉，采到的就不是请求那一刻的图。
    /// </para>
    /// </summary>
    /// <param name="triggerMode">触发模式。</param>
    /// <param name="exposureMicroseconds">曝光，单位微秒；空表示不改写。</param>
    /// <param name="gainDecibels">增益，单位分贝；空表示不改写。</param>
    /// <param name="grabTimeoutMilliseconds">单次抓取等待上限；到点未出图按超时处理而不是设备故障。</param>
    void ApplyArmParameters(
        EVisionTriggerMode triggerMode,
        double? exposureMicroseconds,
        double? gainDecibels,
        int grabTimeoutMilliseconds);

    /// <summary>
    /// 按请求单次抓取一帧，并把请求参数写到设备上。
    /// <para>
    /// 与 <see cref="GrabOnce"/> 共用同一条取流通道：设备正在持续取流时本调用必须明确失败，
    /// 而不是抢占通道；实现必须在返回前结束本次抓图，不把残留状态留给下一次布防。
    /// </para>
    /// </summary>
    /// <param name="triggerMode">触发模式。</param>
    /// <param name="exposureMicroseconds">曝光，单位微秒；空表示不改写设备设置。</param>
    /// <param name="gainDecibels">增益，单位分贝；空表示不改写设备设置。</param>
    /// <param name="timeoutMilliseconds">等待一帧的最长时间。</param>
    /// <param name="cancellationToken">协作取消；在调用边界检查，无法中断已经进入 SDK 的阻塞抓取。</param>
    /// <returns>抓到的设备帧；所有权随返回值转给调用方，调用方负责释放。</returns>
    IHalconGrabFrame CaptureSingleFrame(
        EVisionTriggerMode triggerMode,
        double? exposureMicroseconds,
        double? gainDecibels,
        int timeoutMilliseconds,
        CancellationToken cancellationToken);

    /// <summary>
    /// 阻塞抓取一帧；长连接采集循环每次拉取一帧。
    /// <para>
    /// 超时（HALCON 错误码 5322）必须抛 <see cref="HalconGrabTimeoutException"/>，
    /// 因为外部触发下"这一轮没有触发到来"是正常现象，不能当成设备故障。
    /// 设备故障与许可证故障必须抛对应的 <see cref="VisionAcquisitionException"/> 子类。
    /// </para>
    /// </summary>
    /// <returns>调用方拥有的设备帧。</returns>
    IHalconGrabFrame GrabOnce();

    /// <summary>尽力中止当前阻塞中的抓取；不支持时允许失败。可从其他线程调用。</summary>
    void AbortGrab();
}

/// <summary>抓取超时；只表示"这一轮没有等到帧"，与设备故障、许可证故障互不等价。</summary>
internal sealed class HalconGrabTimeoutException : VisionAcquisitionException
{
    /// <summary>创建抓取超时。</summary>
    /// <param name="message">诊断说明。</param>
    public HalconGrabTimeoutException(string message)
        : base(message)
    {
    }
}

/// <summary>创建真实长连接设备；未装配 HALCON SDK 时明确失败，而不是静默降级。</summary>
internal static class HalconStreamCameras
{
    /// <summary>
    /// 当前程序集是否包含 SDK 实现；不是设备在线或许可证通过的证明。
    /// <para>
    /// 它与 <see cref="Create"/> 出自同一个条件编译开关，因此"插件报告可用"与"工厂能真的造出设备"
    /// 不会被两条各自演化的事实描述拆开——旧的 OnDemand 读取器已删除，本属性是唯一入口。
    /// </para>
    /// </summary>
    public static bool IsSdkEnabled
    {
        get
        {
#if HALCON_SDK
            return true;
#else
            return false;
#endif
        }
    }

    /// <summary>创建绑定对应的长连接设备。</summary>
    /// <param name="binding">Provider 私有绑定。</param>
    /// <returns>尚未打开的设备对象。</returns>
    /// <exception cref="VisionProviderUnavailableException">本程序集未装配 HALCON SDK。</exception>
    public static IHalconStreamCamera Create(HalconAcquisitionBinding binding)
    {
        if (binding is null)
            throw new ArgumentNullException(nameof(binding));
#if HALCON_SDK
        return new HalconFramegrabberCamera(binding);
#else
        throw new VisionProviderUnavailableException(
            HalconAcquisitionProvider.ProviderIdentity,
            "本程序集未装配 HALCON SDK（HALCON_SDK 未定义）；请安装 HALCON 运行时并以 HALCONROOT 或 HalconDotNetPath 重新构建 DP.Vision.Halcon。");
#endif
    }
}
