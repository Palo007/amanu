using Amanu.Core.Processing;

namespace Amanu.Core.Tests;

public sealed class SilenceSkipTests
{
    /// <summary>Mean-square blocks: <paramref name="seconds"/> at a level in dBFS.</summary>
    private static double[] Level(double seconds, double db, int seed = 1)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, (int)(seconds * 10)).Select(_ => Math.Pow(10, (db + random.NextDouble() - 0.5) / 10)).ToArray();
    }

    private static double[] Join(params double[][] parts) => parts.SelectMany(part => part).ToArray();

    [Fact]
    public void LongNoiseRunIsLeftOutWithPads()
    {
        var blocks = Join(Level(10, -45), Level(30, -70), Level(10, -45));
        var keep = SilenceSkip.KeepBlocks(blocks);
        Assert.Equal(2, keep.Count);
        // 10 s speech + 1 s pad; 1 s pad + 10 s speech.
        Assert.InRange(keep[0].To, 108, 112);
        Assert.InRange(keep[1].From, 388, 392);
        Assert.Equal(blocks.Length, keep[1].To);
    }

    [Fact]
    public void ShortPauseStays()
    {
        var blocks = Join(Level(10, -45), Level(4, -70), Level(10, -45));
        Assert.Equal([(0, blocks.Length)], SilenceSkip.KeepBlocks(blocks));
    }

    [Fact]
    public void QuietSpeakerIsNeverLeftOut()
    {
        // Speech at -45 dBFS over a -70 dBFS floor, with pauses of 3 s: all kept.
        var blocks = Join(Level(8, -45), Level(3, -70), Level(8, -45), Level(3, -70), Level(8, -45));
        Assert.Equal([(0, blocks.Length)], SilenceSkip.KeepBlocks(blocks));
        // Even a speaker only 12 dB over the floor stays.
        var faint = Join(Level(8, -58), Level(40, -70), Level(8, -58));
        var keep = SilenceSkip.KeepBlocks(faint);
        Assert.Equal(0, keep[0].From);
        Assert.Equal(faint.Length, keep[^1].To);
        Assert.True(keep[0].To >= 80);
        Assert.True(keep[^1].From <= faint.Length - 80);
    }

    [Fact]
    public void LeadingAndTrailingNoiseIsLeftOut()
    {
        var blocks = Join(Level(30, -70), Level(10, -45), Level(30, -70));
        var keep = SilenceSkip.KeepBlocks(blocks);
        var single = Assert.Single(keep);
        Assert.InRange(single.From, 288, 292);
        Assert.InRange(single.To, 408, 412);
    }

    [Fact]
    public void TimeMapGivesOriginalTimes()
    {
        var map = new SilenceSkip.TimeMap([(0, 100), (300, 400)], 100);
        Assert.Equal(20_000, map.ShortLengthMs);
        Assert.Equal(5_000, map.ToOriginal(5_000));
        Assert.Equal(30_000, map.ToOriginal(10_000));
        Assert.Equal(35_000, map.ToOriginal(15_000));
        Assert.Equal(40_000, map.ToOriginal(20_000));
    }

    [Fact]
    public void EmptyTrackKeepsNothing() => Assert.Empty(SilenceSkip.KeepBlocks([]));
}
