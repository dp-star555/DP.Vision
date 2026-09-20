using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.Algorithms;

/// <summary>实测QR网格几何，不是重新编码或纠错后的标准码图。</summary>
public sealed class BarcodeModuleGrid
{
    /// <summary>复制QR网格：四角按左上、右上、右下、左下给出8个原图坐标值，模块位来自检测器实测。</summary>
    /// <param name = "dimension">QR每边的模块数。</param>
    /// <param name = "corners">8个原图像素边缘坐标，按左上、右上、右下、左下的X/Y排列；集合会复制。</param>
    /// <param name = "sampledModules">检测器实测模块位，按行排列，长度为dimension的平方；不是纠错真值。</param>
    public BarcodeModuleGrid(int dimension, IEnumerable<double> corners, IEnumerable<bool> sampledModules)
    {
        if (dimension < 21 || dimension > 177 || (dimension - 21) % 4 != 0)
        {
            throw new ArgumentException("Invalid QR dimension.");
        }

        var points = corners?.ToArray() ?? throw new ArgumentNullException(nameof(corners));
        var bits = sampledModules?.ToArray() ?? throw new ArgumentNullException(nameof(sampledModules));
        if (
            points.Length != 8
            || points.Any(v => double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v) > 24000)
            || bits.Length != dimension * dimension
        )
        {
            throw new ArgumentException("Invalid module geometry.");
        }

        Dimension = dimension;
        Corners = Array.AsReadOnly(points);
        SampledModules = Array.AsReadOnly(bits);
    }

    /// <summary>QR每边的模块数。</summary>
    public int Dimension { get; }

    /// <summary>原图中的左上、右上、右下、左下角坐标；裁切或病态几何可能被拒绝。</summary>
    public IReadOnlyList<double> Corners { get; }

    /// <summary>解码和纠错前检测到的网格；这些位不是独立的印刷真值。</summary>
    public IReadOnlyList<bool> SampledModules { get; }
}
