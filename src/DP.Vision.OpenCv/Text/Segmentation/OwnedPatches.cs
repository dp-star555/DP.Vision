using System;
using System.Collections.Generic;
using DP.Vision.Algorithms;

namespace DP.Vision.OpenCv;

internal sealed class OwnedPatches : IDisposable
{
    private readonly List<CharacterPatch> _patches = new List<CharacterPatch>();
    internal int Count => _patches.Count;

    internal void Add(CharacterPatch patch)
    {
        _patches.Add(patch);
    }

    internal CharacterPatch[] Detach()
    {
        var result = _patches.ToArray();
        _patches.Clear();
        return result;
    }

    public void Dispose()
    {
        foreach (var patch in _patches)
        {
            patch.Dispose();
        }

        _patches.Clear();
    }
}
