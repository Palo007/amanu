namespace Amanu.Core.Processing;

/// <summary>
/// Skip silence for the copy recognition hears (roadmap-speed step 6).
/// A long stretch that stays at the track's own noise floor is left out of the
/// ASR copy. Short pauses stay, and every kept stretch keeps a pad of audio on
/// both sides, so a word at the edge is not cut. The level is the one of
/// <see cref="SpeechLevels"/>, and "quiet" is relative to the track's own floor:
/// a quiet speaker is far above his own floor, so he is never left out.
/// </summary>
public static class SilenceSkip
{
    /// <summary>A block is quiet when its level is under the floor plus this.</summary>
    public const double MarginDb = 6;
    /// <summary>A quiet run must be at least this long (5 s) to be left out.</summary>
    public const int MinRunBlocks = 50;
    /// <summary>Kept on each side of a left-out run (1 s).</summary>
    public const int PadBlocks = 10;

    /// <summary>
    /// The block ranges [From, To) to keep, in reading order. Everything not
    /// listed is a quiet run of at least <see cref="MinRunBlocks"/> blocks
    /// (minus the pads). Returns the whole track when nothing can be left out.
    /// </summary>
    public static IReadOnlyList<(int From, int To)> KeepBlocks(IReadOnlyList<double> blocks)
    {
        var count = blocks.Count;
        if (count == 0) return [];
        var levels = SpeechLevels.WindowLevels(blocks);
        var floor = SpeechLevels.Percentile(levels, 0.10);
        var limit = floor + MarginDb;
        var keep = new List<(int From, int To)>();
        var start = 0;
        var index = 0;
        while (index < count)
        {
            if (levels[index] >= limit) { index++; continue; }
            var end = index;
            while (end < count && levels[end] < limit) end++;
            if (end - index >= MinRunBlocks)
            {
                // Pads stay with the audio around the run; at the track's edges there is nothing to pad.
                var cutFrom = index == 0 ? 0 : index + PadBlocks;
                var cutTo = end == count ? count : end - PadBlocks;
                if (cutFrom < start) cutFrom = start;
                if (cutTo > cutFrom)
                {
                    if (cutFrom > start) keep.Add((start, cutFrom));
                    start = cutTo;
                }
            }
            index = end;
        }
        if (start < count) keep.Add((start, count));
        return keep;
    }

    /// <summary>Maps a time on the shortened track back to the original track.</summary>
    public sealed class TimeMap
    {
        private readonly long[] shortStart;
        private readonly long[] originalStart;
        private readonly long[] length;

        public TimeMap(IReadOnlyList<(int From, int To)> keep, int blockMs)
        {
            shortStart = new long[keep.Count];
            originalStart = new long[keep.Count];
            length = new long[keep.Count];
            long at = 0;
            for (var index = 0; index < keep.Count; index++)
            {
                shortStart[index] = at;
                originalStart[index] = (long)keep[index].From * blockMs;
                length[index] = (long)(keep[index].To - keep[index].From) * blockMs;
                at += length[index];
            }
            ShortLengthMs = at;
        }

        public long ShortLengthMs { get; }

        public long ToOriginal(long shortMs)
        {
            if (shortStart.Length == 0) return shortMs;
            var low = 0;
            var high = shortStart.Length - 1;
            while (low < high)
            {
                var middle = (low + high + 1) / 2;
                if (shortStart[middle] <= shortMs) low = middle; else high = middle - 1;
            }
            return originalStart[low] + Math.Min(Math.Max(0, shortMs - shortStart[low]), length[low]);
        }
    }
}
