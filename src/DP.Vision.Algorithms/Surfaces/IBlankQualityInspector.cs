using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>可替换的空白表面检查接口，不需要参考图、OCR或业务数据。</summary>
public interface IBlankQualityInspector
{
    /// <summary>检查借用图块，原点将图块边缘映射到原图；可选Gray8掩码必须同尺寸，0排除、255包含。不修改或保留输入；取消及无效参数均抛出异常。</summary>
    /// <param name = "actual">借用的实际图块。</param>
    /// <param name = "origin">图块左上边缘在原图中的坐标，单位为原图像素。</param>
    /// <param name = "options">阈值、原图容差和最小连通面积。</param>
    /// <param name = "allowedMask">同尺寸二值Gray8掩码，0排除、255包含；null表示不额外排除。</param>
    /// <param name = "token">协作式取消标记。</param>
    InkInspectionResult Inspect(
        IImageSource actual,
        PointD origin,
        InkInspectionOptions options,
        IImageSource? allowedMask = null,
        CancellationToken token = default
    );
}
