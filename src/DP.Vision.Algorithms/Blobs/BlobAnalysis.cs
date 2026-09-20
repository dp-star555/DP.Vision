using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DP.Vision.Algorithms;

/// <summary>固定阈值Blob参数；选择灰度闭区间内的像素，不进行隐式形态学处理。</summary>
public sealed class BlobOptions
{
    /// <summary>创建不可变参数。</summary>
    /// <param name="minimumGray">包含的最小灰度，0至255。</param>
    /// <param name="maximumGray">包含的最大灰度，0至255。</param>
    /// <param name="minimumArea">最小保留面积，单位原图像素。</param>
    /// <param name="eightConnected">true为8连通，false为4连通。</param>
    public BlobOptions(int minimumGray = 0, int maximumGray = 127, int minimumArea = 1, bool eightConnected = true)
    {
        if (minimumGray < 0 || maximumGray > 255 || minimumGray > maximumGray || minimumArea < 1)
            throw new ArgumentOutOfRangeException(nameof(minimumGray));
        MinimumGray = minimumGray; MaximumGray = maximumGray;
        MinimumArea = minimumArea; EightConnected = eightConnected;
    }
    /// <summary>灰度下界，包含。</summary>
    public int MinimumGray { get; }
    /// <summary>灰度上界，包含。</summary>
    public int MaximumGray { get; }
    /// <summary>最小像素面积。</summary>
    public int MinimumArea { get; }
    /// <summary>是否8连通。</summary>
    public bool EightConnected { get; }
}

/// <summary>单个连通域的精确事实，无产品裁决。</summary>
public sealed class BlobObservation
{
    /// <summary>从精确Region建立面积和像素中心质心。</summary>
    /// <param name="region">原图坐标下的非空Region，保留孔洞。</param>
    /// <param name="token">特征计算取消。</param>
    public BlobObservation(RegionGeometry region, CancellationToken token = default)
    {
        Region = region ?? throw new ArgumentNullException(nameof(region));
        if (region.AreaPixels == 0) throw new ArgumentException("Blob must not be empty.", nameof(region));
        double x = 0, y = 0;
        foreach (var run in region.Runs)
        {
            token.ThrowIfCancellationRequested();
            long length = (long)run.EndExclusive - run.Start;
            x += (run.Start + (double)run.EndExclusive) / 2 * length;
            y += (run.Row + .5) * length;
        }
        Centroid = new PointD(x / region.AreaPixels, y / region.AreaPixels);
        Features = new BlobShapeFeatures(region, Centroid, token);
    }
    /// <summary>栅格周长、圆度和面积矩等效椭圆特征。</summary>
    public BlobShapeFeatures Features { get; }
    /// <summary>精确Region。</summary>
    public RegionGeometry Region { get; }
    /// <summary>原图像素面积。</summary>
    public long Area => Region.AreaPixels;
    /// <summary>原图像素边界坐标下的质心，单像素中心含0.5偏移。</summary>
    public PointD Centroid { get; }
}

/// <summary>完成的Blob分析；空集合也是正常完成。</summary>
public sealed class BlobAnalysisResult
{
    /// <summary>复制分析事实。</summary>
    /// <param name="frameId">输入图像身份。</param>
    /// <param name="blobs">按首个像素行列排序的Blob。</param>
    public BlobAnalysisResult(string frameId, IEnumerable<BlobObservation> blobs)
    {
        if (string.IsNullOrWhiteSpace(frameId)) throw new ArgumentException("Frame identity required.", nameof(frameId));
        var copy = (blobs ?? throw new ArgumentNullException(nameof(blobs))).ToArray();
        if (copy.Any(b => b == null)) throw new ArgumentException("Null blob.", nameof(blobs));
        FrameId = frameId; Blobs = Array.AsReadOnly(copy);
    }
    /// <summary>本次检测使用的定位坐标系；未绑定时为空，原有Region/质心始终为图像坐标。</summary>
    public LocatedCoordinateSystem? CoordinateSystem { get; private set; }
    /// <summary>与Blobs同序的双坐标质心；未绑定时为空。</summary>
    public IReadOnlyList<LocatedPoint>? LocatedCentroids { get; private set; }
    /// <summary>附加同帧定位表达，不改变原图证据或原结果。</summary><param name="system">定位。</param><returns>独立结果。</returns>
    public BlobAnalysisResult InCoordinates(LocatedCoordinateSystem system)
    {
        if (system == null) throw new ArgumentNullException(nameof(system));
        if (system.FrameId != FrameId) throw new InvalidOperationException("Result coordinate frame mismatch.");
        return new BlobAnalysisResult(FrameId, Blobs) { CoordinateSystem = system, LocatedCentroids = Array.AsReadOnly(Blobs.Select(b => system.Locate(b.Centroid)).ToArray()) };
    }
    /// <summary>输入帧身份。</summary>
    public string FrameId { get; }
    /// <summary>完成状态，与是否找到Blob无关。</summary>
    public EAlgorithmStatus Status => EAlgorithmStatus.Completed;
    /// <summary>不可变观测列表。</summary>
    public IReadOnlyList<BlobObservation> Blobs { get; }
    /// <summary>连通域数量。</summary>
    public int Count => Blobs.Count;
}

/// <summary>只读原图的连通域分析能力。</summary>
public interface IBlobAnalyzer
{
    /// <summary>分析矩形范围；不支持的像素格式或范围明确抛错，不自动裁剪。</summary>
    /// <param name="frame">借用输入帧。</param>
    /// <param name="bounds">原图半开矩形范围。</param>
    /// <param name="options">固定阈值与连通性。</param>
    /// <param name="token">协作取消。</param>
    /// <param name="regionMask">可选精确原图Region，与矩形取交集；孔洞和排除像素不参与分析。</param>
    /// <returns>不拥有图像租约的不可变事实。</returns>
    BlobAnalysisResult Analyze(ImageFrame frame, PixelBounds bounds, BlobOptions options, CancellationToken token = default, RegionGeometry? regionMask = null);
}
