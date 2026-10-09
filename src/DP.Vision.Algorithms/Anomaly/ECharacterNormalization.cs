namespace DP.Vision.Algorithms;

/// <summary>字符模型的行归一化依据，训练与检测必须使用同一方式，历史模型默认为墨迹行几何。</summary>
public enum ECharacterNormalization
{
    /// <summary>历史方式：从整行字母/数字墨迹测量字高与基线，不按单字墨迹框缩放。</summary>
    LineInk = 0,

    /// <summary>稳定制作行ROI：统一缩放整行范围，保持中文/标点相对位置，不因缺笔改变归一化比例。</summary>
    LineRegion = 1,
}
