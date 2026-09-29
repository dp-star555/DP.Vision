using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DP.Vision.OnnxDetection;

/// <summary>PP-OCRv4 DB检测模型任务：加载、契约校验、推理与证据返回。</summary>
/// <remarks>本类不引用OpenCV，也不产生候选框或业务阈值；调用与释放串行执行，取消是协作式的。</remarks>
public sealed class PPOcrDetectionTask : IDisposable
{
    private readonly object _sync = new object();
    private readonly InferenceSession _session;
    private readonly string _input;
    private bool _disposed;

    /// <summary>加载可信的本地检测模型快照，不下载模型，也不跨任务接口暴露原生类型。</summary>
    /// <param name = "modelPath">可信的本地ONNX检测模型路径，内部加载实际模型字节。</param>
    public PPOcrDetectionTask(string modelPath)
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

    /// <summary>用于创建会话的实际模型字节的SHA256。</summary>
    public string ModelIdentity { get; }

    /// <summary>执行一次DB推理，返回脱离会话的概率图快照。</summary>
    /// <param name = "input">调用者准备好的连续NCHW输入及显式几何映射。</param>
    /// <param name = "token">协作式取消标记。</param>
    /// <returns>模型身份、概率图与映射元数据；不包含候选框。</returns>
    public PPOcrDetectionOutput Detect(PPOcrDetectionInput input, CancellationToken token = default)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PPOcrDetectionTask));
            }

            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            token.ThrowIfCancellationRequested();
            int width = input.Width,
                height = input.Height;
            var tensor = new DenseTensor<float>(input.CopyValues(), new[] { 1, 3, height, width });
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

            long count = (long)width * height;
            if (count > PPOcrDetectionOutput.MaxProbabilityCount)
            {
                throw new ArgumentOutOfRangeException(nameof(input), "Probability map exceeds the supported budget.");
            }

            var probability = new float[(int)count];
            for (int i = 0; i < probability.Length; i++)
            {
                float p = output.GetValue(i);
                if (float.IsNaN(p) || p < 0 || p > 1)
                {
                    throw new InvalidOperationException("Invalid DB probability.");
                }

                probability[i] = p;
            }

            return new PPOcrDetectionOutput(
                ModelIdentity,
                width,
                height,
                input.ImageWidth,
                input.ImageHeight,
                probability
            );
        }
    }

    /// <summary>等待正在进行的推理，并只释放一次原生会话。</summary>
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
