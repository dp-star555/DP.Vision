using System;

namespace DP.Vision.Acquisition;

/// <summary>采集请求的发起方身份；用于资源冲突诊断，不是设备句柄。</summary>
public sealed record VisionAcquisitionOwner
{
    /// <summary>创建发起方身份。</summary>
    /// <param name="ownerId">根运行或上层任务身份。</param>
    /// <param name="operationId">节点执行或采集意图身份。</param>
    /// <param name="displayName">可选的人类可读名称，仅用于诊断。</param>
    /// <exception cref="ArgumentException">身份为空或仅包含空白字符。</exception>
    public VisionAcquisitionOwner(string ownerId, string operationId, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException("发起方身份不能为空。", nameof(ownerId));
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("操作身份不能为空。", nameof(operationId));
        OwnerId = ownerId.Trim();
        OperationId = operationId.Trim();
        DisplayName = displayName is null || string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
    }

    /// <summary>根运行或上层任务身份。</summary>
    public string OwnerId { get; }

    /// <summary>节点执行或采集意图身份。</summary>
    public string OperationId { get; }

    /// <summary>可选的人类可读名称。</summary>
    public string? DisplayName { get; }

    /// <inheritdoc/>
    public override string ToString() => DisplayName is null ? OwnerId : $"{OwnerId}（{DisplayName}）";
}
