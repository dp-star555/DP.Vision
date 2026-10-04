namespace DP.Vision.Algorithms;

/// <summary>定位输出向制作界面提供本帧坐标系，未检出时为空。</summary>
public interface IVisionCoordinateResult
{
    /// <summary>搜索原图身份。</summary>
    string FrameId { get; }
    /// <summary>本帧成功定位的坐标系；不能回退上一帧。</summary>
    VisionCoordinateSystem? CoordinateSystem { get; }
}
