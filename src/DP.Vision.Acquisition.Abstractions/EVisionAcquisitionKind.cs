namespace DP.Vision.Acquisition;

/// <summary>
/// AcquisitionType的采集几何形态。机器配置通过AcquisitionTypeId引用具体Type，
/// Workflow节点的Source下拉按本枚举过滤：面阵节点只显示AreaScan，线扫节点只显示LineScan。
/// </summary>
public enum EVisionAcquisitionKind
{
    /// <summary>面阵相机；每次返回一张已经完成的二维整图。</summary>
    AreaScan = 0,

    /// <summary>线扫相机或采集卡通道；由Adapter完成拼接后返回整张二维图，不进入公共模型。</summary>
    LineScan = 1
}
