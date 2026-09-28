using DP.Vision.Algorithms;
using OpenCvSharp;

namespace DP.Vision.OpenCv;

public sealed partial class OpenCvCharacterAnomalyDetector
{
    /// <summary>收集到的一个训练/归一化字符：所在行序号、行内序号、模型键、共享灰度图、行几何及分割单元。</summary>
    private sealed class Sample
    {
        internal Sample(int sample, int index, string key, Mat gray, CharacterLine line, PixelBounds cell)
        {
            SampleIndex = sample;
            Index = index;
            Key = key;
            Gray = gray;
            Line = line;
            Cell = cell;
        }

        /// <summary>所在行序号。</summary>
        internal int SampleIndex { get; }

        /// <summary>行内字符序号。</summary>
        internal int Index { get; }

        /// <summary>模型键。</summary>
        internal string Key { get; }

        /// <summary>所在原图的灰度图（按原图对象共享，不由本对象拥有）。</summary>
        internal Mat Gray { get; }

        /// <summary>所在行几何。</summary>
        internal CharacterLine Line { get; }

        /// <summary>分割单元（原图坐标）。</summary>
        internal PixelBounds Cell { get; }
    }
}
