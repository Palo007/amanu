using Amanu.Core.Processing;

namespace Amanu.Core.Tests;

public sealed class SummarySourcesTests
{
    private const string Live = "# Live transcript\n\nCaptions.\n\n[00:05] **me:** Hello.\n\n";

    [Fact]
    public void Without_live_lines_the_final_transcript_is_used_whatever_the_setting()
    {
        Assert.Equal(("final", (string?)null), SummarySources.Choose("live", "final", null));
        Assert.Equal(("final", (string?)null), SummarySources.Choose("both", "final", "# Live transcript\n\n"));
    }

    [Fact]
    public void The_setting_picks_the_source_when_there_are_live_lines()
    {
        Assert.Equal(("final", (string?)null), SummarySources.Choose("transcript", "final", Live));
        Assert.Equal((Live, (string?)null), SummarySources.Choose("live", "final", Live));
        Assert.Equal(("final", (string?)Live), SummarySources.Choose("both", "final", Live));
        Assert.Equal(("final", (string?)null), SummarySources.Choose("nonsense", "final", Live));
    }

    [Theory]
    [InlineData("**[1:05] speaker:** x", 65_000L)]
    [InlineData("[01:05] **me:** x", 65_000L)]
    [InlineData("**[1:02:03] A:** x", 3_723_000L)]
    [InlineData("# Live transcript", null)]
    public void Reads_the_time_from_either_kind_of_line(string line, long? expected) =>
        Assert.Equal(expected, SummarySources.Time(line));

    [Fact]
    public void Each_piece_of_the_final_transcript_gets_the_live_lines_from_the_same_time()
    {
        var final = string.Concat(Enumerable.Range(0, 10).Select(minute => $"**[{minute}:00] A:** final {minute}\n\n"));
        var live = "# Live transcript\n\n" + string.Concat(Enumerable.Range(0, 10).Select(minute => $"[{minute:00}:30] **me:** live {minute}\n\n"));

        var pairs = SummarySources.Pair(final, live, maximumCharacters: 60);

        Assert.True(pairs.Count > 1);
        Assert.Contains("# Live transcript", pairs[0].Live);
        foreach (var (piece, liveLines) in pairs)
        {
            var minutes = piece.Split('\n').Select(SummarySources.Time).OfType<long>().Select(ms => ms / 60_000).ToList();
            var liveMinutes = liveLines.Split('\n').Select(SummarySources.Time).OfType<long>().Select(ms => ms / 60_000).ToList();
            Assert.Equal(minutes, liveMinutes);
        }
    }

    [Fact]
    public void A_short_meeting_is_sent_in_one_piece()
    {
        var pair = Assert.Single(SummarySources.Pair("**[0:00] A:** hi\n", Live));
        Assert.Equal(Live, pair.Live);
    }
}
