using System;
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
/// 真实长连接会话所依赖的设备侧动作；由 HALCON 实现或由测试替身实现。
/// <para>
/// 与 pylon 的**结构差异**：HALCON 没有 <c>ImageGrabbed</c> 那样的事件回调，
/// 只有阻塞式的异步抓取，因此本接口只暴露"抓一帧"，
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

    /// <summary>把布防参数写到设备上；为空的 <paramref name="triggerSource"/> 表示不指定触发源。</summary>
    /// <param name="triggerMode">触发模式。</param>
    /// <param name="triggerSource">外部触发的触发源，例如 GenICam 的 <c>Line1</c>；只在外部触发模式下使用。</param>
    /// <param name="grabTimeoutMilliseconds">单次抓取等待上限；到点未出图按超时处理而不是设备故障。</param>
    void ApplyArmParameters(EVisionTriggerMode triggerMode, string? triggerSource, int grabTimeoutMilliseconds);

    /// <summary>
    /// 阻塞抓取一帧。
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
