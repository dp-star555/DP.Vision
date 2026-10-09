using System;
using System.Collections.Generic;
using System.Globalization;

namespace DP.Vision.Algorithms;

/// <summary>独立字形标签与水平单行身份的Unicode约束；不猜字、不排序、不归一化。</summary>
public static class CharacterIdentity
{
    /// <summary>ASCII字母或数字判断，保留原有语义，不代表完整字形支持范围。</summary>
    /// <param name = "c">待判断的UTF-16代码单元。</param>
    /// <returns>字符属于A–Z、a–z或0–9时为true。</returns>
    public static bool IsAlphanumeric(char c)
    {
        return c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9';
    }

    /// <summary>判断标签是否为一个可见Unicode标量：字母、数字、标点或符号；空白、控制、组合标记和孤立代理项不是独立参考。</summary>
    /// <param name = "label">保持原始编码和大小写的单字标签，包括补充平面汉字。</param>
    /// <returns>可作为独立字形身份时为true。</returns>
    public static bool IsGlyph(string? label)
    {
        if (string.IsNullOrEmpty(label) || ScalarLength(label!, 0) != label!.Length)
        {
            return false;
        }

        switch (CharUnicodeInfo.GetUnicodeCategory(label, 0))
        {
            case UnicodeCategory.UppercaseLetter:
            case UnicodeCategory.LowercaseLetter:
            case UnicodeCategory.TitlecaseLetter:
            case UnicodeCategory.ModifierLetter:
            case UnicodeCategory.OtherLetter:
            case UnicodeCategory.DecimalDigitNumber:
            case UnicodeCategory.LetterNumber:
            case UnicodeCategory.OtherNumber:
            case UnicodeCategory.ConnectorPunctuation:
            case UnicodeCategory.DashPunctuation:
            case UnicodeCategory.OpenPunctuation:
            case UnicodeCategory.ClosePunctuation:
            case UnicodeCategory.InitialQuotePunctuation:
            case UnicodeCategory.FinalQuotePunctuation:
            case UnicodeCategory.OtherPunctuation:
            case UnicodeCategory.MathSymbol:
            case UnicodeCategory.CurrencySymbol:
            case UnicodeCategory.ModifierSymbol:
            case UnicodeCategory.OtherSymbol:
                return true;
            default:
                return false;
        }
    }

    /// <summary>将水平单行拆为非空白字形身份；只忽略Unicode空格分隔符，拒绝换行、格式控制、未配对代理项与组合序列。</summary>
    /// <param name = "text">原始身份假设，不进行全半角、大小写或组合字符归一化。</param>
    /// <param name = "glyphs">按原顺序返回单标量标签；失败时为空，不返回半截结果。</param>
    /// <param name = "maximumCharacters">包括空格的Unicode标量数上限，默认128。</param>
    /// <returns>输入有效且包含至少一个字形时为true。</returns>
    public static bool TryTokenizeLine(string? text, out string[] glyphs, int maximumCharacters = 128)
    {
        if (maximumCharacters < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        }

        glyphs = Array.Empty<string>();
        if (string.IsNullOrEmpty(text) || text!.Length > 2L * maximumCharacters)
        {
            return false;
        }

        var labels = new List<string>();
        int count = 0;
        for (int i = 0; i < text.Length; )
        {
            int length = ScalarLength(text, i);
            if (length == 0 || ++count > maximumCharacters)
            {
                return false;
            }

            string label = text.Substring(i, length);
            if (CharUnicodeInfo.GetUnicodeCategory(text, i) != UnicodeCategory.SpaceSeparator)
            {
                if (!IsGlyph(label))
                {
                    return false;
                }

                labels.Add(label);
            }

            i += length;
        }

        if (labels.Count == 0)
        {
            return false;
        }

        glyphs = labels.ToArray();
        return true;
    }

    private static int ScalarLength(string value, int index)
    {
        char c = value[index];
        if (!char.IsSurrogate(c))
        {
            return 1;
        }

        return char.IsHighSurrogate(c) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])
            ? 2
            : 0;
    }
}
