using System;

namespace DP.Vision.Acquisition;

/// <summary>工作流与操作员使用的逻辑视觉源身份；不表达厂商接口名、设备地址或打开方式。</summary>
public sealed record VisionSourceReference
{
    /// <summary>创建非空逻辑源身份。</summary>
    /// <param name="sourceId">机器配置中发布的逻辑源标识。</param>
    /// <exception cref="ArgumentException">标识为空或仅包含空白字符。</exception>
    public VisionSourceReference(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new ArgumentException("逻辑视觉源标识不能为空。", nameof(sourceId));
        SourceId = sourceId.Trim();
    }

    /// <summary>逻辑源标识；由机器配置绑定到唯一Provider与Provider设备绑定。</summary>
    public string SourceId { get; }

    /// <inheritdoc/>
    public override string ToString() => SourceId;
}
