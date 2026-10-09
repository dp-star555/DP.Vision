using System;
using System.Collections.Generic;
using System.Threading;
using DP.Vision;
using DP.Vision.Algorithms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Runtime.Tests;

/// <summary>任何普通Vision模型工厂都可登记，不要求Factory本身是厂商实现。</summary>
[TestClass]
public sealed class AnomalyCatalogTests
{
    /// <summary>目录捕获不创建SDK；模型运行实例拥有其工厂资源，两者恰好释放一次。</summary>
    [TestMethod]
    public void OrdinaryFactoryIsLazyAndItsResourceLivesUntilNativeModelDisposal()
    {
        int created = 0, disposed = 0, modelsDisposed = 0;
        var factory = VisionAlgorithmFactory<IAnomalyImplementation>.Stateless(() => { created++; return new Implementation(() => disposed++, () => modelsDisposed++); });
        var catalog = VisionAlgorithmCatalog.Compose(new[] { new Module(factory) });
        var providers = VisionAnomalyImplementations.FromCatalog(catalog);
        Assert.AreEqual(0, created); Assert.AreEqual(1, providers.Count); Assert.IsFalse(providers[0] is IAnomalyTrainer);
        var asset = new AnomalyModelAsset("test.native", "test.v1", 16, 16, .3, 3, "", new Dictionary<string, byte[]> { ["model.bin"] = new byte[] { 1 } });
        var runtime = providers[0].Load(asset);
        Assert.AreEqual(1, created); Assert.AreEqual(0, disposed);
        runtime.Dispose(); runtime.Dispose(); Assert.AreEqual(1, disposed); Assert.AreEqual(1, modelsDisposed);
    }
    private sealed class Module(IVisionAlgorithmFactory factory) : IVisionAlgorithmModule
    {
        public string ExtensionId => "test.native.module";
        public void Register(IVisionAlgorithmRegistration registration) => registration.Add(new VisionAlgorithmDescriptor("test.native", "test", "1", factory));
    }
    private sealed class Implementation(Action release, Action releaseModel) : IAnomalyImplementation, IDisposable
    {
        public string ImplementationId => "test.native"; public string DisplayName => "native";
        public ILoadedAnomalyModel Load(AnomalyModelAsset asset, CancellationToken token = default) => new Loaded(asset, releaseModel);
        public void Dispose() => release();
        private sealed class Loaded(AnomalyModelAsset asset, Action release) : ILoadedAnomalyModel
        {
            public AnomalyModelAsset Asset => asset;
            public PatchAnomalyResult Inspect(IImageSource image, AnomalyDetectionOptions options, CancellationToken token = default) => throw new NotImplementedException();
            public void Dispose() => release();
        }
    }
}
