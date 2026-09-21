using System;
using System.Collections.Generic;
using System.Linq;

namespace DP.Vision.Acquisition;

/// <summary>
/// 已冻结的AcquisitionType Catalog。发布后不再接受任何变更；机器相机配置只查询本Catalog，
/// 因此"列出已安装Type"不读取任何机器配置。
/// </summary>
public sealed class VisionAcquisitionTypeCatalog
{
    private readonly Dictionary<string, VisionAcquisitionTypeDescriptor> _types;

    internal VisionAcquisitionTypeCatalog(
        string catalogId,
        IEnumerable<VisionAcquisitionTypeDescriptor> types,
        IEnumerable<string> manifest)
    {
        CatalogId = catalogId;
        _types = types.ToDictionary(item => item.AcquisitionTypeId, StringComparer.Ordinal);
        Manifest = manifest.ToArray();
    }

    /// <summary>Catalog内容身份；Type清单变化会产生新的Catalog身份。</summary>
    public string CatalogId { get; }

    /// <summary>Type版本清单，按TypeId排序；可导出用于部署与运行制品。</summary>
    public IReadOnlyList<string> Manifest { get; }

    /// <summary>全部已冻结Type，按TypeId排序。</summary>
    public IReadOnlyList<VisionAcquisitionTypeDescriptor> Types =>
        _types.Values.OrderBy(item => item.AcquisitionTypeId, StringComparer.Ordinal).ToArray();

    /// <summary>按Type身份查找描述。</summary>
    /// <param name="acquisitionTypeId">Type稳定身份。</param>
    /// <param name="descriptor">找到的描述；未找到时为空。</param>
    /// <returns>已冻结时返回true。</returns>
    public bool TryGetType(string acquisitionTypeId, out VisionAcquisitionTypeDescriptor? descriptor)
    {
        if (string.IsNullOrWhiteSpace(acquisitionTypeId))
        {
            descriptor = null;
            return false;
        }

        return _types.TryGetValue(acquisitionTypeId, out descriptor);
    }

    /// <summary>按采集形态列出Type，按TypeId排序；Workflow节点Source下拉据此过滤。</summary>
    /// <param name="kind">采集几何形态。</param>
    /// <returns>匹配的Type描述，按TypeId排序。</returns>
    public IReadOnlyList<VisionAcquisitionTypeDescriptor> GetByKind(EVisionAcquisitionKind kind) =>
        _types.Values
            .Where(item => item.Kind == kind)
            .OrderBy(item => item.AcquisitionTypeId, StringComparer.Ordinal)
            .ToArray();
}
