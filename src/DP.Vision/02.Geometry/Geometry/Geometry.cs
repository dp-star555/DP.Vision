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

    /// <summary>几何的基本元素数量，用于显示与会话的几何预算：轮廓为顶点数，Region为游程数，矩形与椭圆计为4。</summary>
    public abstract long ElementCount { get; }

    /// <summary>平移几何，不改变尺寸、角度及轮廓开闭与填充属性；Region按整像素平移。</summary>
    /// <param name="dx">X方向偏移，原图像素。</param>
    /// <param name="dy">Y方向偏移，原图像素。</param>
    /// <returns>新的几何对象，原对象不变。</returns>
    public abstract Geometry Translate(double dx, double dy);
}
