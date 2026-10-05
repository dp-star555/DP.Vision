using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DP.Vision.Algorithms;

/// <summary>坐标单位；参考像素不代表毫米。</summary>
public enum EVisionCoordinateUnit
{
    /// <summary>独立参考坐标的像素单位。</summary>
    ReferencePixel = 0,
    /// <summary>由明确标定关系建立的毫米单位。</summary>
    Millimeter = 1
}

/// <summary>稳定业务坐标定义；不含定位模型和运行图像。</summary>
public sealed class VisionCoordinateDefinition
{
    /// <summary>建立定义，修改原点/轴含义或单位时应更新版本。</summary>
    /// <param name="id">稳定ID。</param><param name="name">显示名称。</param><param name="version">正版本。</param>
    /// <param name="unit">坐标单位。</param><param name="originDescription">原点含义。</param>
    /// <param name="axisDescription">轴正方向约定。</param>
    public VisionCoordinateDefinition(string id, string name, int version = 1,
        EVisionCoordinateUnit unit = EVisionCoordinateUnit.ReferencePixel,
        string originDescription = "业务原点", string axisDescription = "X正方向指定，Y正方向顺时针90度")
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || version < 1
            || !Enum.IsDefined(typeof(EVisionCoordinateUnit), unit) || string.IsNullOrWhiteSpace(originDescription) || string.IsNullOrWhiteSpace(axisDescription))
            throw new ArgumentException("坐标定义的身份、版本、单位和原点/轴约定必须有效。");
        Id = id; Name = name; Version = version; Unit = unit; OriginDescription = originDescription; AxisDescription = axisDescription;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        { writer.Write(id); writer.Write(version); writer.Write((int)unit); writer.Write(originDescription); writer.Write(axisDescription); }
        using var hash = SHA256.Create(); Signature = BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-", "");
    }
    /// <summary>稳定身份。</summary>
    public string Id { get; }
    /// <summary>可修改的显示名称，不参与语义签名。</summary>
    public string Name { get; }
    /// <summary>定义版本。</summary>
    public int Version { get; }
    /// <summary>单位。</summary>
    public EVisionCoordinateUnit Unit { get; }
    /// <summary>原点约定。</summary>
    public string OriginDescription { get; }
    /// <summary>轴方向约定。</summary>
    public string AxisDescription { get; }
    /// <summary>语义签名，与模板像素无关。</summary>
    public string Signature { get; }
    /// <summary>输出单位名称。</summary>
    public string UnitName => Unit == EVisionCoordinateUnit.Millimeter ? "mm" : "reference-px";
}

