using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DP.Vision.Acquisition;

/// <summary>可选的设备发现能力；不强迫所有SDK伪造支持。</summary>
public interface IVisionDeviceDiscovery
{
    /// <summary>枚举当前可见的候选设备。</summary>
    /// <param name="cancellationToken">协作取消。</param>
    /// <returns>候选设备描述；无候选时为空列表。</returns>
    ValueTask<IReadOnlyList<VisionDeviceDescriptor>> DiscoverAsync(
        CancellationToken cancellationToken);
}

/// <summary>可选的设备健康查询能力；不强迫所有SDK伪造支持。</summary>
public interface IVisionDeviceHealthSource
{
    /// <summary>查询当前健康状态。</summary>
    /// <param name="cancellationToken">协作取消。</param>
    /// <returns>健康快照。</returns>
    ValueTask<VisionDeviceHealth> GetHealthAsync(
        CancellationToken cancellationToken);
}
