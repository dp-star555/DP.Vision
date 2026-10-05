using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DP.Vision.Algorithms;

namespace DP.Vision.OpenCv;

/// <summary>OpenCV 算法模块；登记不触发原生运算或加载模型。</summary>
public sealed class OpenCvVisionAlgorithmModule : IVisionAlgorithmModule
{
    /// <inheritdoc/>
    public string ExtensionId => "dp.vision.opencv";
    /// <inheritdoc/>
    public void Register(IVisionAlgorithmRegistration registrations)
    {
        if (registrations == null) throw new ArgumentNullException(nameof(registrations));
        Add<IImageFileReader>(registrations, "image-read", () => new OpenCvImageFileReader());
        Add<IImagePreprocessor>(registrations, "preprocess", () => new OpenCvImagePreprocessor());
        Add<IRegionProcessor>(registrations, "region", () => new OpenCvRegionProcessor());
        Add<IBlobAnalyzer>(registrations, "blob", () => new OpenCvBlobAnalyzer());
        Add<IEdgeMeasurer>(registrations, "edges", () => new OpenCvEdgeMeasurer());
        Add<ITemplateLocator>(registrations, "template", () => new OpenCvTemplateLocator());
        Add<ITemplatePoseLocator>(registrations, "template-pose", () => new OpenCvTemplatePoseLocator());
        foreach (bool pose in new[] { false, true })
        {
            var factory = new OpenCvTemplateModelFactory(pose);
            registrations.Add(new VisionAlgorithmDescriptor(factory.ImplementationId, "OpenCV", "1", factory,
                features: pose ? new[] { "translation", "rotation", "scale", "masked-template" } : new[] { "translation", "masked-template" },
                parameters: new[] { new VisionAlgorithmParameter("templatePath", "模板资源", typeof(string), description: "已发布版本的manifest.json。", isFilePath: true) }));
            registrations.Add(new VisionAlgorithmDescriptor(factory.BuilderImplementationId, "OpenCV", "1",
                VisionAlgorithmFactory<IVisionTemplateBuilder>.Stateless(() => new OpenCvTemplateModelBuilder(factory.ImplementationId), () => _ = OpenCvSharp.Cv2.GetVersionString())));
        }
        Add<ITranslationRegistrar>(registrations, "translation", () => new OpenCvTranslationRegistrar());
        Add<ICharacterSegmenter>(registrations, "character-segment", () => new OpenCvCharacterSegmenter(), "glyph-candidates");
        Add<IGlyphComparer>(registrations, "glyph-compare", () => new OpenCvGlyphComparer());
        Add<ITextLinePreprocessor>(registrations, "text-preprocess", () => new OpenCvTextLinePreprocessor());
        Add<IBlankQualityInspector>(registrations, "blank", () => new OpenCvInkInspector());
        Add<IFixedQualityInspector>(registrations, "fixed", () => new OpenCvInkInspector());
        Add<ILinearBarcodeQualityInspector>(registrations, "linear-quality", () => new OpenCvBarcodePrintInspector());
        Add<IQrQualityInspector>(registrations, "qr-quality", () => new OpenCvQrPrintInspector());
        Add<IPatchAnomalyDetector>(registrations, "patch", () => new OpenCvPatchAnomalyDetector(), "grouped-training");
        registrations.Add(new VisionAlgorithmDescriptor("opencv.cnn-patch", "OpenCV", "1", new VisionAlgorithmFactory<IPatchAnomalyDetector>(PrepareCnnAsync)
            .WithConfigurationPolicy(c => VisionAlgorithmConfigurationRules.ValidateVersionOne(c, new[] { "backbonePath", "scale" }, "backbonePath")),
            parameters: new[] { new VisionAlgorithmParameter("backbonePath", "骨干模型", typeof(string), description: "ONNX骨干模型路径，准备时捕获内容快照。", isFilePath: true),
                new VisionAlgorithmParameter("scale", "输入放大倍数", typeof(double), "2", "1至4；必须与参考模型一致。", minimum: 1, maximum: 4) }));
        registrations.Add(new VisionAlgorithmDescriptor("opencv.character-anomaly", "OpenCV", "1", new VisionAlgorithmFactory<ICharacterAnomalyDetector>((configuration, dependencies, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (configuration.SettingsVersion != 1 || configuration.Settings.Count != 0) throw new ArgumentException("字符异常检测初始化只接受版本1空配置。");
            return Task.FromResult(new VisionAlgorithmActivation("character:v1", EVisionAlgorithmSharing.SharedConcurrent,
                cancellation => { cancellation.ThrowIfCancellationRequested(); return Task.FromResult(new VisionAlgorithmResource(new OpenCvCharacterAnomalyDetector((IPatchAnomalyDetector)dependencies["trainer"]))); }));
        }, _ => new[] { new VisionAlgorithmDependency("trainer", typeof(IPatchAnomalyDetector)) })
            .WithConfigurationPolicy(c => VisionAlgorithmConfigurationRules.ValidateVersionOne(c, Array.Empty<string>()))));
    }

    private static void Add<T>(IVisionAlgorithmRegistration registrations, string id, Func<T> create, params string[] features) where T : class =>
        registrations.Add(new VisionAlgorithmDescriptor("opencv." + id, "OpenCV", "1",
            VisionAlgorithmFactory<T>.Stateless(create, () => { _ = OpenCvSharp.Cv2.GetVersionString(); }), features));

    private static async Task<VisionAlgorithmActivation> PrepareCnnAsync(VisionAlgorithmConfiguration configuration,
        System.Collections.Generic.IReadOnlyDictionary<string, object> dependencies, CancellationToken token)
    {
        if (configuration.SettingsVersion != 1 || configuration.Settings.Keys.Any(k => k != "backbonePath" && k != "scale"))
            throw new ArgumentException("CNN参数版本或字段不支持。");
        if (!configuration.Settings.TryGetValue("backbonePath", out var path) || string.IsNullOrWhiteSpace(path)) throw new ArgumentException("需要骨干模型路径 backbonePath。");
        var scale = configuration.Settings.TryGetValue("scale", out var text) ? double.Parse(text, CultureInfo.InvariantCulture) : 2;
        if (double.IsNaN(scale) || scale < 1 || scale > 4) throw new ArgumentOutOfRangeException("scale");
        byte[] bytes;
        using (var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
        {
            if (stream.Length < 1 || stream.Length > 256L * 1024 * 1024) throw new ArgumentException("骨干模型大小不支持。");
            bytes = new byte[checked((int)stream.Length)]; var offset = 0;
            while (offset < bytes.Length) { var read = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false); if (read == 0) throw new EndOfStreamException(); offset += read; }
        }
        string identity;
        using (var sha = SHA256.Create()) identity = Convert.ToBase64String(sha.ComputeHash(bytes)) + ":" + scale.ToString("R", CultureInfo.InvariantCulture);
        return new VisionAlgorithmActivation(identity, EVisionAlgorithmSharing.SharedSerial, cancellation =>
        {
            cancellation.ThrowIfCancellationRequested();
            var snapshotPath = Path.GetTempFileName();
            VisionAlgorithmResource? resource = null;
            try { File.WriteAllBytes(snapshotPath, bytes); resource = new VisionAlgorithmResource(new OpenCvCnnPatchAnomalyDetector(snapshotPath, scale)); }
            finally { try { File.Delete(snapshotPath); } catch { resource?.Dispose(); throw; } }
            return Task.FromResult(resource!);
        });
    }
}
