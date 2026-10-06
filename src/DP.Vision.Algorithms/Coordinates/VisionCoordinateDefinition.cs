using System;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DP.Vision.Algorithms;

/// <summary>坐标单位；参考像素不代表毫米。</summary>
public enum EVisionCoordinateUnit
{
    /// <summary>独立参考坐标的像素单位。</summary>
    [Description("参考像素")]
    ReferencePixel = 0,
    /// <summary>由明确标定关系建立的毫米单位。</summary>
    [Description("毫米")]
    Millimeter = 1
}

/// <summary>稳定业务坐标定义；不含定位模型和运行图像。</summary>
public sealed class VisionCoordinateDefinition
{
    /// <summary>建立定义；改变基准、单位或标定时应递增版本。</summary>
    /// <param name="id">稳定ID。</param><param name="name">显示名称，不参与签名。</param><param name="version">正版本。</param>
    /// <param name="unit">坐标单位。</param>
    /// <param name="reference">附加的参考签名（如模板参考），参与签名；没有时为空。</param>
    public VisionCoordinateDefinition(string id, string name, int version = 1,
        EVisionCoordinateUnit unit = EVisionCoordinateUnit.ReferencePixel, string reference = "")
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || version < 1
            || !Enum.IsDefined(typeof(EVisionCoordinateUnit), unit) || reference == null)
            throw new ArgumentException("坐标定义的身份、名称、版本和单位必须有效。");
        Id = id; Name = name; Version = version; Unit = unit; Reference = reference;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        { writer.Write(id); writer.Write(version); writer.Write((int)unit); writer.Write(reference); }
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
    /// <summary>附加的参考签名（如模板参考）；没有时为空。</summary>
    public string Reference { get; }
    /// <summary>语义签名：ID、版本、单位及参考签名，不含显示名称。</summary>
    public string Signature { get; }
    /// <summary>输出单位名称。</summary>
    public string UnitName => Unit == EVisionCoordinateUnit.Millimeter ? "mm" : "reference-px";
}

