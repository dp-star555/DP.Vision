using System.Linq;

namespace DP.Vision.UI;

public sealed partial class ResultBrowserSession
{
    /// <summary>依据界面实际显示的集合选择视图，替换集合后的旧下拉事件被忽略。</summary>
    /// <param name="expected">发起操作时显示的快照。</param>
    /// <param name="viewId">目标视图键。</param>
    /// <returns>是否应用选择。</returns>
    public bool TrySelectView(ResultBrowserSnapshot expected, string viewId)
    {
        lock (_gate)
        {
            Alive();
            if (!Matches(expected) || !_views.Any(v => v.Id == viewId))
                return false;
            SelectView(viewId);
            return true;
        }
    }

    /// <summary>仅修改仍对应当前界面快照的视图，防止旧复选事件影响新集合。</summary>
    /// <param name="expected">界面显示的快照。</param>
    /// <param name="layerId">图层键。</param>
    /// <param name="visible">目标可见性。</param>
    /// <returns>是否应用变更。</returns>
    public bool TrySetLayerVisible(ResultBrowserSnapshot expected, string layerId, bool visible)
    {
        lock (_gate)
        {
            Alive();
            if (!Matches(expected) || SelectedView()?.Overlay.Layers.Any(l => l.Id == layerId) != true)
                return false;
            SetLayerVisible(layerId, visible);
            return true;
        }
    }

    /// <summary>在快照仍有效时全选、全不选或恢复默认。</summary>
    /// <param name="expected">界面显示的快照。</param>
    /// <param name="visible">true全选，false全不选，null恢复默认。</param>
    /// <returns>是否应用变更。</returns>
    public bool TrySetAllLayersVisible(ResultBrowserSnapshot expected, bool? visible)
    {
        lock (_gate)
        {
            Alive();
            if (!Matches(expected))
                return false;
            if (visible.HasValue)
                SetAllLayersVisible(visible.Value);
            else
                ResetLayerVisibility();
            return true;
        }
    }

    private bool Matches(ResultBrowserSnapshot expected) =>
        expected != null
        && ReferenceEquals(expected.Collection, _collection)
        && expected.ViewId == _selectedView;
}
