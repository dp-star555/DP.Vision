using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using DP.Vision.Algorithms;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using PixelRect = DP.Vision.Algorithms.PixelBounds;

namespace DP.Vision.OnnxDetection;

/// <summary>使用PP-OCRv4 DB模型生成保守的水平矩形候选，不认证旋转或透视文本。</summary>
public sealed class OnnxTextRegionDetector : ITextRegionDetector
{
    private readonly object _sync = new object();
    private readonly InferenceSession _session;
    private readonly string _input;
    private bool _disposed;

    /// <summary>加载可信的本地检测模型快照，不下载模型，也不跨任务接口暴露原生类型。</summary>
    /// <param name = "modelPath">可信的本地ONNX检测模型路径，内部加载实际模型字节。</param>
    public OnnxTextRegionDetector(string modelPath)
    {
        using var stream = File.OpenRead(modelPath);
        if (stream.Length < 1 || stream.Length > 128 * 1024 * 1024)
        {
            throw new ArgumentException("Detector model too large.");
        }

        var bytes = new byte[(int)stream.Length];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int n = stream.Read(bytes, offset, bytes.Length - offset);
            if (n == 0)
            {
                throw new EndOfStreamException();
            }

            offset += n;
        }

        using (var sha = SHA256.Create())
        {
            ModelIdentity = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 2 };
        _session = new InferenceSession(bytes, options);
        try
        {
            if (_session.InputMetadata.Count != 1 || _session.OutputMetadata.Count != 1)
            {
                throw new ArgumentException("Expected one DB input/output.");
            }

            var input = _session.InputMetadata.Single();
            _input = input.Key;
            if (
                input.Value.ElementType != typeof(float)
                || input.Value.Dimensions.Length != 4
                || input.Value.Dimensions[1] != 3
            )
            {
                throw new ArgumentException("Expected BGR NCHW detector input.");
            }
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public string ModelIdentity { get; }

    /// <inheritdoc/>
    public IReadOnlyList<PixelRect> Detect(IImageSource frame, CancellationToken token = default)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(OnnxTextRegionDetector));
            }

            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            token.ThrowIfCancellationRequested();
            if (frame.Info.Layout != EPixelLayout.Gray8 && frame.Info.Layout != EPixelLayout.Bgr24)
            {
                throw new NotSupportedException("DB detector requires Gray8 or Bgr24.");
            }

            int imageWidth = frame.Info.Width,
                imageHeight = frame.Info.Height,
                channels = frame.Info.Layout == EPixelLayout.Gray8 ? 1 : 3;
            double ratio = Math.Min(1, 736.0 / Math.Max(imageWidth, imageHeight));
            int width = Math.Max(32, (int)Math.Round(imageWidth * ratio / 32) * 32),
                height = Math.Max(32, (int)Math.Round(imageHeight * ratio / 32) * 32);
            using var raw = new Mat(
                imageHeight,
                imageWidth,
                channels == 1 ? MatType.CV_8UC1 : MatType.CV_8UC3
            );
            var pixels = new byte[frame.Info.ByteLength];
            frame.CopyTo(0, pixels, 0, pixels.Length);
            for (int row = 0; row < imageHeight; row++)
            {
                Marshal.Copy(pixels, row * frame.Info.Stride, raw.Ptr(row), imageWidth * channels);
            }

            using var bgr = new Mat();
            if (raw.Channels() == 1)
            {
                Cv2.CvtColor(raw, bgr, ColorConversionCodes.GRAY2BGR);
            }
            else
            {
                raw.CopyTo(bgr);
            }

            using var resized = new Mat();
            Cv2.Resize(bgr, resized, new Size(width, height));
            var source = new byte[width * height * 3];
            Marshal.Copy(resized.Data, source, 0, source.Length);
            var input = new float[width * height * 3];
            float[] mean =  { .485f, .456f, .406f },
                std =  { .229f, .224f, .225f };
            for (int i = 0; i < width * height; i++)
            {
                for (int c = 0; c < 3; c++)
                {
                    input[c * width * height + i] = (source[3 * i + c] / 255f - mean[c]) / std[c];
                }
            }

            var tensor = new DenseTensor<float>(input, new[] { 1, 3, height, width });
            token.ThrowIfCancellationRequested();
            using var outputs = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_input, tensor) });
            token.ThrowIfCancellationRequested();
            var output = outputs.First().AsTensor<float>();
            if (
                output.Dimensions.Length != 4
                || output.Dimensions[0] != 1
                || output.Dimensions[1] != 1
                || output.Dimensions[2] != height
                || output.Dimensions[3] != width
            )
            {
                throw new InvalidOperationException("Unexpected DB probability map.");
            }

            using var mask = new Mat(height, width, MatType.CV_8UC1);
            var probability = new float[width * height];
            for (int i = 0; i < probability.Length; i++)
            {
                float p = output.GetValue(i);
                if (float.IsNaN(p) || p < 0 || p > 1)
                {
                    throw new InvalidOperationException("Invalid DB probability.");
                }

                probability[i] = p;
                mask.Set(i / width, i % width, p > .3f ? (byte)255 : (byte)0);
            }

            Cv2.FindContours(
                mask,
                out Point[][] contours,
                out _,
                RetrievalModes.List,
                ContourApproximationModes.ApproxSimple
            );
            var regions = new List<PixelRect>();
            foreach (var contour in contours.OrderByDescending(c => Cv2.ContourArea(c)).Take(1000))
            {
                token.ThrowIfCancellationRequested();
                var box = Cv2.BoundingRect(contour);
                if (box.Width < 4 || box.Height < 3 || box.Width < box.Height * 1.3)
                {
                    continue;
                }

                var oriented = Cv2.MinAreaRect(contour);
                var vertices = oriented.Points();
                double longest = 0,
                    slope = 0;
                for (int i = 0; i < 4; i++)
                {
                    var a = vertices[i];
                    var b = vertices[(i + 1) % 4];
                    double dx = b.X - a.X,
                        dy = b.Y - a.Y,
                        length = dx * dx + dy * dy;
                    if (length > longest)
                    {
                        longest = length;
                        slope = Math.Abs(dy) / Math.Max(.001, Math.Abs(dx));
                    }
                }

                if (slope > Math.Tan(Math.PI / 12))
                {
                    continue;
                }

                using var area = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
                Cv2.FillPoly(area, new[] { contour }, Scalar.All(255));
                double sum = 0;
                int n = 0;
                for (int y = box.Y; y < box.Bottom; y++)
                {
                    for (int x = box.X; x < box.Right; x++)
                    {
                        if (area.At<byte>(y, x) != 0)
                        {
                            sum += probability[y * width + x];
                            n++;
                        }
                    }
                }

                if (n == 0 || sum / n < .6)
                {
                    continue;
                }

                double pad = box.Width * box.Height * 1.5 / (2.0 * (box.Width + box.Height));
                int left = Math.Max(0, (int)Math.Floor((box.Left - pad) * imageWidth / width)),
                    top = Math.Max(0, (int)Math.Floor((box.Top - pad) * imageHeight / height));
                int right = Math.Min(imageWidth, (int)Math.Ceiling((box.Right + pad) * imageWidth / width)),
                    bottom = Math.Min(
                        imageHeight,
                        (int)Math.Ceiling((box.Bottom + pad) * imageHeight / height)
                    );
                if (right - left < 4 || bottom - top < 4 || bottom - top > 512 || right - left > 6000)
                {
                    continue;
                }

                var candidate = new PixelRect(left, top, right - left, bottom - top);
                if (
                    regions.Any(r =>
                        Intersection(r, candidate)
                        > .7 * Math.Min((long)r.Width * r.Height, (long)candidate.Width * candidate.Height)
                    )
                )
                {
                    continue;
                }

                regions.Add(candidate);
                if (regions.Count == 64)
                {
                    break;
                }
            }

            return regions.OrderBy(r => r.Y).ThenBy(r => r.X).ToArray();
        }
    }

    private static long Intersection(PixelRect a, PixelRect b)
    {
        return Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X))
            * (long)Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _session.Dispose();
        }
    }
}
