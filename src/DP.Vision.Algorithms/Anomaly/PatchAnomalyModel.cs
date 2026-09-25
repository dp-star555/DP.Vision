using System;
using System.IO;

namespace DP.Vision.Algorithms;

/// <summary>
/// 由良品训练得到的局部块记忆库及标定阈值，只含数值数据，可序列化后随配方/ROI保存。
/// 与位置无关的模式保存核心集块特征；位置相关模式（<see cref = "Radius"/>大于0）保存各良品裁图的逐图数据
/// （手工特征为墨量平面，CNN特征为灰度裁图、加载后按同一骨干网络重算特征），检测时只与同一位置±Radius像素内的良品块比较。
/// </summary>
public sealed class PatchAnomalyModel
{
    private const int Magic = 0x41505044; // "DPPA"
    private const int Version = 2;

    /// <summary>手工块特征（墨量平面）的特征来源标识。</summary>
    public const string Handcrafted = "handcrafted";
    private readonly float[] _memory;

    /// <summary>创建模型快照；记忆库数组会被复制。</summary>
    /// <param name = "patchSize">训练所用块边长。</param>
    /// <param name = "dimensions">每个块特征的维数。</param>
    /// <param name = "memory">按行排列的记忆库特征，长度为块数×维数。</param>
    /// <param name = "threshold">标定阈值（块到最近良品块的距离），大于0。</param>
    /// <param name = "trainingImages">参与训练的良品图数。</param>
    /// <param name = "calibration">阈值来源说明，例如留一法或增强。</param>
    /// <param name = "radius">位置相关搜索半径（像素）；0为与位置无关的核心集模式。</param>
    /// <param name = "width">位置相关模式的裁图宽度。</param>
    /// <param name = "height">位置相关模式的裁图高度。</param>
    /// <param name = "featureSource">特征来源：<see cref = "Handcrafted"/>或CNN骨干网络标识（含模型哈希与缩放）；检测时必须一致。</param>
    public PatchAnomalyModel(
        int patchSize,
        int dimensions,
        float[] memory,
        double threshold,
        int trainingImages,
        string calibration,
        int radius = 0,
        int width = 0,
        int height = 0,
        string featureSource = Handcrafted
    )
    {
        bool local = radius > 0;
        featureSource = string.IsNullOrEmpty(featureSource) ? Handcrafted : featureSource;
        int perImage = trainingImages > 0 && memory != null ? memory.Length / trainingImages : 0;
        if (
            patchSize < 4
            || dimensions < 1
            || memory == null
            || memory.Length == 0
            || radius < 0
            || (
                local
                    ? width < 1
                        || height < 1
                        || memory.Length != trainingImages * perImage
                        || (
                            featureSource == Handcrafted
                                ? width < patchSize
                                    || height < patchSize
                                    || perImage != PlaneLength(width, height)
                                : perImage < 1
                        )
                    : memory.Length % dimensions != 0
            )
            || double.IsNaN(threshold)
            || threshold <= 0
            || trainingImages < 1
        )
        {
            throw new ArgumentException("Invalid patch anomaly model.");
        }

        Radius = radius;
        FeatureSource = featureSource;
        Width = local ? width : 0;
        Height = local ? height : 0;

        PatchSize = patchSize;
        Dimensions = dimensions;
        _memory = (float[])memory.Clone();
        Threshold = threshold;
        TrainingImages = trainingImages;
        Calibration = calibration ?? "";
    }

    /// <summary>训练所用块边长；检测时必须一致。</summary>
    public int PatchSize { get; }

    /// <summary>每个块特征的维数。</summary>
    public int Dimensions { get; }

    /// <summary>记忆库块数；位置相关模式为良品平面数。</summary>
    public int Count => Radius > 0 ? TrainingImages : _memory.Length / Dimensions;

    /// <summary>特征来源；检测实现必须使用同一来源，否则拒绝而不是给出无意义的得分。</summary>
    public string FeatureSource { get; }

    /// <summary>位置相关搜索半径（像素）；0为与位置无关模式。</summary>
    public int Radius { get; }

    /// <summary>位置相关模式要求的裁图宽度。</summary>
    public int Width { get; }

    /// <summary>位置相关模式要求的裁图高度。</summary>
    public int Height { get; }

    /// <summary>位置相关模式每张良品保存的浮点数：原尺度平面加1/2尺度平面。</summary>
    /// <param name = "width">裁图宽度。</param>
    /// <param name = "height">裁图高度。</param>
    public static int PlaneLength(int width, int height)
    {
        return width * height + Math.Max(1, width / 2) * Math.Max(1, height / 2);
    }

    /// <summary>标定阈值（块到最近良品块的特征距离）。</summary>
    public double Threshold { get; }

    /// <summary>参与训练的良品图数。</summary>
    public int TrainingImages { get; }

    /// <summary>阈值来源说明。</summary>
    public string Calibration { get; }

    /// <summary>复制记忆库特征（按行排列）。</summary>
    public float[] CopyMemory()
    {
        return (float[])_memory.Clone();
    }

    /// <summary>序列化为紧凑二进制（小端），用于随配方保存。</summary>
    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(PatchSize);
            writer.Write(Dimensions);
            writer.Write(Threshold);
            writer.Write(TrainingImages);
            writer.Write(Calibration);
            writer.Write(Radius);
            writer.Write(Width);
            writer.Write(Height);
            writer.Write(FeatureSource);
            writer.Write(_memory.Length);
            foreach (float v in _memory)
            {
                writer.Write(v);
            }
        }

        return stream.ToArray();
    }

    /// <summary>从<see cref = "ToBytes"/>的输出恢复模型。</summary>
    /// <param name = "bytes">完整序列化字节。</param>
    public static PatchAnomalyModel FromBytes(byte[] bytes)
    {
        if (bytes == null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }

        using var reader = new BinaryReader(new MemoryStream(bytes));
        int version = reader.ReadInt32() == Magic ? reader.ReadInt32() : -1;
        if (version != 1 && version != Version)
        {
            throw new InvalidDataException("Not a patch anomaly model, or a newer format.");
        }

        int patchSize = reader.ReadInt32(),
            dimensions = reader.ReadInt32();
        double threshold = reader.ReadDouble();
        int images = reader.ReadInt32();
        string calibration = reader.ReadString();
        int radius = reader.ReadInt32(),
            width = reader.ReadInt32(),
            height = reader.ReadInt32();
        // 版本1没有特征来源字段，只有手工特征。
        string source = version >= 2 ? reader.ReadString() : Handcrafted;
        int length = reader.ReadInt32();
        if (length <= 0 || length > 256 * 1024 * 1024)
        {
            throw new InvalidDataException("Invalid memory size.");
        }

        var memory = new float[length];
        for (int i = 0; i < length; i++)
        {
            memory[i] = reader.ReadSingle();
        }

        return new PatchAnomalyModel(
            patchSize,
            dimensions,
            memory,
            threshold,
            images,
            calibration,
            radius,
            width,
            height,
            source
        );
    }
}
