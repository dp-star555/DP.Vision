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
    private readonly Dictionary<string, VisionAcquisitionPluginAvailability> _pluginAvailability;

    internal VisionAcquisitionTypeCatalog(
        string catalogId,
        IEnumerable<VisionAcquisitionTypeDescriptor> types,
        IEnumerable<string> manifest,
        IEnumerable<VisionAcquisitionPluginAvailability>? pluginAvailability = null)
    {
        CatalogId = catalogId;
        _types = types.ToDictionary(item => item.AcquisitionTypeId, StringComparer.Ordinal);
        Manifest = manifest.ToArray();
        _pluginAvailability = (pluginAvailability ?? Array.Empty<VisionAcquisitionPluginAvailability>())
            .ToDictionary(item => item.PluginId, StringComparer.Ordinal);
    }

    /// <summary>Catalog内容身份；Type清单变化会产生新的Catalog身份。</summary>
    public string CatalogId { get; }

    /// <summary>Type版本清单，按TypeId排序；可导出用于部署与运行制品。</summary>
    public IReadOnlyList<string> Manifest { get; }

    /// <summary>
    /// 各插件在冻结时刻的可用性，按PluginId排序。缺SDK/缺原生运行时的诊断由此暴露；
    /// 未实现健康报告的Module视为可用，因此这里只列出有Type声明的插件。
    /// </summary>
    public IReadOnlyList<VisionAcquisitionPluginAvailability> PluginAvailability =>
        _pluginAvailability.Values.OrderBy(item => item.PluginId, StringComparer.Ordinal).ToArray();

    /// <summary>按插件身份查询可用性。</summary>
    /// <param name="pluginId">插件稳定身份。</param>
    /// <param name="availability">找到的可用性快照；未声明Type的插件查不到。</param>
    /// <returns>该插件有Type进入本Catalog时返回true。</returns>
    public bool TryGetPluginAvailability(string pluginId, out VisionAcquisitionPluginAvailability? availability)
    {
        if (string.IsNullOrWhiteSpace(pluginId))
        {
            availability = null;
            return false;
        }

        return _pluginAvailability.TryGetValue(pluginId, out availability);
    }

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
