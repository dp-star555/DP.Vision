using System;
using System.Text;

namespace DP.Vision.Acquisition;

/// <summary>采集公共错误基类；不得把设备冲突包装成"未找到图像"或普通False业务结果。</summary>
public class VisionAcquisitionException : Exception
{
    /// <summary>创建带消息的采集错误。</summary>
    /// <param name="message">面向运行监视和操作员的诊断说明。</param>
    public VisionAcquisitionException(string message)
        : base(message)
    {
    }

    /// <summary>创建带消息和内部原因的采集错误。</summary>
    /// <param name="message">面向运行监视和操作员的诊断说明。</param>
    /// <param name="innerException">底层原因。</param>
    public VisionAcquisitionException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Source配置错误；运行准备阶段即可确定性失败。</summary>
public sealed class VisionSourceConfigurationException : VisionAcquisitionException
{
    /// <summary>创建Source配置错误。</summary>
    /// <param name="message">诊断说明。</param>
    public VisionSourceConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>创建带内部原因的Source配置错误。</summary>
    /// <param name="message">诊断说明。</param>
    /// <param name="innerException">底层原因。</param>
    public VisionSourceConfigurationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>资源冲突诊断；必须包含双方身份，否则操作员无法判断谁占用了设备。</summary>
/// <param name="SourceId">请求的逻辑源标识。</param>
/// <param name="ResourceKey">发生冲突的物理资源键。</param>
/// <param name="RequestOwnerId">请求方运行身份。</param>
/// <param name="RequestOperationId">请求方操作身份。</param>
/// <param name="HolderOwnerId">当前占用方运行身份；未知时为空。</param>
/// <param name="HolderOperationId">当前占用方操作身份；未知时为空。</param>
/// <param name="Policy">冲突发生时生效的共享策略。</param>
/// <param name="Reason">等待或失败原因。</param>
public sealed record VisionResourceConflictDiagnostics(
    string SourceId,
    string ResourceKey,
    string RequestOwnerId,
    string RequestOperationId,
    string? HolderOwnerId,
    string? HolderOperationId,
    EVisionSourceSharingPolicy Policy,
    string Reason);

/// <summary>同一物理资源键被其他运行或节点占用；确定性失败并报告占用者。</summary>
public sealed class VisionResourceConflictException : VisionAcquisitionException
{
    /// <summary>创建资源冲突错误。</summary>
    /// <param name="diagnostics">冲突诊断。</param>
    /// <exception cref="ArgumentNullException">诊断为空。</exception>
    public VisionResourceConflictException(VisionResourceConflictDiagnostics diagnostics)
        : base(BuildMessage(diagnostics))
    {
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    /// <summary>冲突诊断。</summary>
    public VisionResourceConflictDiagnostics Diagnostics { get; }

    private static string BuildMessage(VisionResourceConflictDiagnostics? diagnostics)
    {
        if (diagnostics is null)
            return "采集资源冲突。";
        var builder = new StringBuilder();
        builder.Append("采集资源冲突：源").Append(diagnostics.SourceId)
            .Append(" 的资源键 ").Append(diagnostics.ResourceKey)
            .Append(" 当前被占用。请求方 ").Append(diagnostics.RequestOwnerId)
            .Append('/').Append(diagnostics.RequestOperationId)
            .Append("；占用方 ").Append(diagnostics.HolderOwnerId ?? "未知")
            .Append('/').Append(diagnostics.HolderOperationId ?? "未知")
            .Append("；策略 ").Append(diagnostics.Policy)
            .Append("；原因：").Append(diagnostics.Reason);
        return builder.ToString();
    }
}

/// <summary>设备离线；节点故障，不自动切换Provider。</summary>
public sealed class VisionDeviceOfflineException : VisionAcquisitionException
{
    /// <summary>创建设备离线错误。</summary>
    /// <param name="message">诊断说明。</param>
    public VisionDeviceOfflineException(string message)
        : base(message)
    {
    }

    /// <summary>创建带内部原因的设备离线错误。</summary>
    /// <param name="message">诊断说明。</param>
    /// <param name="innerException">底层原因。</param>
    public VisionDeviceOfflineException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>请求的通用参数或触发模式不被该Provider支持；明确拒绝而不是静默忽略。</summary>
public sealed class VisionParameterNotSupportedException : VisionAcquisitionException
{
    /// <summary>创建参数不支持错误。</summary>
    /// <param name="message">诊断说明，应指出哪个参数以及应改在哪里配置。</param>
    public VisionParameterNotSupportedException(string message)
        : base(message)
    {
    }
}

/// <summary>采集超时，例如外部触发未到达。</summary>
public sealed class VisionCaptureTimeoutException : VisionAcquisitionException
{
    /// <summary>创建采集超时错误。</summary>
    /// <param name="message">诊断说明。</param>
    public VisionCaptureTimeoutException(string message)
        : base(message)
    {
    }
}

/// <summary>数据错误，例如空帧、像素格式非法或超出预算；不发布输出。</summary>
public sealed class VisionDataException : VisionAcquisitionException
{
    /// <summary>创建数据错误。</summary>
    /// <param name="message">诊断说明。</param>
    public VisionDataException(string message)
        : base(message)
    {
    }
}

/// <summary>Provider不可用，例如原生依赖缺失、许可证无效或CPU架构不匹配；必须带ProviderId诊断。</summary>
public sealed class VisionProviderUnavailableException : VisionAcquisitionException
{
    /// <summary>创建Provider不可用错误。</summary>
    /// <param name="providerId">出现问题的Provider稳定身份。</param>
    /// <param name="message">诊断说明。</param>
    /// <param name="innerException">底层原因。</param>
    public VisionProviderUnavailableException(string providerId, string message, Exception? innerException = null)
        : base($"[{providerId}] {message}", innerException)
    {
        ProviderId = providerId;
    }

    /// <summary>出现问题的Provider稳定身份。</summary>
    public string ProviderId { get; }
}
