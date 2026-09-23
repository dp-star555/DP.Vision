namespace DP.Vision.Algorithms;

/// <summary>可移植的实测异常或完成状态诊断。</summary>
public sealed class QualityFinding
{
    /// <summary>创建不包含厂商类型或标签报告类型的诊断记录。</summary>
    /// <param name = "code">稳定诊断代码。</param>
    /// <param name = "message">面向用户的测量说明，不作为判定依据。</param>
    /// <param name = "kind">信息、阻断或缺陷证据角色。</param>
    /// <param name = "bounds">可选的原图像素范围。</param>
    /// <param name = "areaPixels">可选实测面积，单位为原图平方像素，不是外接框面积。</param>
    public QualityFinding(
        string code,
        string message,
        EQualityFindingKind kind,
        PixelBounds? bounds = null,
        int? areaPixels = null
    )
    {
        Code = code;
        Message = message;
        Kind = kind;
        Bounds = bounds;
        AreaPixels = areaPixels;
    }

    /// <summary>稳定的算法诊断代码。</summary>
    public string Code { get; }

    /// <summary>测量结果及适用条件说明。</summary>
    public string Message { get; }

    /// <summary>证据角色。</summary>
    public EQualityFindingKind Kind { get; }

    /// <summary>已知时提供的原图像素范围。</summary>
    public PixelBounds? Bounds { get; }

    /// <summary>实测原图像素数，不是外接矩形面积。</summary>
    public int? AreaPixels { get; }
}
