using DP.Vision.Algorithms;
using External.Probe;
namespace External.ProbeEngine;
/// <summary>两个独立构建的引擎分别使用私有依赖的不同版本。</summary>
public sealed class ProbeModule : IVisionAlgorithmModule
{
#if PROBE_V1
    /// <inheritdoc/>
    public string ExtensionId => "fixture.probe.v1";
#else
    /// <inheritdoc/>
    public string ExtensionId => "fixture.probe.v2";
#endif
    /// <inheritdoc/>
    public void Register(IVisionAlgorithmRegistration registrations) => registrations.Add(new VisionAlgorithmDescriptor(
        ExtensionId, "Probe", "1", VisionAlgorithmFactory<IPrivateProbe>.Stateless(() => new Probe())));
    private sealed class Probe : IPrivateProbe
    {
        private readonly int _value = External.PrivateDependency.Value.Read();
        public int Read() => _value;
    }
}
