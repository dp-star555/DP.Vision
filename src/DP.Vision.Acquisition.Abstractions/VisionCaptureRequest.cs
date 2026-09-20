using System;

namespace DP.Vision.Acquisition;

/// <summary>触发模式；KeepCurrent表示不改变设备当前设置。</summary>
public enum EVisionTriggerMode
{
    /// <summary>保持设备当前触发设置。</summary>
    KeepCurrent = 0,

    /// <summary>设备自由运行。</summary>
    FreeRun = 1,

    /// <summary>软件触发。</summary>
    Software = 2,

    /// <summary>外部硬件触发。</summary>
    External = 3
}

/// <summary>一次采集请求；公共物理量必须带明确单位，Provider不得用厂商原始刻度冒充。</summary>
public sealed record VisionCaptureRequest
{
    /// <summary>创建采集请求。</summary>
    /// <param name="timeout">等待采集完成的超时；必须为正值。</param>
    /// <param name="exposureMicroseconds">曝光，单位微秒；空表示保持设备当前设置。</param>
    /// <param name="gainDecibels">增益，单位分贝；空表示保持设备当前设置。</param>
    /// <param name="triggerMode">触发模式。</param>
    /// <exception cref="ArgumentOutOfRangeException">超时不是正值，或物理量为负数、NaN、无穷。</exception>
    public VisionCaptureRequest(
        TimeSpan timeout,
        double? exposureMicroseconds = null,
        double? gainDecibels = null,
        EVisionTriggerMode triggerMode = EVisionTriggerMode.KeepCurrent)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "采集超时必须为正值。");
        ValidatePhysical(exposureMicroseconds, nameof(exposureMicroseconds));
        ValidatePhysical(gainDecibels, nameof(gainDecibels));
        Timeout = timeout;
        ExposureMicroseconds = exposureMicroseconds;
        GainDecibels = gainDecibels;
        TriggerMode = triggerMode;
    }

    /// <summary>等待采集完成的超时。</summary>
    public TimeSpan Timeout { get; }

    /// <summary>曝光，单位微秒；空表示保持设备当前设置。</summary>
    public double? ExposureMicroseconds { get; }

    /// <summary>增益，单位分贝；空表示保持设备当前设置。</summary>
    public double? GainDecibels { get; }

    /// <summary>触发模式。</summary>
    public EVisionTriggerMode TriggerMode { get; }

    private static void ValidatePhysical(double? value, string name)
    {
        if (value is null)
            return;
        if (double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value < 0)
            throw new ArgumentOutOfRangeException(name, value, "公共物理量必须是有限非负值。");
    }
}
