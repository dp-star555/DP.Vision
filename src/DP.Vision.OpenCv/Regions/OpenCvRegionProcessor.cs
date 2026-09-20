using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

/// <summary>有界精确Region算子，明确零背景边界，不以外接框替代Region。</summary>
public sealed class OpenCvRegionProcessor : IRegionProcessor
{
    /// <inheritdoc/>
    public RegionAnalysisResult Threshold(ImageFrame frame, PixelBounds bounds, int minimumGray, int maximumGray, RegionGeometry? mask = null, CancellationToken token = default)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));
        if (!bounds.Fits(frame.Image) || minimumGray < 0 || maximumGray > 255 || minimumGray > maximumGray)
            throw new ArgumentOutOfRangeException(nameof(bounds));
        if (!CvPixels.Supports(frame.Image)) throw new NotSupportedException("Convert Gray16 explicitly first.");
        if ((long)frame.Image.Info.Width * frame.Image.Info.Height > 16777216) throw new ArgumentException("Region pixel budget exceeded.");
        InspectionMask.Validate(mask, frame.Image); token.ThrowIfCancellationRequested();
        using var gray = CvPixels.Gray(frame.Image);
        using var roi = new Mat(gray, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        using var binary = new Mat(); Cv2.InRange(roi, new Scalar(minimumGray), new Scalar(maximumGray), binary);
        var region = Read(binary, bounds.X, bounds.Y, token);
        if (mask != null) region = RegionAnalysisResult.Intersect(region, mask, token);
        return new RegionAnalysisResult(frame.FrameId, frame.Image.Info.Width, frame.Image.Info.Height, region);
    }

    /// <inheritdoc/>
    public RegionAnalysisResult Morphology(RegionAnalysisResult input, ERegionMorphology operation, int radius = 1, ERegionKernel kernel = ERegionKernel.Rectangle, CancellationToken token = default)
    {
        if (input == null) throw new ArgumentNullException(nameof(input));
        if (!Enum.IsDefined(typeof(ERegionMorphology), operation) || !Enum.IsDefined(typeof(ERegionKernel), kernel) || radius < 0 || radius > 31)
            throw new ArgumentOutOfRangeException(nameof(operation));
        token.ThrowIfCancellationRequested();
        using var binary = new Mat(input.Height, input.Width, MatType.CV_8UC1, Scalar.All(0));
        foreach (var run in input.Region.Runs)
        {
            token.ThrowIfCancellationRequested();
            using var strip = new Mat(binary, new Rect(run.Start, run.Row, run.EndExclusive - run.Start, 1)); strip.SetTo(Scalar.All(255));
        }
        using var output = new Mat();
        if (operation == ERegionMorphology.FillHoles)
        {
            using var padded = new Mat();
            Cv2.CopyMakeBorder(binary, padded, 1, 1, 1, 1, BorderTypes.Constant, Scalar.All(0));
            Cv2.FloodFill(padded, new Point(0, 0), Scalar.All(128));
            using var interior = new Mat(padded, new Rect(1, 1, input.Width, input.Height));
            Cv2.InRange(interior, Scalar.All(128), Scalar.All(128), output); Cv2.BitwiseNot(output, output);
        }
        else
        {
            var shape = kernel == ERegionKernel.Rectangle ? MorphShapes.Rect : kernel == ERegionKernel.Ellipse ? MorphShapes.Ellipse : MorphShapes.Cross;
            using var element = Cv2.GetStructuringElement(shape, new Size(radius * 2 + 1, radius * 2 + 1));
            var mode = operation == ERegionMorphology.Dilate ? MorphTypes.Dilate : operation == ERegionMorphology.Erode ? MorphTypes.Erode
                : operation == ERegionMorphology.Open ? MorphTypes.Open : MorphTypes.Close;
            Cv2.MorphologyEx(binary, output, mode, element, borderType: BorderTypes.Constant, borderValue: Scalar.All(0));
        }
        token.ThrowIfCancellationRequested();
        var result = new RegionAnalysisResult(input.FrameId, input.Width, input.Height, Read(output, 0, 0, token));
        return input.CoordinateSystem is null ? result : result.InCoordinates(input.CoordinateSystem);
    }

    private static RegionGeometry Read(Mat mask, int offsetX, int offsetY, CancellationToken token)
    {
        var runs = new List<RegionRun>(); var row = new byte[mask.Cols];
        for (int y = 0; y < mask.Rows; y++)
        {
            token.ThrowIfCancellationRequested(); Marshal.Copy(mask.Ptr(y), row, 0, row.Length);
            int x = 0;
            while (x < row.Length)
            {
                if (row[x] == 0) { x++; continue; }
                int start = x++; while (x < row.Length && row[x] != 0) x++;
                if (runs.Count >= 2000000) throw new InvalidOperationException("Region run budget exceeded.");
                runs.Add(new RegionRun(y + offsetY, start + offsetX, x + offsetX));
            }
        }
        return new RegionGeometry(runs);
    }
}
