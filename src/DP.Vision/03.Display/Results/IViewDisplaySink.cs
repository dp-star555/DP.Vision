using System.Collections.Generic;

namespace DP.Vision;

/// <summary>中立的视图集合提交入口，不包含流程、节点或任务执行语义。</summary>
public interface IViewDisplaySink
{
    /// <summary>
    /// 线程安全地完整替换视图集合；空集合清空预览。返回前保留需要的源，不接管客户句柄。
    /// 多次提交按入口接受顺序生效，异步工作的过期结果过滤由调用方负责。
    /// </summary>
    /// <param name="views">按展示顺序排列、键唯一的视图；所有输入源须有效至方法返回。</param>
    /// <returns>是否接受替换；入口关闭、视图数或几何预算不足时为false，原集合不变。接受后部分像素仍可能因预算不保留。</returns>
    bool SetViews(IEnumerable<VisionView> views);
}
