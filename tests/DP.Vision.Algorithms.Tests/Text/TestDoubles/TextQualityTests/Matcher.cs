using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DP.Vision.Algorithms;
using DP.Vision.OpenCv;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DP.Vision.Algorithms.Tests;

public sealed partial class TextQualityTests
{
    private sealed class Matcher : ICharacterMatcher
    {
        internal int Calls;
        internal bool Missing;

        public IReadOnlyList<string?> Match(
            IReadOnlyList<CharacterPatch> patches,
            IReadOnlyCollection<string> keys,
            CancellationToken token = default
        )
        {
            Calls++;
            return patches.Select(_ => Missing ? null : "A").ToArray();
        }
    }
}
