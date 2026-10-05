using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>平移模板定位契约，不支持旋转/尺度搜索，不冒充通用形状定位。</summary>
[VisionCapability("location.template", "定位", "平移模板定位")]
public interface ITemplateLocator
{
    /// <summary>在搜索矩形中匹配模板矩形；范围不可越界，模板不可比搜索区大。</summary>
    /// <param name="frame">搜索帧。</param>
    /// <param name="search">原图搜索范围。</param>
    /// <param name="template">借用模板帧。</param>
    /// <param name="templateBounds">模板原图范围。</param>
    /// <param name="minimumScore">0至1分数阈值。</param>
    /// <param name="token">取消令牌。</param>
    /// <param name="regionMask">候选有效采样足迹须完全包含的精确原图掩码。</param>
    /// <param name="searchCoordinates">固定父姿态，模板在该姿态下仅搜索平移；需使用完整模板。</param>
    /// <returns>最佳位置或明确空检出；模板参考取模板矩形中心。</returns>
    TemplatePoseResult Locate(ImageFrame frame, PixelBounds search, ImageFrame template, PixelBounds templateBounds,
        double minimumScore = .9, CancellationToken token = default, RegionGeometry? regionMask = null, VisionCoordinateSystem? searchCoordinates = null);
}
