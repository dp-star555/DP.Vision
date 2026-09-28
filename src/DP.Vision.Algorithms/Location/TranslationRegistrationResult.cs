namespace DP.Vision.Algorithms;

/// <summary>受限平移配准结果：待检图相对参考图的平移（待检坐标 = 参考坐标 + 平移）；不可信时Found为false，不给出平移。</summary>
public sealed class TranslationRegistrationResult
{
    /// <summary>创建结果。</summary>
    /// <param name = "found">是否取得可信平移。</param>
    /// <param name = "offsetX">横向平移（像素）；未取得时为0。</param>
    /// <param name = "offsetY">纵向平移（像素）；未取得时为0。</param>
    /// <param name = "score">ECC相关系数；未执行或失败时为0。</param>
    /// <param name = "reason">未取得时的原因：low_texture（纹理或像素不足）、not_converged（ECC失败）、low_score、shift_limit。</param>
    public TranslationRegistrationResult(
        bool found,
        double offsetX,
        double offsetY,
        double score,
        string? reason
    )
    {
        Found = found;
        OffsetX = offsetX;
        OffsetY = offsetY;
        Score = score;
        Reason = reason;
    }

    /// <summary>是否取得可信平移。</summary>
    public bool Found { get; }

    /// <summary>横向平移（像素）。</summary>
    public double OffsetX { get; }

    /// <summary>纵向平移（像素）。</summary>
    public double OffsetY { get; }

    /// <summary>ECC相关系数。</summary>
    public double Score { get; }

    /// <summary>未取得时的原因；取得时为null。</summary>
    public string? Reason { get; }

    /// <summary>完成状态，与Found独立。</summary>
    public EAlgorithmStatus Status => EAlgorithmStatus.Completed;
}
