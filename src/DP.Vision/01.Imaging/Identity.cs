namespace DP.Vision;

/// <summary>帧、视图、图层及显示项共用的文本身份规则：非空白且不超过256个字符。各调用方保留自己的异常与消息。</summary>
internal static class Identity
{
    /// <summary>身份文本的最大长度。</summary>
    internal const int MaxLength = 256;

    /// <summary>检查身份文本是否非空白且不超长。</summary>
    /// <param name="text">待检查的文本。</param>
    /// <returns>是否满足身份规则。</returns>
    internal static bool IsValid(string? text)
    {
        return !string.IsNullOrWhiteSpace(text) && text!.Length <= MaxLength;
    }
}
