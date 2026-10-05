using System;
using System.Threading.Tasks;

namespace DP.Vision.Algorithms;

/// <summary>已有纯 C# 算法的内置登记模块。</summary>
public sealed class ManagedVisionAlgorithmModule : IVisionAlgorithmModule
{
    /// <inheritdoc/>
    public string ExtensionId => "dp.vision.managed";
    /// <inheritdoc/>
    public void Register(IVisionAlgorithmRegistration registrations)
    {
        if (registrations == null) throw new ArgumentNullException(nameof(registrations));
        registrations.Add(new VisionAlgorithmDescriptor("managed.color", "Managed", "1", VisionAlgorithmFactory<IColorAnalyzer>.Stateless(() => new RgbColorAnalyzer())));
        registrations.Add(new VisionAlgorithmDescriptor("managed.blob-select", "Managed", "1", VisionAlgorithmFactory<IBlobSelector>.Stateless(() => new BlobSelector())));
        registrations.Add(new VisionAlgorithmDescriptor("managed.caliper", "Managed", "1", VisionAlgorithmFactory<ICaliperMeasurer>.Stateless(() => new CaliperMeasurer())));
        registrations.Add(new VisionAlgorithmDescriptor("managed.robust-line", "Managed", "1", VisionAlgorithmFactory<IRobustLineFitter>.Stateless(() => new RobustLineFitter())));
        registrations.Add(new VisionAlgorithmDescriptor("managed.geometry", "Managed", "1", VisionAlgorithmFactory<IGeometryMeasurer>.Stateless(() => new GeometryMeasurer())));
        registrations.Add(new VisionAlgorithmDescriptor("managed.character-match", "Managed", "1", VisionAlgorithmFactory<ICharacterMatcher>.Stateless(() => new OrdinalCharacterMatcher())));
        registrations.Add(new VisionAlgorithmDescriptor("managed.text-quality", "Managed", "1", new VisionAlgorithmFactory<ITextQualityInspector>((configuration, dependencies, token) =>
        {
            token.ThrowIfCancellationRequested();
            if (configuration.SettingsVersion != 1 || configuration.Settings.Count != 0) throw new ArgumentException("文字质量组合只接受版本1空初始化设置。");
            return Task.FromResult(new VisionAlgorithmActivation("composition:v1", EVisionAlgorithmSharing.SharedConcurrent,
                cancellation => { cancellation.ThrowIfCancellationRequested(); return Task.FromResult(new VisionAlgorithmResource(new TextQualityInspector(
                    (ICharacterSegmenter)dependencies["segmenter"], (ICharacterMatcher)dependencies["matcher"], (IGlyphComparer)dependencies["comparer"]))); }));
        }, _ => new[] { new VisionAlgorithmDependency("segmenter", typeof(ICharacterSegmenter)), new VisionAlgorithmDependency("matcher", typeof(ICharacterMatcher)), new VisionAlgorithmDependency("comparer", typeof(IGlyphComparer)) })
            .WithConfigurationPolicy(c => VisionAlgorithmConfigurationRules.ValidateVersionOne(c, Array.Empty<string>()))));
    }
}
