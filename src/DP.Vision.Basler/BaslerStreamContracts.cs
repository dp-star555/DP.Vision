using System;
using System.Threading;
using DP.Vision.Acquisition;

namespace DP.Vision.Basler;

/// <summary>
/// Provider 侧一帧原始抓图数据的中立视图。
/// <para>
/// 这里刻意只用中立概念描述设备帧（尺寸、格式名、帧序号、时刻、像素转换），不出现任何 pylon 类型，
/// 因此回调边界与像素落地逻辑可以在没有相机、甚至没有 pylon 运行时的机器上被完整验证；
/// 真实实现是 <c>PylonGrabFrame</c>。
/// </para>
/// <para>
/// 生命周期：设备帧只在回调期间有效。回调实现必须在返回前完成所有像素读取，
/// 并在结束时调用 <see cref="IDisposable.Dispose"/> 声明"本帧不再使用"；
/// 底层缓冲到底由谁释放由实现决定——pylon 在事件返回后自行释放抓图结果，因此它的实现是空操作。
/// </para>
/// </summary>
internal interface IBaslerGrabFrame : IDisposable
{
    /// <summary>设备报告的像素格式名，例如 <c>Mono8</c>、<c>Mono16</c>、<c>BGR8packed</c>。</summary>
    string PixelFormatName { get; }

    /// <summary>帧宽（像素）。</summary>
    int Width { get; }

    /// <summary>帧高（像素）。</summary>
    int Height { get; }

    /// <summary>设备帧序号；作为中立元数据的 DeviceSequence 原样上报，不做重编号。</summary>
    long ImageNumber { get; }

    /// <summary>本帧被观测到的时刻。</summary>
    DateTimeOffset CapturedAtUtc { get; }

    /// <summary>设备侧报告的目标格式转换所需缓冲区字节数（含行填充）。</summary>
    /// <param name="targetPixelFormat">pylon 目标格式名。</param>
    /// <returns>转换结果所需字节数。</returns>
    long GetConversionBufferSize(string targetPixelFormat);

    /// <summary>把像素转换并写入目标缓冲区。</summary>
    /// <param name="destination">目标缓冲区；长度由调用方按中立布局给出。</param>
    /// <param name="targetPixelFormat">pylon 目标格式名。</param>
    void ConvertInto(byte[] destination, string targetPixelFormat);

    /// <summary>声明本帧不再使用；实现决定是否真的释放底层缓冲。</summary>
    new void Dispose();
}

/// <summary>
/// 一台 Basler 相机上全部设备侧动作的唯一入口；由 pylon 实现或由测试替身实现。
/// <para>
/// 本接口代表<b>同一个已连接相机</b>：主动单次采集与外部回调持续取流都走它，
/// 因此一台物理设备上只会有一条取流通道（pylon 的 <c>StreamGrabber</c> 只有一个），
/// 两种模式不能同时进行。相机在设备被释放之前保持打开，跨采集请求与跨布防复用。
/// </para>
/// <para>
/// 线程契约：<see cref="StartContinuousGrab"/> 注册的帧回调由 SDK 回调线程直接调用，
/// 回调实现不得把异常抛回该线程（厂商回调线程上抛异常通常直接崩进程）。
/// </para>
/// <para>
/// 停止契约：<see cref="StopContinuousGrab"/> 返回后不得再有新的回调<b>进入</b>；
/// 但已经进入的回调可能仍在执行——等待它们退出是调用方（<c>BaslerStreamSession</c>）的责任。
/// </para>
/// </summary>
internal interface IBaslerStreamCamera : IDisposable
{
    /// <summary>设备是否已打开。</summary>
    bool IsOpen { get; }

    /// <summary>打开设备；已打开时为空操作。失败必须抛出，且不得留下半开状态。</summary>
    void Open();

    /// <summary>关闭并释放设备对象；未打开时为空操作。之后 <see cref="Open"/> 会重新创建一个设备对象。</summary>
    void Close();

    /// <summary>把布防参数写到设备上；为空的分支表示保持设备当前设置。</summary>
    /// <param name="triggerMode">触发模式。</param>
    /// <param name="exposureMicroseconds">曝光，单位微秒；空表示不改写。</param>
    /// <param name="gainDecibels">增益，单位分贝；空表示不改写。</param>
    void ApplyArmParameters(EVisionTriggerMode triggerMode, double? exposureMicroseconds, double? gainDecibels);

    /// <summary>开始持续取流。</summary>
    /// <param name="onFrame">每成功抓取一帧回调一次；所有权随回调进入转移，回调负责释放。</param>
    /// <param name="onFailure">设备报告取流失败（例如断线）时回调一次。</param>
    void StartContinuousGrab(Action<IBaslerGrabFrame> onFrame, Action<Exception> onFailure);

    /// <summary>停止持续取流并摘除回调；返回后不得再有新的回调进入。</summary>
    void StopContinuousGrab();

    /// <summary>
    /// 按请求单次抓取一帧，并把请求参数写到设备上。
    /// <para>
    /// 与 <see cref="StartContinuousGrab"/> 共用同一条取流通道：设备正在持续取流时本调用必须明确失败，
    /// 而不是抢占通道；实现必须在返回前停掉本次抓图，不把残留的取流状态留给下一次布防。
    /// </para>
    /// </summary>
    /// <param name="triggerMode">触发模式。</param>
    /// <param name="exposureMicroseconds">曝光，单位微秒；空表示不改写设备设置。</param>
    /// <param name="gainDecibels">增益，单位分贝；空表示不改写设备设置。</param>
    /// <param name="timeoutMilliseconds">等待一帧的最长时间。</param>
    /// <param name="cancellationToken">协作取消。</param>
    /// <returns>抓到的设备帧；所有权随返回值转给调用方，调用方负责释放。</returns>
    IBaslerGrabFrame CaptureSingleFrame(
        EVisionTriggerMode triggerMode,
        double? exposureMicroseconds,
        double? gainDecibels,
        int timeoutMilliseconds,
        CancellationToken cancellationToken);
}

/// <summary>创建真实相机对象；未装配 pylon 支持时明确失败，而不是静默降级。</summary>
internal static class BaslerStreamCameras
{
    /// <summary>创建绑定对应的相机对象（尚未打开，两种采集模式共用它）。</summary>
    /// <param name="binding">Provider 私有绑定。</param>
    /// <returns>尚未打开的设备对象。</returns>
    /// <exception cref="VisionProviderUnavailableException">本程序集未装配 pylon 支持。</exception>
    public static IBaslerStreamCamera Create(BaslerAcquisitionBinding binding)
    {
        if (binding is null)
            throw new ArgumentNullException(nameof(binding));
#if BASLER_SDK
        return new PylonStreamCamera(binding);
#else
        throw new VisionProviderUnavailableException(
            BaslerAcquisitionProvider.ProviderIdentity,
            "本程序集未装配 Basler pylon 支持（BASLER_SDK 未定义）；请以默认配置重新构建 DP.Vision.Basler。");
#endif
    }
}
