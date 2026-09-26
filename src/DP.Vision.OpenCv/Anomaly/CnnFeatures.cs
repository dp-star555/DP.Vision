using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace DP.Vision.OpenCv;

/// <summary>
/// 用OpenCV DNN运行ONNX卷积骨干网络，取1/4与1/8两级中层特征（PatchCore做法：3×3邻域平均后，1/8级上采样到1/4级网格再拼接）。
/// 输入按纸色归一化并放大<see cref = "Scale"/>倍，按ImageNet均值/方差标准化，灰度复制为三通道。
/// 模型须有两个输出（1/4与1/8级特征），例如从PP-OCR检测模型截取的骨干网络。
/// </summary>
internal sealed class CnnFeatures : IDisposable
{
    private static readonly float[] Mean = { .485f, .456f, .406f };
    private static readonly float[] Std = { .229f, .224f, .225f };
    private readonly Net _net;
    private readonly string[] _outputs;
    private readonly object _gate = new object();

    internal CnnFeatures(string path, double scale)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Backbone model not found.", path);
        }

        if (double.IsNaN(scale) || scale < 1 || scale > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        byte[] bytes = File.ReadAllBytes(path);
        using (var sha = SHA256.Create())
        {
            Hash = BitConverter
                .ToString(sha.ComputeHash(bytes))
                .Replace("-", "")
                .Substring(0, 16)
                .ToLowerInvariant();
        }

        _net =
            CvDnn.ReadNetFromOnnx(path)
            ?? throw new InvalidDataException("Backbone model could not be loaded.");
        _outputs = (_net.GetUnconnectedOutLayersNames() ?? Array.Empty<string?>())
            .Where(n => n != null)
            .Select(n => n!)
            .ToArray();
        if (_outputs.Length != 2)
        {
            _net.Dispose();
            throw new InvalidDataException(
                "Backbone must expose exactly two outputs (1/4 and 1/8 resolution features)."
            );
        }

        Scale = scale;
    }

    /// <summary>输入放大倍数。</summary>
    internal double Scale { get; }

    /// <summary>模型文件SHA256前16位。</summary>
    internal string Hash { get; }

    /// <summary>写入模型的特征来源标识；模型文件或放大倍数不同即不兼容。</summary>
    internal string Source => $"cnn:{Hash}@{Scale:0.##}";

    /// <summary>1/4级网格一个单元对应的原图像素边长。</summary>
    internal double Cell => 4 / Scale;

    /// <summary>
    /// 提取特征网格（行优先，每单元<paramref name = "dimensions"/>维）。网格只覆盖原裁图范围（不含补边）。
    /// </summary>
    /// <param name = "gray">灰度裁图。</param>
    /// <param name = "gridWidth">网格宽度。</param>
    /// <param name = "gridHeight">网格高度。</param>
    /// <param name = "dimensions">每单元特征维数。</param>
    /// <param name = "blank">每单元周围约9×9原图像素内是否全为纸白（墨量低于12%）。</param>
    internal float[] Extract(
        Mat gray,
        out int gridWidth,
        out int gridHeight,
        out int dimensions,
        out bool[] blank
    )
    {
        // 按纸色（98%分位）拉伸，抵消整体亮度差异。
        using var hist = new Mat();
        Cv2.CalcHist(new[] { gray }, new[] { 0 }, null, hist, 1, new[] { 256 }, new[] { new Rangef(0, 256) });
        double total = gray.Rows * (double)gray.Cols,
            seen = 0;
        int paper = 255;
        for (int v = 0; v < 256; v++)
        {
            seen += hist.At<float>(v);
            if (seen >= total * .98)
            {
                paper = v;
                break;
            }
        }

        using var stretched = new Mat();
        gray.ConvertTo(stretched, MatType.CV_32F, 1.0 / Math.Max(20, paper));
        Cv2.Min(stretched, 1.0, stretched);
        int width = (int)Math.Round(gray.Cols * Scale),
            height = (int)Math.Round(gray.Rows * Scale),
            paddedWidth = (width + 7) / 8 * 8,
            paddedHeight = (height + 7) / 8 * 8;
        using var resized = new Mat();
        Cv2.Resize(stretched, resized, new Size(width, height), 0, 0, InterpolationFlags.Linear);
        using var padded = new Mat();
        Cv2.CopyMakeBorder(
            resized,
            padded,
            0,
            paddedHeight - height,
            0,
            paddedWidth - width,
            BorderTypes.Constant,
            Scalar.All(1)
        );
        var channels = Enumerable
            .Range(0, 3)
            .Select(c =>
            {
                var m = new Mat();
                padded.ConvertTo(m, MatType.CV_32F, 1.0 / Std[c], -Mean[c] / Std[c]);
                return m;
            })
            .ToArray();
        using var merged = new Mat();
        Cv2.Merge(channels, merged);
        foreach (var c in channels)
        {
            c.Dispose();
        }

        // 注意：多维Mat不能用SetArray/GetArray读写（OpenCvSharp会静默不复制），这里用BlobFromImage与指针复制。
        using var blob = CvDnn.BlobFromImage(
            merged,
            1.0,
            new Size(paddedWidth, paddedHeight),
            new Scalar(),
            false,
            false
        );
        var outs = new[] { new Mat(), new Mat() };
        try
        {
            lock (_gate)
            {
                _net.SetInput(blob);
                _net.Forward(outs, _outputs);
            }

            var (fine, fineC, fineH, fineW) = Planes(outs[0]);
            var (coarse, coarseC, coarseH, coarseW) = Planes(outs[1]);
            if (fineH * 4 != paddedHeight || coarseH * 8 != paddedHeight)
            {
                (fine, fineC, fineH, fineW, coarse, coarseC, coarseH, coarseW) = (
                    coarse,
                    coarseC,
                    coarseH,
                    coarseW,
                    fine,
                    fineC,
                    fineH,
                    fineW
                );
            }

            gridWidth = Math.Min(fineW, (int)Math.Ceiling(width / 4.0));
            gridHeight = Math.Min(fineH, (int)Math.Ceiling(height / 4.0));
            using (var darkest = new Mat())
            using (var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(9, 9)))
            {
                Cv2.Erode(stretched, darkest, kernel);
                blank = new bool[gridWidth * gridHeight];
                for (int y = 0; y < gridHeight; y++)
                {
                    for (int x = 0; x < gridWidth; x++)
                    {
                        int cx = Math.Min(gray.Cols - 1, (int)((x + .5) * Cell)),
                            cy = Math.Min(gray.Rows - 1, (int)((y + .5) * Cell));
                        blank[y * gridWidth + x] = darkest.At<float>(cy, cx) > .88f;
                    }
                }
            }

            dimensions = fineC + coarseC;
            var grid = new float[gridWidth * gridHeight * dimensions];
            Fill(fine, fineC, fineH, fineW, fineW, fineH, grid, gridWidth, gridHeight, dimensions, 0);
            Fill(
                coarse,
                coarseC,
                coarseH,
                coarseW,
                fineW,
                fineH,
                grid,
                gridWidth,
                gridHeight,
                dimensions,
                fineC
            );
            return grid;
        }
        finally
        {
            foreach (var o in outs)
            {
                o.Dispose();
            }
        }
    }

    /// <summary>NCHW输出复制为按通道的浮点数组。</summary>
    private static (float[] Data, int Channels, int Height, int Width) Planes(Mat output)
    {
        int c = output.Size(1),
            h = output.Size(2),
            w = output.Size(3);
        var data = new float[c * h * w];
        Marshal.Copy(output.Data, data, 0, data.Length);
        return (data, c, h, w);
    }

    /// <summary>每通道3×3平均，缩放到目标网格后写入网格特征的指定通道偏移。</summary>
    private static void Fill(
        float[] data,
        int channels,
        int height,
        int width,
        int targetWidth,
        int targetHeight,
        float[] grid,
        int gridWidth,
        int gridHeight,
        int dimensions,
        int offset
    )
    {
        using var plane = new Mat(height, width, MatType.CV_32F);
        using var pooled = new Mat();
        using var scaled = new Mat();
        var row = new float[targetWidth * targetHeight];
        for (int c = 0; c < channels; c++)
        {
            Marshal.Copy(data, c * height * width, plane.Data, height * width);
            Cv2.Blur(plane, pooled, new Size(3, 3), new Point(-1, -1), BorderTypes.Replicate);
            if (width == targetWidth && height == targetHeight)
            {
                Marshal.Copy(pooled.Data, row, 0, row.Length);
            }
            else
            {
                Cv2.Resize(
                    pooled,
                    scaled,
                    new Size(targetWidth, targetHeight),
                    0,
                    0,
                    InterpolationFlags.Linear
                );
                Marshal.Copy(scaled.Data, row, 0, row.Length);
            }

            for (int y = 0; y < gridHeight; y++)
            {
                for (int x = 0; x < gridWidth; x++)
                {
                    grid[(y * gridWidth + x) * dimensions + offset + c] = row[y * targetWidth + x];
                }
            }
        }
    }

    public void Dispose()
    {
        _net.Dispose();
    }
}
