using DP.Vision.Algorithms;
namespace External.Probe;
/// <summary>未被测试宿主引用的契约。</summary>
[VisionCapability("fixture.probe", "Test", "Private dependency probe")]
public interface IPrivateProbe
{
    /// <summary>私有库的版本值。</summary>
    int Read();
}
