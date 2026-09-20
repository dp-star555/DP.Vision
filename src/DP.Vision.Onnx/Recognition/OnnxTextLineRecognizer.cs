using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using DP.Vision.Algorithms;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PixelRect = DP.Vision.Algorithms.PixelBounds;

namespace DP.Vision.Onnx;

/// <summary>带内嵌字典和可注入任务级预处理器的CPU PP-OCRv4识别器。</summary>
/// <remarks>调用与释放串行执行；取消是协作式的，不强制中断原生推理。</remarks>
public sealed class OnnxTextLineRecognizer : ITextLineRecognizer
{
    private readonly object _sync = new object();
    private readonly ITextLinePreprocessor _preprocessor;
    private readonly InferenceSession _session;
    private readonly string[] _dictionary;
    private readonly string _input;
    private bool _disposed;

    /// <summary>加载本地模型快照，并验证字典与输出兼容性。</summary>
    /// <param name = "modelPath">带内嵌字符元数据的PP-OCRv4识别ONNX模型路径。</param>
    /// <param name = "preprocessor">宿主选择的图像预处理实现，不由本识别器拥有。</param>
    /// <param name = "expectedSha256">可选的预期模型哈希，不匹配时拒绝加载。</param>
    public OnnxTextLineRecognizer(
        string modelPath,
        ITextLinePreprocessor preprocessor,
        string? expectedSha256 = null
    )
    {
        _preprocessor = preprocessor ?? throw new ArgumentNullException(nameof(preprocessor));
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            throw new ArgumentException("Model path required.", nameof(modelPath));
        }

        using var stream = File.OpenRead(modelPath);
        if (stream.Length < 1 || stream.Length > 256 * 1024 * 1024)
        {
            throw new ArgumentException("Model size outside supported limit.", nameof(modelPath));
        }

        var bytes = new byte[checked((int)stream.Length)];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }

        using (var sha = SHA256.Create())
        {
            ModelSha256 = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        if (
            expectedSha256 != null
            && !string.Equals(expectedSha256, ModelSha256, StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new ArgumentException("Model SHA256 mismatch.", nameof(expectedSha256));
        }

        using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 2 };
        _session = new InferenceSession(bytes, options);
        try
        {
            if (_session.InputMetadata.Count != 1 || _session.OutputMetadata.Count != 1)
            {
                throw new ArgumentException("Expected one recognition input/output.");
            }

            var input = _session.InputMetadata.Single();
            _input = input.Key;
            var dims = input.Value.Dimensions;
            if (
                input.Value.ElementType != typeof(float)
                || dims.Length != 4
                || (dims[0] > 0 && dims[0] != 1)
                || dims[1] != 3
                || (dims[2] > 0 && dims[2] != 48)
                || dims[3] > 0
            )
            {
                throw new ArgumentException("Expected dynamic-width float NCHW PP-OCRv4 input.");
            }

            if (
                !_session.ModelMetadata.CustomMetadataMap.TryGetValue("character", out string? characters)
                || string.IsNullOrEmpty(characters)
            )
            {
                throw new ArgumentException("Embedded character dictionary is required.");
            }

            _dictionary = new[] { "" }
                .Concat(characters.Split('\n').Select(s => s.TrimEnd('\r')))
                .Concat(new[] { " " })
                .ToArray();
            if (_dictionary.Skip(1).Any(string.IsNullOrEmpty))
            {
                throw new ArgumentException("Invalid embedded dictionary.");
            }

            var output = _session.OutputMetadata.Single().Value;
            if (
                output.ElementType != typeof(float)
                || output.Dimensions.Length != 3
                || output.Dimensions[2] != _dictionary.Length
            )
            {
                throw new ArgumentException("Dictionary/output class mismatch.");
            }
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    /// <summary>用于创建会话的实际模型字节的SHA256。</summary>
    public string ModelSha256 { get; }

    /// <inheritdoc/>
    public TextLineRecognition Recognize(IImageSource frame, PixelRect bounds, CancellationToken token)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(OnnxTextLineRecognizer));
            }

            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (!bounds.Fits(frame))
            {
                throw new ArgumentException("Line outside image.", nameof(bounds));
            }

            token.ThrowIfCancellationRequested();
            var input = _preprocessor.Prepare(frame, bounds, token);
            var tensor = new DenseTensor<float>(input.CopyValues(), new[] { 1, 3, 48, input.Width });
            token.ThrowIfCancellationRequested();
            using var outputs = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_input, tensor) });
            token.ThrowIfCancellationRequested();
            var prediction = outputs.First().AsTensor<float>();
            if (
                prediction.Dimensions.Length != 3
                || prediction.Dimensions[0] != 1
                || prediction.Dimensions[1] < 1
                || prediction.Dimensions[2] != _dictionary.Length
            )
            {
                throw new InvalidOperationException("Unexpected recognition output.");
            }

            var steps = new CtcStep[prediction.Dimensions[1]];
            for (int t = 0; t < steps.Length; t++)
            {
                token.ThrowIfCancellationRequested();
                int best = 0;
                float confidence = -1;
                double sum = 0;
                for (int c = 0; c < _dictionary.Length; c++)
                {
                    float value = prediction.GetValue(t * _dictionary.Length + c);
                    if (float.IsNaN(value) || value < 0 || value > 1)
                    {
                        throw new InvalidOperationException("Expected probabilities, not logits.");
                    }

                    sum += value;
                    if (value > confidence)
                    {
                        confidence = value;
                        best = c;
                    }
                }

                if (Math.Abs(sum - 1) > .01)
                {
                    throw new InvalidOperationException("Recognition probabilities are not normalized.");
                }

                steps[t] = new CtcStep(best, confidence);
            }

            return new TextLineRecognition(
                bounds,
                ModelSha256,
                input.Width,
                input.ContentWidth,
                steps,
                CtcDecoder.Decode(steps, _dictionary)
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
