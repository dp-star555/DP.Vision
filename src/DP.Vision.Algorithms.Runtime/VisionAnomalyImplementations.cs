using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.Vision;

namespace DP.Vision.Algorithms;

/// <summary>复用现有Vision算法目录提供模型资产运行能力，不另建厂商插件发现体系。</summary>
public static class VisionAnomalyImplementations
{
    /// <summary>只捕获目录元数据，真正选中训练/模型时才创建算法工厂资源。</summary>
    /// <param name="catalog">已经原子组合、处理冲突的Vision目录。</param>
    public static IReadOnlyList<IAnomalyImplementation> FromCatalog(VisionAlgorithmCatalog catalog)
    {
        if (catalog == null) throw new ArgumentNullException(nameof(catalog));
        return Array.AsReadOnly(catalog.Implementations.Where(d => d.ContractType == typeof(IAnomalyImplementation))
            .Select(d => d.Features.Contains("normal-only-training") ? (IAnomalyImplementation)new Trainer(d) : new Implementation(d)).ToArray());
    }
    private class Implementation : IAnomalyImplementation
    {
        protected readonly VisionAlgorithmDescriptor Descriptor;
        internal Implementation(VisionAlgorithmDescriptor descriptor) => Descriptor = descriptor;
        public string ImplementationId => Descriptor.ImplementationId;
        public string DisplayName => (Descriptor.Factory as IAnomalyImplementation)?.DisplayName ?? Descriptor.Engine + " · " + ImplementationId;
        protected VisionAlgorithmResource Create(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var configuration = new VisionAlgorithmConfiguration(1, new Dictionary<string, string>());
            if (Descriptor.Factory.GetDependencies(configuration).Count != 0)
                throw new NotSupportedException("异常模型工厂必须由完整资产解释运行依赖，不能隐式借用另一个算法资源。");
            var activation = Descriptor.Factory.PrepareAsync(configuration, new Dictionary<string, object>(), token).GetAwaiter().GetResult();
            var resource = activation.CreateAsync(token).GetAwaiter().GetResult();
            if (resource.Instance is not IAnomalyImplementation implementation || implementation.ImplementationId != ImplementationId)
            { resource.Dispose(); throw new InvalidOperationException("异常模型工厂返回了不匹配的实现。"); }
            return resource;
        }
        public ILoadedAnomalyModel Load(AnomalyModelAsset asset, CancellationToken token = default)
        {
            var resource = Create(token);
            try { return new Owned(((IAnomalyImplementation)resource.Instance).Load(asset, token), resource); }
            catch { resource.Dispose(); throw; }
        }
    }
    private sealed class Trainer : Implementation, IAnomalyTrainer
    {
        internal Trainer(VisionAlgorithmDescriptor descriptor) : base(descriptor) { }
        public AnomalyModelAsset Train(IReadOnlyList<IImageSource> good, IReadOnlyList<int> sources, AnomalyTrainingOptions options, CancellationToken token = default)
        {
            using var resource = Create(token);
            return (resource.Instance as IAnomalyTrainer ?? throw new NotSupportedException("目录声明训练能力，但实现未提供训练接口。"))
                .Train(good, sources, options, token);
        }
    }
    private sealed class Owned : ILoadedAnomalyModel
    {
        private readonly ILoadedAnomalyModel _runtime; private VisionAlgorithmResource? _resource;
        internal Owned(ILoadedAnomalyModel runtime, VisionAlgorithmResource resource) { _runtime = runtime; _resource = resource; }
        public AnomalyModelAsset Asset => _runtime.Asset;
        public PatchAnomalyResult Inspect(IImageSource image, AnomalyDetectionOptions options, CancellationToken token = default)
            => _runtime.Inspect(image, options, token);
        public void Dispose()
        {
            var resource = Interlocked.Exchange(ref _resource, null); if (resource == null) return;
            try { _runtime.Dispose(); } finally { resource.Dispose(); }
        }
    }
}
