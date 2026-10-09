using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DP.Vision.Algorithms;

/// <summary>厂商中立的完整不可变异常模型资产；不解释原生文件内容，不持有运行句柄。</summary>
public sealed class AnomalyModelAsset
{
    private const int Magic = 0x414d5044;
    private const int Version = 1;
    /// <summary>完整资产包预算；原生模型及配套文件共同计入。</summary>
    public const int MaximumBytes = 256 * 1024 * 1024;
    private readonly Lazy<string> _digest;
    /// <summary>包含实现/格式/预处理/标定/全部文件的规范化SHA256；首次计算后固定。</summary>
    public string ContentSha256 => _digest.Value;
    private readonly SortedDictionary<string, byte[]> _files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
    /// <summary>创建并复制模型资产。</summary>
    /// <param name="implementationId">实际运行实现身份，不是显示品牌。</param>
    /// <param name="modelFormat">明确且版本化的模型格式。</param>
    /// <param name="width">制作输入宽度。</param>
    /// <param name="height">制作输入高度。</param>
    /// <param name="threshold">本资产标定阈值。</param>
    /// <param name="trainingImages">训练来源/样本统计，标定说明须明确口径。</param>
    /// <param name="calibration">标定协议及结果。</param>
    /// <param name="files">根内相对名称到原生文件字节；复制后不可变。</param>
    /// <param name="settings">预处理、算法和训练配置，格式由对应实现解释。</param>
    public AnomalyModelAsset(string implementationId, string modelFormat, int width, int height,
        double threshold, int trainingImages, string calibration, IReadOnlyDictionary<string, byte[]> files,
        IReadOnlyDictionary<string, string>? settings = null)
    {
        if (string.IsNullOrWhiteSpace(implementationId) || implementationId.Length > 200 || string.IsNullOrWhiteSpace(modelFormat)
            || modelFormat.Length > 100 || width < 1 || height < 1 || (long)width * height > 16777216
            || !(threshold > 0) || double.IsInfinity(threshold) || trainingImages < 1 || calibration == null || calibration.Length > 8192)
            throw new ArgumentException("异常模型资产元数据无效。");
        if (files == null || files.Count < 1 || files.Count > 128) throw new ArgumentException("模型文件清单无效。", nameof(files));
        long total = 0;
        foreach (var pair in files)
        {
            if (!SafeName(pair.Key) || pair.Value == null || pair.Value.Length == 0 || (total += pair.Value.Length) > MaximumBytes - 65536)
                throw new ArgumentException("模型文件名称或资产预算无效。", nameof(files));
            _files.Add(pair.Key, (byte[])pair.Value.Clone());
        }
        var config = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in settings ?? new Dictionary<string, string>())
        {
            if (config.Count >= 64 || string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 || pair.Value == null || pair.Value.Length > 1024)
                throw new ArgumentException("模型配置清单无效。", nameof(settings));
            config.Add(pair.Key, pair.Value);
        }
        ImplementationId = implementationId; ModelFormat = modelFormat; Width = width; Height = height;
        Threshold = threshold; TrainingImages = trainingImages; Calibration = calibration;
        Settings = new ReadOnlyDictionary<string, string>(config);
        FileNames = Array.AsReadOnly(_files.Keys.ToArray());
        _digest = new Lazy<string>(() => { using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(ToBytes())).Replace("-", "").ToLowerInvariant(); });
    }
    /// <summary>运行实现身份。</summary>
    public string ImplementationId { get; }
    /// <summary>版本化资产格式。</summary>
    public string ModelFormat { get; }
    /// <summary>制作输入宽度。</summary>
    public int Width { get; }
    /// <summary>制作输入高度。</summary>
    public int Height { get; }
    /// <summary>独立标定阈值。</summary>
    public double Threshold { get; }
    /// <summary>训练统计。</summary>
    public int TrainingImages { get; }
    /// <summary>标定协议。</summary>
    public string Calibration { get; }
    /// <summary>不可变预处理/算法配置。</summary>
    public IReadOnlyDictionary<string, string> Settings { get; }
    /// <summary>配套文件相对名称。</summary>
    public IReadOnlyList<string> FileNames { get; }
    /// <summary>读取文件副本；不返回内部可写内存。</summary>
    /// <param name="name">清单内文件名称。</param>
    public byte[] Read(string name) => (byte[])_files[name].Clone();
    /// <summary>该字节包是否使用新资产头；旧DPPA格式由旧实现Adapter解析。</summary>
    /// <param name="bytes">模型字节。</param>
    public static bool IsAsset(byte[] bytes) => bytes != null && bytes.Length >= 8 && BitConverter.ToInt32(bytes, 0) == Magic;
    /// <summary>序列化清单及所有文件；每个文件独立附带摘要。</summary>
    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(Magic); writer.Write(Version); writer.Write(ImplementationId); writer.Write(ModelFormat);
            writer.Write(Width); writer.Write(Height); writer.Write(Threshold); writer.Write(TrainingImages); writer.Write(Calibration);
            writer.Write(Settings.Count);
            foreach (var pair in Settings) { writer.Write(pair.Key); writer.Write(pair.Value); }
            writer.Write(_files.Count);
            using var sha = SHA256.Create();
            foreach (var pair in _files) { writer.Write(pair.Key); writer.Write(pair.Value.Length); writer.Write(sha.ComputeHash(pair.Value)); writer.Write(pair.Value); }
        }
        if (stream.Length > MaximumBytes) throw new InvalidDataException("异常模型资产超过256MiB预算。");
        return stream.ToArray();
    }
    /// <summary>有界读取，校验格式版本、文件摘要、路径及完整性。</summary>
    /// <param name="bytes">完整资产包。</param>
    public static AnomalyModelAsset FromBytes(byte[] bytes)
    {
        if (bytes == null || bytes.Length > MaximumBytes) throw new InvalidDataException("模型资产预算无效。");
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version) throw new InvalidDataException("未知异常模型资产格式。");
        string id = Text(reader, 200), format = Text(reader, 100);
        int w = reader.ReadInt32(), h = reader.ReadInt32(); double threshold = reader.ReadDouble(); int samples = reader.ReadInt32();
        string calibration = Text(reader, 8192);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        int n = reader.ReadInt32(); if (n < 0 || n > 64) throw new InvalidDataException("配置数量无效。");
        for (int i = 0; i < n; i++) settings.Add(Text(reader, 128), Text(reader, 1024));
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        n = reader.ReadInt32(); if (n < 1 || n > 128) throw new InvalidDataException("文件数量无效。");
        using var sha = SHA256.Create();
        for (int i = 0; i < n; i++)
        {
            string name = Text(reader, 128); int length = reader.ReadInt32();
            if (!SafeName(name) || length <= 0 || length > stream.Length - stream.Position - 32) throw new InvalidDataException("模型文件清单无效。");
            var hash = reader.ReadBytes(32); var content = reader.ReadBytes(length);
            if (!hash.SequenceEqual(sha.ComputeHash(content))) throw new InvalidDataException("模型配套文件摘要不匹配。");
            files.Add(name, content);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("资产包含尾随数据。");
        return new AnomalyModelAsset(id, format, w, h, threshold, samples, calibration, files, settings);
    }
    private static bool SafeName(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 128
        && name[0] != '/' && !name.Contains('\\') && !name.Contains(':')
        && name.Split('/').All(p => p.Length > 0 && p != "." && p != ".." && p.All(c => c >= 32 && c != 127));
    private static string Text(BinaryReader reader, int maximum)
    {
        uint length = 0; int shift = 0;
        for (int i = 0; i < 5; i++) { byte b = reader.ReadByte(); if (i == 4 && (b & 0xf0) != 0) throw new InvalidDataException("字符串长度无效。");
            length |= (uint)(b & 127) << shift; if ((b & 128) == 0) break; shift += 7; if (i == 4) throw new InvalidDataException("字符串长度无效。"); }
        if (length > maximum * 4 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("字符串超出预算。");
        string text = new UTF8Encoding(false, true).GetString(reader.ReadBytes((int)length));
        if (text.Length > maximum) throw new InvalidDataException("字符串超出预算。"); return text;
    }
}
