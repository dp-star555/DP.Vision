using System;
using System.Diagnostics;
using DP.Vision.Acquisition;

namespace DP.Vision.Basler;

/// <summary>
/// 把设备帧落地为中立图像的唯一实现。
///
/// OnDemand 单次采集与 BufferedExternal 回调交付共用这一条路径：两条路径若各写一份，
/// "行填充处理"和"尺寸校验"就会各自漂移，而这类偏差只在真实相机上才暴露。
/// 本类只依赖 <see cref="IBaslerGrabFrame"/>，因此不需要相机或 pylon 运行时即可验证。
/// </summary>
internal static class BaslerNeutralFrames
{
    /// <summary>把一帧设备数据复制为中立图像。</summary>
    /// <param name="grab">设备帧；本方法只读取，不负责释放。</param>
    /// <returns>所有权交给调用方的中立图像。</returns>
    /// <exception cref="ArgumentNullException">设备帧为空。</exception>
    /// <exception cref="VisionDataException">像素格式不受支持，或转换结果尺寸与中立布局不一致。</exception>
    public static IImageSource Copy(IBaslerGrabFrame grab) => CopyObserved(grab).Image;

    /// <summary>
    /// 把一帧设备数据复制为中立图像，并同时报告这次落地的耗时与字节数。
    /// <para>
    /// 观测只覆盖真正的像素转换与复制：格式解析和尺寸校验不计时，
    /// 否则监控看到的是"设备帧元数据处理耗时"，而不是现场关心的像素搬运成本。
    /// </para>
    /// </summary>
    /// <param name="grab">设备帧；本方法只读取，不负责释放。</param>
    /// <returns>中立图像与本次像素落地观测。</returns>
    /// <exception cref="ArgumentNullException">设备帧为空。</exception>
    /// <exception cref="VisionDataException">像素格式不受支持，或转换结果尺寸与中立布局不一致。</exception>
    public static (IImageSource Image, VisionPixelTransferObservation Observation) CopyObserved(
        IBaslerGrabFrame grab)
    {
        if (grab is null)
            throw new ArgumentNullException(nameof(grab));

        var conversion = BaslerPixelFormats.Resolve(grab.PixelFormatName);
        var info = new ImageInfo(grab.Width, grab.Height, conversion.Layout);

        // 先问设备侧"这个转换要多少字节"，再与中立布局比对：不一致说明格式映射表有误，
        // 此时直接拒绝发布该帧，而不是让下游按错误的步长解释像素。
        var expected = grab.GetConversionBufferSize(conversion.TargetPixelFormat);
        if (expected != info.ByteLength)
        {
            throw new VisionDataException(
                $"Basler 像素转换结果尺寸与中立布局不一致：转换报告 {expected} 字节，"
                + $"布局 {conversion.Layout} 需要 {info.ByteLength} 字节。这表示格式映射表有误，已拒绝发布该帧。");
        }

        var pixels = new byte[info.ByteLength];
        var stopwatch = Stopwatch.StartNew();
        grab.ConvertInto(pixels, conversion.TargetPixelFormat);
        stopwatch.Stop();
        return (
            VisionImage.CopyFrom(info, pixels),
            new VisionPixelTransferObservation(stopwatch.Elapsed, info.ByteLength));
    }
}
