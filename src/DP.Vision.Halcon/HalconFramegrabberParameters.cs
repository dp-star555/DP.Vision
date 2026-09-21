using System;
using System.Collections.Generic;
using System.Globalization;
using DP.Vision.Acquisition;

namespace DP.Vision.Halcon;

/// <summary>要写到 HALCON 采集设备上的一个参数。</summary>
/// <param name="Name">HALCON 参数名，例如 <c>ExposureTime</c>。</param>
/// <param name="Value">
/// 参数取值；只使用 <see cref="string"/>、<see cref="int"/> 与 <see cref="double"/>。
/// 字符串参数原样写入，整数与浮点走 HALCON 的数值重载——不依赖 HALCON 把文本解析成数值，
/// 也就不会把宿主区域设置（小数点符号）带进设备参数。
/// </param>
internal readonly record struct HalconDeviceParameter(string Name, object Value)
{
    /// <summary>诊断与错误消息里使用的取值文本。</summary>
    public string DisplayValue => Value is null
        ? "null"
        : Convert.ToString(Value, CultureInfo.InvariantCulture) ?? "null";
}

/// <summary>
/// 公共采集参数到 HALCON 设备参数的**唯一**决策点：主动单次采集与外部回调布防都走这里。
/// <para>
/// 之所以只留一条路径：触发、曝光与增益的单位语义一旦各写一份，两条采集路径就会慢慢漂移，
/// 而漂移出来的差异只会在现场以"设置了但不生效"或"采集到的不是触发那一刻的图"的形式暴露。
/// 这里只做**决策**，不接触 <c>HFramegrabber</c>，因此全部语义都能在没有相机、没有许可证的机器上被验证。
/// </para>
/// <para>
/// 单位语义是契约的一部分，不允许用厂商原始刻度冒充：
/// <list type="bullet">
/// <item>曝光写 <c>ExposureTime</c>，单位**微秒**（GenICam SFNC）。</item>
/// <item>增益写 <c>Gain</c>，单位**分贝**（GenICam SFNC）。</item>
/// <item>写手动值之前先关闭对应的自动算法（<c>ExposureAuto</c>/<c>GainAuto</c>），
/// 否则手动值会被自动算法覆盖，操作员看到的是"设置了但不生效"。</item>
/// <item>为空的物理量表示**保持设备当前设置**（一个参数都不写）；
/// 显式给出的 0 必须真的写到设备上，不能因为"零等于没给"而静默丢弃。</item>
/// </list>
/// </para>
/// </summary>
internal static class HalconFramegrabberParameters
{
    /// <summary>抓取等待上限参数；它同时是长连接停止等待的上界。</summary>
    public const string GrabTimeout = "grab_timeout";

    /// <summary>尽力中止当前阻塞抓取的参数；是否被支持取决于采集接口。</summary>
    public const string AbortGrab = "do_abort_grab";

    /// <summary>曝光参数名；GenICam SFNC 规定单位为微秒。</summary>
    public const string ExposureTime = "ExposureTime";

    /// <summary>自动曝光开关；写手动曝光之前必须先关掉它。</summary>
    public const string ExposureAuto = "ExposureAuto";

    /// <summary>增益参数名；GenICam SFNC 规定单位为分贝。</summary>
    public const string Gain = "Gain";

    /// <summary>自动增益开关；写手动增益之前必须先关掉它。</summary>
    public const string GainAuto = "GainAuto";

    /// <summary>HALCON 通用触发参数，取值 <c>'default'</c>、<c>'false'</c>、<c>'true'</c>。</summary>
    public const string ExternalTrigger = "external_trigger";

    /// <summary>MVTec 采集参数：触发方式；<c>'Software'</c> 表示软触发。</summary>
    public const string ConsumerTrigger = "[Consumer]trigger";

    /// <summary>MVTec 采集参数：发一次软触发。</summary>
    public const string ConsumerTriggerSoftware = "[Consumer]trigger_software";

    /// <summary>采集模式参数；软触发要求连续采集模式。</summary>
    public const string AcquisitionMode = "AcquisitionMode";

    /// <summary>GenICam 触发选择器参数名。</summary>
    public const string TriggerSelector = "TriggerSelector";

    /// <summary>GenICam 触发源参数名。</summary>
    public const string TriggerSource = "TriggerSource";

    /// <summary>GenICam 触发模式参数名。</summary>
    public const string TriggerMode = "TriggerMode";

    /// <summary>每次抓取前是否要先发一条软触发命令。</summary>
    /// <param name="mode">触发模式。</param>
    /// <returns>需要先发软触发命令时返回 <see langword="true"/>。</returns>
    public static bool RequiresSoftwareTriggerCommand(EVisionTriggerMode mode) =>
        mode == EVisionTriggerMode.Software;

    /// <summary>把公共触发模式翻译成需要写到设备上的触发参数。</summary>
    /// <param name="mode">触发模式。</param>
    /// <param name="triggerSource">外部触发的触发源；只在外部触发模式下使用。</param>
    /// <returns>按顺序写入的参数；<see cref="EVisionTriggerMode.KeepCurrent"/> 返回空序列。</returns>
    /// <exception cref="VisionSourceConfigurationException">外部触发没有声明触发源。</exception>
    /// <exception cref="VisionParameterNotSupportedException">该模式无法用 HALCON 参数表达。</exception>
    /// <remarks>
    /// <see cref="EVisionTriggerMode.KeepCurrent"/> 返回空序列而不是 <c>'default'</c>：
    /// 在已经打开的共享句柄上，"保持当前设置"只能表现为**一个触发参数都不写**；
    /// 写成 <c>'false'</c> 会在每次采集时显式关闭外部触发，把硬件触发的相机顺手改成自由运行。
    /// <para>
    /// <see cref="EVisionTriggerMode.Software"/> 按 MVTec 官方示例
    /// <c>genicamtl_software_trigger.hdev</c> 的写法配置：触发方式取 <c>'Software'</c>，
    /// 并把采集模式设为连续——官方说明"无论哪种触发模式，连续采集都是抓图所必需的"
    /// （<c>Regardless of the trigger mode continuous acquisition is required</c>）。
    /// 每帧的触发命令由 <see cref="ConsumerTriggerSoftware"/> 在抓取前单独发出，
    /// 因此这里不写 <see cref="ExternalTrigger"/>，避免与触发方式参数互相覆盖。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<HalconDeviceParameter> ResolveTrigger(
        EVisionTriggerMode mode,
        string? triggerSource)
    {
        switch (mode)
        {
            case EVisionTriggerMode.KeepCurrent:
                return Array.Empty<HalconDeviceParameter>();

            case EVisionTriggerMode.FreeRun:
                return new[] { new HalconDeviceParameter(ExternalTrigger, "false") };

            case EVisionTriggerMode.Software:
                return new[]
                {
                    new HalconDeviceParameter(ConsumerTrigger, "Software"),
                    new HalconDeviceParameter(AcquisitionMode, "Continuous")
                };

            case EVisionTriggerMode.External:
                if (string.IsNullOrWhiteSpace(triggerSource))
                {
                    throw new VisionSourceConfigurationException(
                        "HALCON 外部触发必须由Provider私有配置显式声明触发源（triggerSource，例如 Line1）；"
                        + "不猜物理接线，也不静默退回自由运行。");
                }

                // GenICam SFNC 标准节点名。设备不支持这些节点时会明确失败（H_ERR_FGPARAM 等），
                // 这正是想要的：写不进触发配置比"以为在等触发其实在自由运行"安全得多。
                return new[]
                {
                    new HalconDeviceParameter(ExternalTrigger, "true"),
                    new HalconDeviceParameter(TriggerSelector, "FrameStart"),
                    new HalconDeviceParameter(TriggerSource, triggerSource!.Trim()),
                    new HalconDeviceParameter(TriggerMode, "On")
                };

            default:
                throw new VisionParameterNotSupportedException(
                    $"HALCON Adapter 不支持触发模式 {mode}；请在Provider私有配置中设置触发源。");
        }
    }

    /// <summary>发一次软触发；只在 <see cref="RequiresSoftwareTriggerCommand"/> 为真时使用。</summary>
    /// <returns>要写入的单个参数。</returns>
    public static HalconDeviceParameter SoftwareTriggerCommand() =>
        new HalconDeviceParameter(ConsumerTriggerSoftware, 1);

    /// <summary>把请求里的曝光与增益翻译成需要写到设备上的参数。</summary>
    /// <param name="exposureMicroseconds">曝光，单位微秒；空表示保持设备当前设置。</param>
    /// <param name="gainDecibels">增益，单位分贝；空表示保持设备当前设置。</param>
    /// <returns>按顺序写入的参数。</returns>
    /// <remarks>
    /// 关闭自动算法与写手动值必须成对出现：只写手动值而设备仍处于自动模式时，
    /// 数值会被自动算法立刻覆盖，而现场看到的现象只是"这张图还是那么亮"。
    /// </remarks>
    public static IReadOnlyList<HalconDeviceParameter> ResolveCaptureParameters(
        double? exposureMicroseconds,
        double? gainDecibels)
    {
        var parameters = new List<HalconDeviceParameter>(4);
        if (exposureMicroseconds is { } exposure)
        {
            parameters.Add(new HalconDeviceParameter(ExposureAuto, "Off"));
            parameters.Add(new HalconDeviceParameter(ExposureTime, exposure));
        }

        if (gainDecibels is { } gain)
        {
            parameters.Add(new HalconDeviceParameter(GainAuto, "Off"));
            parameters.Add(new HalconDeviceParameter(Gain, gain));
        }

        return parameters;
    }
}