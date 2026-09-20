namespace DP.Vision.UI;

/// <summary>当前视图的图层选择项，显隐状态属于浏览器，不修改原始图层。</summary>
public sealed class BrowserLayerChoice
{
    internal BrowserLayerChoice(string id, string name, bool visible)
    {
        Id = id;
        Name = name;
        Visible = visible;
    }

    /// <summary>图层稳定键，只在所属视图内使用。</summary>
    public string Id { get; }

    /// <summary>图层显示名称。</summary>
    public string Name { get; }

    /// <summary>应用用户偏好后的显示开关。</summary>
    public bool Visible { get; }

    /// <summary>复选列表的显示文本。</summary>
    public override string ToString() => Name;
}
