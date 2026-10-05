using System.Collections.Generic;

namespace DP.Vision.Algorithms;

/// <summary>几何事实的通用原图预览，不依赖节点包或具体引擎类型。</summary>
public interface IVisionGeometryFact
{
    /// <summary>原图身份。</summary>
    string FrameId { get; }
    /// <summary>原图坐标下的有界几何显示项。</summary>
    IReadOnlyList<Geometry> DisplayGeometry { get; }
    /// <summary>事实和单位说明。</summary>
    string Summary { get; }
}
