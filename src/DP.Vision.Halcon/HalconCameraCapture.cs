using System;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Acquisition;
using DP.Vision.Algorithms;
#if HALCON_SDK
using HalconDotNet;
#endif

namespace DP.Vision.Halcon;

/// <summary>直接基于HALCON SDK的中立相机采集实现，不经过旧视觉框架。CameraId为“接口名|设备名”。</summary>
public sealed class HalconCameraCapture : ICameraCapture
{
    private readonly int _grabTimeoutMilliseconds;
    /// <summary>创建每次采集独立打开/关闭设备的读取器，不共享可变设备句柄。</summary>
    /// <param name="grabTimeoutMilliseconds">设备采集超时；采集接口必须支持grab_timeout，不能无限等触发。</param>
    public HalconCameraCapture(int grabTimeoutMilliseconds = 5000)
    {
        if (grabTimeoutMilliseconds < 1) throw new ArgumentOutOfRangeException(nameof(grabTimeoutMilliseconds));
        _grabTimeoutMilliseconds = grabTimeoutMilliseconds;
    }

    /// <summary>当前程序集是否包含SDK实现；不是设备在线或许可证通过的证明。</summary>
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

    /// <inheritdoc/>
    public Task<IImageSource> CaptureAsync(string cameraId, CameraCaptureOptions options, CancellationToken token = default) =>
        CaptureWithTriggerAsync(
            cameraId,
            options,
            options != null && options.Triggered ? EVisionTriggerMode.External : EVisionTriggerMode.FreeRun,
            token);

    /// <summary>
    /// 按公共触发模式采集一帧。
    /// <para>
    /// 与只接受布尔值的重载相比，这里能区分"保持设备当前触发设置"与"自由运行"：
    /// 布尔值只有两态，把 <see cref="EVisionTriggerMode.KeepCurrent"/> 归到"非外部触发"一侧，
    /// 会在打开参数里显式写下 <c>'false'</c>，把一台硬件触发的相机顺手改成自由运行。
    /// </para>
    /// <para>
    /// 刻意不复用 <c>CaptureAsync</c> 这个名字：多出一个第三参数为重载会让
    /// <c>CaptureAsync(id, options, default)</c> 这类写法变成歧义调用。
    /// </para>
    /// </summary>
    /// <param name="cameraId">设备键，格式为“接口名|设备名”。</param>
    /// <param name="options">曝光与增益参数。</param>
    /// <param name="triggerMode">触发模式；<see cref="EVisionTriggerMode.KeepCurrent"/> 不写任何触发参数。</param>
    /// <param name="token">取消令牌。</param>
    /// <returns>调用者必须释放的统一图像源。</returns>
    /// <exception cref="VisionParameterNotSupportedException">该触发模式无法用HALCON打开参数表达。</exception>
    public Task<IImageSource> CaptureWithTriggerAsync(
        string cameraId,
        CameraCaptureOptions options,
        EVisionTriggerMode triggerMode,
        CancellationToken token = default)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        var parts = (cameraId ?? string.Empty).Split(new[] { '|' }, 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            throw new ArgumentException("CameraId must be interface|device.", nameof(cameraId));
        token.ThrowIfCancellationRequested();

        // 触发值在进入后台线程之前解析：不受支持的模式必须同步明确拒绝，而不是变成一个稍后失败的 Task。
        var triggerValue = MapTriggerValue(triggerMode);
#if HALCON_SDK
        // SDK阻塞操作隔离到后台，取消在调用边界检查，真实等待受设备grab_timeout约束。
        return Task.Run<IImageSource>(() =>
        {
            token.ThrowIfCancellationRequested();
            using var camera = new HFramegrabber(parts[0], 1, 1, 0, 0, 0, 0,
                "default", -1, "default", -1, triggerValue, "default", parts[1], 0, -1);
            SetIntegerParam(camera, "grab_timeout", _grabTimeoutMilliseconds);
            if (options.Exposure > 0) SetRealParam(camera, "ExposureTime", options.Exposure);
            if (options.Gain > 0) SetRealParam(camera, "Gain", options.Gain);
            token.ThrowIfCancellationRequested();
            using var image = camera.GrabImageAsync(-1);
            token.ThrowIfCancellationRequested();
            return HalconImageSource.CopyFrom(image, token);
        }, token);
#else
        throw new PlatformNotSupportedException("Build DP.Vision.Halcon with HALCONROOT or HalconDotNetPath and deploy the HALCON runtime.");
#endif
    }

    /// <summary>把公共触发模式映射为HALCON打开参数的取值。</summary>
    /// <param name="mode">触发模式。</param>
    /// <returns><c>'default'</c>、<c>'false'</c> 或 <c>'true'</c>。</returns>
    /// <exception cref="VisionParameterNotSupportedException">该模式无法用打开参数表达。</exception>
    /// <remarks>
    /// <see cref="EVisionTriggerMode.KeepCurrent"/> 对应 <c>'default'</c>（接口默认值，即不改动设备设置），
    /// 而不是 <c>'false'</c>；后者会把硬件触发的相机改成自由运行。
    /// 本方法对测试程序集开放，就是为了让这条语义有可执行的回归，而不是只写在注释里。
    /// </remarks>
    internal static string MapTriggerValue(EVisionTriggerMode mode)
    {
        switch (mode)
        {
            case EVisionTriggerMode.KeepCurrent:
                return "default";
            case EVisionTriggerMode.FreeRun:
                return "false";
            case EVisionTriggerMode.External:
                return "true";
            default:
                // 无法把软件触发表达为当前 HALCON 打开参数时明确拒绝，而不是静默按自由运行采集。
                throw new VisionParameterNotSupportedException(
                    $"HALCON Adapter 不支持触发模式 {mode}；请在Provider私有配置中设置触发源。");
        }
    }

#if HALCON_SDK
    // 必须把两个实参都显式写成 HTuple：HTuple 与 string 之间存在双向隐式转换，
    // 只写 (string, HTuple) 会让 (string, string) 与 (HTuple, HTuple) 两个重载同时可用而产生二义性。
    private static void SetIntegerParam(HFramegrabber camera, string name, int value)
    {
        using var nameTuple = (HTuple)name;
        using var valueTuple = (HTuple)value;
        camera.SetFramegrabberParam(nameTuple, valueTuple);
    }

    private static void SetRealParam(HFramegrabber camera, string name, double value)
    {
        using var nameTuple = (HTuple)name;
        using var valueTuple = (HTuple)value;
        camera.SetFramegrabberParam(nameTuple, valueTuple);
    }
#endif
}
