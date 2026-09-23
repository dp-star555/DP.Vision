namespace DP.Vision;

/// <summary>几何；显示简化结果不能替换此原始对象。</summary>
public abstract class Geometry
{
    /// <summary>完整包围原始几何的原图坐标范围。</summary>
    public abstract RectD Bounds { get; }

    /// <summary>判断原图点的几何成员关系。矩形使用半开边界，填充轮廓使用奇偶规则。</summary>
    /// <param name = "point">待判断的原图坐标点。</param>
    /// <param name = "tolerance">非负容差，单位为原图像素；Region仍保持精确像素成员关系，不进行膨胀。</param>
    /// <returns>点是否属于几何或其允许的命中容差范围。</returns>
    public abstract bool Contains(PointD point, double tolerance = 0);
}
