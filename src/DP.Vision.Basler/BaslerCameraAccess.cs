#if BASLER_SDK
using System;
using System.Collections.Generic;
using Basler.Pylon;
using DP.Vision.Acquisition;

namespace DP.Vision.Basler;

/// <summary>按 Provider 私有绑定唯一解析目标相机；OnDemand 与长连接两条路径共用同一套选择规则。</summary>
internal static class BaslerCameraSelection
{
    /// <summary>解析绑定唯一对应的相机；匹配不到或多于一台都明确失败，不回退到"第一台"。</summary>
    /// <param name="binding">Provider 私有绑定。</param>
    /// <returns>唯一匹配的相机信息。</returns>
    /// <exception cref="VisionDeviceOfflineException">没有匹配的设备。</exception>
    /// <exception cref="VisionSourceConfigurationException">匹配到多于一台设备，配置无法确定目标。</exception>
    public static ICameraInfo Resolve(BaslerAcquisitionBinding binding)
    {
        if (binding is null)
            throw new ArgumentNullException(nameof(binding));

        // 选择键使用 pylon 自己的常量，避免依赖字符串字面量与 SDK 定义恰好一致。
        var key = binding.SerialNumber is not null ? CameraInfoKey.SerialNumber : CameraInfoKey.UserDefinedName;
        var expected = binding.SelectorValue;

        var matches = new List<ICameraInfo>();
        foreach (var info in CameraFinder.Enumerate())
        {
            if (info.ContainsKey(key) && string.Equals(info[key], expected, StringComparison.Ordinal))
                matches.Add(info);
        }

        if (matches.Count == 1)
            return matches[0];
        if (matches.Count == 0)
        {
            throw new VisionDeviceOfflineException(
                $"Basler 设备 {binding.BindingId} 未找到：没有相机满足 {key}={expected}。"
                + "请确认设备已上电联网，或修正Provider私有配置中的绑定。");
        }

        throw new VisionSourceConfigurationException(
            $"Basler 设备 {binding.BindingId} 的 {key}={expected} 匹配到 {matches.Count} 台相机；"
            + "绑定必须唯一确定一台设备，请改用序列号区分。");
    }
}

/// <summary>
/// 把公共采集参数写到设备上。OnDemand 单次采集与长连接布防共用这一条路径——
/// 两处各写一份会让"先关自动曝光再写手动值"这类顺序要求各自漂移。
/// </summary>
internal static class BaslerCameraParameters
{
    /// <summary>应用一次采集或布防参数；为空的分支表示保持设备当前设置。</summary>
    /// <param name="camera">已打开的相机。</param>
    /// <param name="binding">Provider 私有绑定；外部触发需要它声明的触发源。</param>
    /// <param name="triggerMode">触发模式。</param>
    /// <param name="exposureMicroseconds">曝光，单位微秒；空表示不改写。</param>
    /// <param name="gainDecibels">增益，单位分贝；空表示不改写。</param>
    /// <exception cref="VisionParameterNotSupportedException">设备不接受给定数值或触发模式无法表达。</exception>
    public static void Apply(
        Camera camera,
        BaslerAcquisitionBinding binding,
        EVisionTriggerMode triggerMode,
        double? exposureMicroseconds,
        double? gainDecibels)
    {
        if (camera is null)
            throw new ArgumentNullException(nameof(camera));
        if (binding is null)
            throw new ArgumentNullException(nameof(binding));

        // 像素格式决定设备实际输出什么，也决定中立帧那侧的转换路径；
        // 必须在开始取流之前写好，否则相机仍按上一次的格式出图。
        if (binding.PixelFormat is { } pixelFormat)
            SetEnum(camera, PLCamera.PixelFormat, pixelFormat, "像素格式");

        if (exposureMicroseconds is { } exposure)
        {
            // 先关自动曝光：否则手动值会被自动算法覆盖，操作员看到的是"设置了但不生效"。
            camera.Parameters[PLCamera.ExposureAuto].TrySetValue(PLCamera.ExposureAuto.Off);
            SetFloat(camera, PLCamera.ExposureTime, exposure, "曝光");
        }

        if (gainDecibels is { } gain)
        {
            camera.Parameters[PLCamera.GainAuto].TrySetValue(PLCamera.GainAuto.Off);
            SetFloat(camera, PLCamera.Gain, gain, "增益");
        }

        ApplyTrigger(camera, binding, triggerMode);
    }

    private static void SetFloat(Camera camera, FloatName name, double value, string label)
    {
        if (!camera.Parameters[name].TrySetValue(value))
        {
            throw new VisionParameterNotSupportedException(
                $"Basler 设备不接受{label} {value}；该值超出设备允许范围或该参数当前不可写。");
        }
    }

    private static void SetEnum(Camera camera, EnumName name, string value, string label)
    {
        if (!camera.Parameters[name].TrySetValue(value))
        {
            throw new VisionParameterNotSupportedException(
                $"Basler 设备不接受{label} {value}；该参数当前不可写或设备不支持该取值。");
        }
    }

    /// <summary>把公共触发模式写到设备上；无法表达的模式明确拒绝，而不是静默按自由运行采集。</summary>
    /// <param name="camera">已打开的相机。</param>
    /// <param name="binding">Provider 私有绑定。</param>
    /// <param name="mode">公共触发模式。</param>
    /// <exception cref="VisionParameterNotSupportedException">外部触发未声明触发源，或模式未知。</exception>
    private static void ApplyTrigger(Camera camera, BaslerAcquisitionBinding binding, EVisionTriggerMode mode)
    {
        switch (mode)
        {
            case EVisionTriggerMode.KeepCurrent:
                // 保持设备当前设置：不写任何触发参数。
                return;

            case EVisionTriggerMode.FreeRun:
                SetEnum(camera, PLCamera.TriggerSelector, PLCamera.TriggerSelector.FrameStart, "触发选择器");
                SetEnum(camera, PLCamera.TriggerMode, PLCamera.TriggerMode.Off, "触发模式");
                return;

            case EVisionTriggerMode.Software:
                SetEnum(camera, PLCamera.TriggerSelector, PLCamera.TriggerSelector.FrameStart, "触发选择器");
                SetEnum(camera, PLCamera.TriggerSource, PLCamera.TriggerSource.Software, "触发源");
                SetEnum(camera, PLCamera.TriggerMode, PLCamera.TriggerMode.On, "触发模式");
                return;

            case EVisionTriggerMode.External:
                var source = binding.TriggerSource ?? throw new VisionParameterNotSupportedException(
                    $"Basler 绑定 {binding.BindingId} 使用外部触发，但Provider私有配置没有声明 triggerSource"
                    + "（例如 \"triggerSource\": \"Line1\"）。为避免猜错物理接线，这里明确拒绝而不是沿用设备当前设置。");
                SetEnum(camera, PLCamera.TriggerSelector, PLCamera.TriggerSelector.FrameStart, "触发选择器");
                SetEnum(camera, PLCamera.TriggerSource, source, "触发源");
                SetEnum(camera, PLCamera.TriggerMode, PLCamera.TriggerMode.On, "触发模式");
                return;

            default:
                throw new VisionParameterNotSupportedException($"Basler Adapter 不支持触发模式 {mode}。");
        }
    }
}
#endif
