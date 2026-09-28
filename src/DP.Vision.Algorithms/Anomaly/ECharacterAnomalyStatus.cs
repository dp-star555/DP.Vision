namespace DP.Vision.Algorithms;

/// <summary>逐字符异常检测中一个字符的检测状态。</summary>
public enum ECharacterAnomalyStatus
{
    /// <summary>已与字符模型比较。</summary>
    Compared,

    /// <summary>没有该字符的模型，无法判断。</summary>
    MissingModel,

    /// <summary>无法测量所在行的字高与基线。</summary>
    Unmeasurable,

    /// <summary>模型与输入不匹配或检测实现未完成。</summary>
    Blocked,
}
