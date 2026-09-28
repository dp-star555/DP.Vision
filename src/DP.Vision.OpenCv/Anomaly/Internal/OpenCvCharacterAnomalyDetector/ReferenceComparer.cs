using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DP.Vision.OpenCv;

public sealed partial class OpenCvCharacterAnomalyDetector
{
    /// <summary>按对象引用比较原图：同一原图对象即同一来源。</summary>
    private sealed class ReferenceComparer : IEqualityComparer<IImageSource>
    {
        internal static readonly ReferenceComparer Instance = new ReferenceComparer();

        public bool Equals(IImageSource? x, IImageSource? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(IImageSource obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}
