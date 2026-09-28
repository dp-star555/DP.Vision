using System;

namespace DP.Vision.Algorithms;

/// <summary>受限平移配准（ECC）的收敛与可信条件。</summary>
public sealed class TranslationRegistrationOptions
{
    /// <summary>创建配准参数。</summary>
    /// <param name = "minimumScore">ECC相关系数下限（0–1），低于时不采用。</param>
    /// <param name = "maximumShift">允许的最大平移（像素，每个方向），超过时不采用。</param>
    /// <param name = "iterations">ECC最大迭代次数。</param>
    /// <param name = "epsilon">ECC收敛阈值。</param>
    /// <param name = "minimumContrast">参考图掩码内灰度标准差下限，低于时纹理不足、不配准。</param>
    /// <param name = "minimumPixels">掩码内最少像素数。</param>
    public TranslationRegistrationOptions(
        double minimumScore = .75,
        double maximumShift = 12,
        int iterations = 80,
        double epsilon = .00001,
        double minimumContrast = 10,
        int minimumPixels = 200
    )
    {
        if (double.IsNaN(minimumScore) || minimumScore < 0 || minimumScore > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumScore));
        }

        if (
            !(maximumShift >= 0)
            || iterations < 1
            || !(epsilon > 0)
            || !(minimumContrast >= 0)
            || minimumPixels < 1
        )
        {
            throw new ArgumentOutOfRangeException(nameof(maximumShift));
        }

        MinimumScore = minimumScore;
        MaximumShift = maximumShift;
        Iterations = iterations;
        Epsilon = epsilon;
        MinimumContrast = minimumContrast;
        MinimumPixels = minimumPixels;
    }

    /// <summary>ECC相关系数下限。</summary>
    public double MinimumScore { get; }

    /// <summary>允许的最大平移（像素）。</summary>
    public double MaximumShift { get; }

    /// <summary>ECC最大迭代次数。</summary>
    public int Iterations { get; }

    /// <summary>ECC收敛阈值。</summary>
    public double Epsilon { get; }

    /// <summary>参考图掩码内灰度标准差下限。</summary>
    public double MinimumContrast { get; }

    /// <summary>掩码内最少像素数。</summary>
    public int MinimumPixels { get; }
}
