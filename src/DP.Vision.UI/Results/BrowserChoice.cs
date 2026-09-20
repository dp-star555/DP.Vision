namespace DP.Vision.UI;

/// <summary>视图下拉选项的只读描述，不持有图像租约。</summary>
public sealed class BrowserChoice
{
    internal BrowserChoice(string id, string name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>稳定键，用于选择，不使用下拉框位置索引作为身份。</summary>
    public string Id { get; }

    /// <summary>面向用户的名称，可附带预览保留状态。</summary>
    public string Name { get; }

    /// <summary>提供原生下拉框的默认显示文本。</summary>
    public override string ToString() => Name;
}
