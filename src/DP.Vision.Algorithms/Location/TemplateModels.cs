using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DP.Vision.Algorithms;

/// <summary>可重建的模板几何；原点和方向使用样图像素边界坐标，独立于模型内容。</summary>
[DataContract]
public sealed class VisionTemplateDefinition
{
    /// <summary>样图宽。</summary>
    [DataMember] public int SourceWidth { get; set; }
    /// <summary>样图高。</summary>
    [DataMember] public int SourceHeight { get; set; }
    /// <summary>模板裁剪左边界。</summary>
    [DataMember] public int X { get; set; }
    /// <summary>模板裁剪上边界。</summary>
    [DataMember] public int Y { get; set; }
    /// <summary>模板宽。</summary>
    [DataMember] public int Width { get; set; }
    /// <summary>模板高。</summary>
    [DataMember] public int Height { get; set; }
    /// <summary>样图中的业务原点X。</summary>
    [DataMember] public double OriginX { get; set; }
    /// <summary>样图中的业务原点Y。</summary>
    [DataMember] public double OriginY { get; set; }
    /// <summary>X轴在样图中的顺时针弧度；Y轴顺时针90度。</summary>
    [DataMember] public double AxisAngleRadians { get; set; }
    /// <summary>参考几何语义版本，重新生成同一几何的模型不改变它。</summary>
    [DataMember] public int ReferenceVersion { get; set; } = 1;
    /// <summary>参考定义的资源身份；同一模板重建模型时保留，新模板使用新身份。</summary>
    [DataMember] public string ReferenceIdentity { get; set; } = "";
    /// <summary>验证有限值、尺寸和区域。</summary>
    public void Validate()
    {
        if (SourceWidth < 1 || SourceHeight < 1 || Width < 1 || Height < 1 || X < 0 || Y < 0
            || (long)X + Width > SourceWidth || (long)Y + Height > SourceHeight || ReferenceVersion < 1
            || !Finite(OriginX) || !Finite(OriginY) || !Finite(AxisAngleRadians)
            || (long)SourceWidth * SourceHeight > 16777216)
            throw new ArgumentException("模板尺寸、参考几何或区域无效。");
    }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    /// <summary>参考几何签名；不包含模型像素、掩码和引擎。</summary>
    public string GeometrySignature()
    {
        Validate();
        using var stream = new System.IO.MemoryStream();
        using (var writer = new System.IO.BinaryWriter(stream, Encoding.UTF8, true))
        { writer.Write(SourceWidth); writer.Write(SourceHeight); writer.Write(X); writer.Write(Y); writer.Write(Width); writer.Write(Height);
          writer.Write(OriginX); writer.Write(OriginY); writer.Write(AxisAngleRadians); }
        return VisionTemplateStore.Hash(stream.ToArray());
    }
    /// <summary>匹配结果使用的参考：原点换算到裁剪模板坐标；签名包含参考身份、版本和几何，不含模型像素。</summary>
    public TemplateReference Reference() => new TemplateReference(OriginX - X, OriginY - Y, AxisAngleRadians,
        "template:" + ReferenceIdentity + ":v" + ReferenceVersion + ":" + GeometrySignature());
}

/// <summary>引擎输出的文件；文件名必须是资源目录内的相对路径。</summary>
public sealed class VisionTemplateArtifact
{
    /// <summary>创建文件快照。</summary>
    public VisionTemplateArtifact(string name, byte[] content) { Name = name; Content = (byte[])(content ?? throw new ArgumentNullException(nameof(content))).Clone(); }
    /// <summary>相对路径。</summary>
    public string Name { get; }
    /// <summary>发布时再次捕获的文件内容。</summary>
    public byte[] Content { get; }
}

/// <summary>模板制作请求；调用期间借用样图，生成结果必须拥有独立快照。</summary>
public sealed class VisionTemplateBuildRequest
{
    /// <summary>建立制作请求。</summary>
    public VisionTemplateBuildRequest(ImageFrame source, VisionTemplateDefinition definition, RegionGeometry? mask,
        IReadOnlyDictionary<string, string> settings)
    { Source = source; Definition = definition; Mask = mask; Settings = settings; }
    /// <summary>样图。</summary>
    public ImageFrame Source { get; }
    /// <summary>参考几何及裁剪区域。</summary>
    public VisionTemplateDefinition Definition { get; }
    /// <summary>样图坐标中的有效区域，null表示全部裁剪区域。</summary>
    public RegionGeometry? Mask { get; }
    /// <summary>引擎专有制作参数。</summary>
    public IReadOnlyDictionary<string, string> Settings { get; }
}

/// <summary>引擎制作结果；共同管理层不解释引擎文件。</summary>
public sealed class VisionTemplateBuild
{
    /// <summary>创建独立模型快照。</summary>
    public VisionTemplateBuild(string implementationId, string format, VisionTemplateDefinition definition,
        IReadOnlyDictionary<string, string> settings, IEnumerable<VisionTemplateArtifact> files)
    {
        ImplementationId = implementationId; Format = format; Definition = VisionTemplateStore.CopyDefinition(definition);
        Settings = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(settings.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
        Files = files.Select(f => new VisionTemplateArtifact(f.Name, f.Content)).ToArray();
    }
    /// <summary>与模型配套的匹配实现。</summary>
    public string ImplementationId { get; }
    /// <summary>引擎模型格式及版本。</summary>
    public string Format { get; }
    /// <summary>共同参考定义。</summary>
    public VisionTemplateDefinition Definition { get; }
    /// <summary>制作参数。</summary>
    public IReadOnlyDictionary<string, string> Settings { get; }
    /// <summary>包括可重建样图及引擎专有内容。</summary>
    public IReadOnlyList<VisionTemplateArtifact> Files { get; }
}

/// <summary>引擎登记配套制作能力；打开属性页面不会调用它。</summary>
[VisionCapability("location.template-build", "定位", "模板制作")]
public interface IVisionTemplateBuilder
{
    /// <summary>生成模型和可重建快照，失败不发布资源。</summary>
    Task<VisionTemplateBuild> BuildAsync(VisionTemplateBuildRequest request, CancellationToken token = default);
}

/// <summary>模型制作扩展由工厂描述；不要求通用UI理解引擎类型。</summary>
public interface IVisionTemplateFactoryDescription
{
    /// <summary>匹配方式的显示名称；不以引擎程序集名称替代算法含义。</summary>
    string MethodDisplayName { get; }
    /// <summary>配套制作能力的实现ID。</summary>
    string BuilderImplementationId { get; }
    /// <summary>制作参数，独立于运行初始化参数。</summary>
    IReadOnlyList<VisionAlgorithmParameter> BuildParameters { get; }
}

/// <summary>草稿试匹配入口，不发布文件；返回的资源由编辑器拥有。</summary>
public interface IVisionTemplatePreviewFactory
{
    /// <summary>校验制作结果并创建独立的预览模型。</summary>
    Task<VisionAlgorithmResource> PreparePreviewAsync(VisionTemplateBuild build, CancellationToken token = default);
}

/// <summary>可选的轻量搜索校验；编辑界面在创建原生模型前提示范围或预算问题。</summary>
public interface IVisionTemplateSearchValidator
{
    /// <summary>根据模板几何、制作参数与搜索条件验证，不读取模型或申请原生资源。</summary>
    /// <param name="definition">已生成模板的参考几何。</param>
    /// <param name="settings">该模型的制作参数。</param>
    /// <param name="search">本次图像中的搜索范围。</param>
    /// <param name="options">本次候选及工作量上限。</param>
    /// <returns>问题列表；空集合表示轻量检查通过。</returns>
    IReadOnlyList<string> ValidateSearch(VisionTemplateDefinition definition, IReadOnlyDictionary<string, string> settings, PixelBounds search, TemplatePoseOptions options);
}

/// <summary>工厂可提供解析后的轻量资源检查；只有显式资源检查时调用。</summary>
public interface IVisionAlgorithmResourceInspector
{
    /// <summary>读取有界清单和文件元数据，不加载模型、校验许可或创建实例。</summary>
    IReadOnlyList<string> InspectResources(VisionAlgorithmConfiguration configuration);
}

/// <summary>运行前准备好的模板匹配，原生模型留在引擎内部。</summary>
[VisionCapability("location.template-model", "定位", "模板模型匹配")]
public interface IPreparedVisionTemplateMatcher
{
    /// <summary>返回独立参考定义副本，不能修改引擎内部状态。</summary>
    VisionTemplateDefinition Definition { get; }
    /// <summary>本模型内容身份。</summary>
    string ModelIdentity { get; }
    /// <summary>使用准备好的模型搜索，输出裁剪模板到原图姿态；结果参考取<see cref="VisionTemplateDefinition.Reference"/>。</summary>
    TemplatePoseResult Match(ImageFrame frame, PixelBounds search, TemplatePoseOptions options, RegionGeometry? region = null, CancellationToken token = default);
}
