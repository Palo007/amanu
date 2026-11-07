using Amanu.Core.Processing;

namespace Amanu.Core.Tests;

public sealed class PauseCutsTests
{
    private static double[] Level(double seconds, double db, int seed = 1)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, (int)(seconds * 10)).Select(_ => Math.Pow(10, (db + random.NextDouble() - 0.5) / 10)).ToArray();
    }

    private static double[] Join(params double[][] parts) => parts.SelectMany(part => part).ToArray();

    [Fact]
    public void CutsAtThePauseInTheSearchWindow()
    {
        // Speech, a 0.5 s pause at 52 s, speech to 130 s.
        var blocks = Join(Level(52, -30), Level(0.5, -70), Level(77.5, -30));
        var cuts = PauseCuts.Boundaries(blocks, 600, 400);
        Assert.InRange(cuts[0], 520, 526);
        Assert.Equal(blocks.Length, cuts[^1]);
    }

    [Fact]
    public void FallsBackToTheMaximumWithoutAPause()
    {
        var blocks = Join(Level(15, -70), Level(115, -30));
        var cuts = PauseCuts.Boundaries(blocks, 600, 400);
        Assert.Equal([600, 1200, 1300], cuts);
    }

    [Fact]
    public void IgnoresAPauseBeforeTheSearchWindowAndNeverExceedsTheMaximum()
    {
        var blocks = Join(Level(20, -30), Level(15, -70), Level(95, -30));
        var cuts = PauseCuts.Boundaries(blocks, 600, 400);
        var start = 0;
        foreach (var cut in cuts)
        {
            Assert.InRange(cut - start, 1, 600);
            start = cut;
        }
        Assert.Equal(600, cuts[0]);
    }

    [Fact]
    public void ShortTrackIsOneChunk() => Assert.Equal([300], PauseCuts.Boundaries(Level(30, -30), 600, 400));

    [Fact]
    public void EmptyTrackHasNoCuts() => Assert.Empty(PauseCuts.Boundaries([], 600, 400));
}
