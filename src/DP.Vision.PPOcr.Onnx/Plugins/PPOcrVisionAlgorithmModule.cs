using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Algorithms;

namespace DP.Vision.PPOcr.Onnx;

/// <summary>PP-OCR 单行识别模块，预处理实现显式绑定。</summary>
public sealed class PPOcrVisionAlgorithmModule : IVisionAlgorithmModule
{
    /// <inheritdoc/>
    public string ExtensionId => "dp.vision.ppocr";
    /// <inheritdoc/>
    public void Register(IVisionAlgorithmRegistration registrations)
    {
        if (registrations == null) throw new ArgumentNullException(nameof(registrations));
        registrations.Add(new VisionAlgorithmDescriptor("ppocr.recognize", "PP-OCR ONNX", "1",
            new VisionAlgorithmFactory<ITextLineRecognizer>(PrepareAsync, _ => new[] { new VisionAlgorithmDependency("preprocessor", typeof(ITextLinePreprocessor)) })
                .WithConfigurationPolicy(c => VisionAlgorithmConfigurationRules.ValidateVersionOne(c, new[] { "modelPath", "expectedSha256" }, "modelPath")),
            parameters: new[] { new VisionAlgorithmParameter("modelPath", "识别模型", typeof(string), description: "带内嵌字典的ONNX模型，准备时捕获内容快照。", isFilePath: true),
                new VisionAlgorithmParameter("expectedSha256", "预期模型SHA256", typeof(string), description: "可选；不匹配则拒绝加载。") }));
    }

    private static async Task<VisionAlgorithmActivation> PrepareAsync(VisionAlgorithmConfiguration configuration, IReadOnlyDictionary<string, object> dependencies, CancellationToken token)
    {
        if (configuration.SettingsVersion != 1 || configuration.Settings.Keys.Any(k => k != "modelPath" && k != "expectedSha256")) throw new ArgumentException("OCR参数版本或字段不支持。");
        if (!configuration.Settings.TryGetValue("modelPath", out var path) || string.IsNullOrWhiteSpace(path)) throw new ArgumentException("需要 modelPath。");
        byte[] bytes;
        using (var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        {
            if (stream.Length < 1 || stream.Length > 256L * 1024 * 1024) throw new ArgumentException("识别模型大小不支持。");
            bytes = new byte[checked((int)stream.Length)]; var offset = 0;
            while (offset < bytes.Length) { var read = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false); if (read == 0) throw new EndOfStreamException(); offset += read; }
        }
        string hash;
        using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        if (configuration.Settings.TryGetValue("expectedSha256", out var expected) && !string.IsNullOrWhiteSpace(expected) && !string.Equals(expected, hash, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("识别模型SHA256不匹配。");
        return new VisionAlgorithmActivation(hash, EVisionAlgorithmSharing.SharedSerial, cancellation =>
        {
            cancellation.ThrowIfCancellationRequested(); var snapshotPath = Path.GetTempFileName();
            VisionAlgorithmResource? resource = null;
            try { File.WriteAllBytes(snapshotPath, bytes); resource = new VisionAlgorithmResource(new OnnxTextLineRecognizer(snapshotPath, (ITextLinePreprocessor)dependencies["preprocessor"], hash)); }
            finally { try { File.Delete(snapshotPath); } catch { resource?.Dispose(); throw; } }
            return Task.FromResult(resource!);
        });
    }
}
