namespace DP.Vision.Algorithms;

/// <summary>视觉几何测量空间；局部单位由稳定坐标定义决定。</summary>
public enum EVisionCoordinateSpace
{
    /// <summary>本帧原图像素边界坐标。</summary>
    Image = 0,
    /// <summary>所选业务坐标定义中的局部表达。</summary>
    Local = 1,
    /// <summary>旧模板局部配置名称，数值与Local相同。</summary>
    TemplateLocal = Local
}
