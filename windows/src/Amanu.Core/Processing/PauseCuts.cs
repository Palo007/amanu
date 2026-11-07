namespace Amanu.Core.Processing;

/// <summary>
/// Where to cut a long track for recognition: at a pause, not inside a word.
/// A chunk is never longer than the maximum (the fixed 60 s of before), so the
/// memory bound of the model is the same. Level is the one of <see cref="SpeechLevels"/>.
/// </summary>
public static class PauseCuts
{
    /// <summary>A pause must be at least this long (300 ms).</summary>
    public const int PauseBlocks = 3;
    /// <summary>A pause counts when its level is under the track's floor plus this.</summary>
    public const double MarginDb = 10;

    /// <summary>
    /// The cut points, in blocks, ending with the track length. Each chunk is
    /// searched from <paramref name="searchFromBlocks"/> to <paramref name="maxBlocks"/>
    /// after its start for the quietest pause; with none, it is cut at the maximum.
    /// </summary>
    public static IReadOnlyList<int> Boundaries(IReadOnlyList<double> blocks, int maxBlocks, int searchFromBlocks)
    {
        if (maxBlocks <= 0 || searchFromBlocks < 0 || searchFromBlocks > maxBlocks) throw new ArgumentOutOfRangeException(nameof(maxBlocks));
        var count = blocks.Count;
        var result = new List<int>();
        if (count == 0) return result;
        var floor = SpeechLevels.Percentile(SpeechLevels.WindowLevels(blocks), 0.10);
        var limit = floor + MarginDb;
        var start = 0;
        while (count - start > maxBlocks)
        {
            var cut = start + maxBlocks;
            var best = double.MaxValue;
            for (var at = start + searchFromBlocks; at + PauseBlocks <= start + maxBlocks; at++)
            {
                var total = 0.0;
                for (var index = at; index < at + PauseBlocks; index++) total += blocks[index];
                var level = 10 * Math.Log10(total / PauseBlocks + 1e-10);
                if (level < limit && level < best)
                {
                    best = level;
                    cut = at + PauseBlocks / 2;
                }
            }
            result.Add(cut);
            start = cut;
        }
        result.Add(count);
        return result;
    }
}
