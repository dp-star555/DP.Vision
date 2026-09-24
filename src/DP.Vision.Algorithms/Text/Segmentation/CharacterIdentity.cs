namespace DP.Vision.Algorithms;

/// <summary>初始物理分割器支持的ASCII身份约束，不代表通用OCR字母表。</summary>
public static class CharacterIdentity
{
    /// <summary>此分割器是否支持指定的独立外观字符标签。</summary>
    /// <param name = "c">待判断是否属于ASCII字母或数字的字符。</param>
    public static bool IsAlphanumeric(char c)
    {
        return c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9';
    }
}
