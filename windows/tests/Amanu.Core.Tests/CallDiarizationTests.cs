using Amanu.Core.Processing;
using Xunit;

namespace Amanu.Core.Tests;

/// <summary>A fixed, fake diarization result: no model runs here.</summary>
public sealed class CallDiarizationTests
{
    private static TranscriptSegment Them(long start, long end) => new(start, end, "x", SpeakerLabels.Them);
    private static TranscriptSegment Me(long start, long end) => new(start, end, "x", SpeakerLabels.Me);

    [Fact]
    public void EachCallLineTakesTheVoiceItOverlapsMost()
    {
        var segments = new[] { Them(0, 4_000), Me(4_000, 5_000), Them(5_000, 9_000), Them(9_000, 12_000) };
        var turns = new[] { new SpeakerTurn(0, 4_500, 7), new SpeakerTurn(5_000, 6_000, 7), new SpeakerTurn(6_000, 12_000, 3) };

        var result = CallDiarization.Apply(segments, turns);

        // Voice 7 speaks first, so it is A; the line 5–9 s has 1 s of 7 and 3 s of 3.
        Assert.Equal(["them A", "me", "them B", "them B"], result.Segments.Select(segment => segment.Speaker));
        Assert.Equal(2, result.Speakers);
    }

    [Fact]
    public void OneVoiceKeepsTheBareLabel()
    {
        var result = CallDiarization.Apply([Them(0, 1_000), Them(2_000, 3_000)], [new SpeakerTurn(0, 3_000, 0)]);
        Assert.Equal(["them", "them"], result.Segments.Select(segment => segment.Speaker));
        Assert.Equal(1, result.Speakers);
    }

    [Fact]
    public void ALineInAShortGapTakesTheNearestVoice()
    {
        var turns = new[] { new SpeakerTurn(0, 1_000, 0), new SpeakerTurn(5_000, 6_000, 1) };
        var result = CallDiarization.Apply([Them(0, 1_000), Them(4_000, 4_500), Them(5_000, 6_000)], turns);
        Assert.Equal(["them A", "them B", "them B"], result.Segments.Select(segment => segment.Speaker));
    }

    [Fact]
    public void AFarLineJoinsTheVoiceBeforeIt()
    {
        var turns = new[] { new SpeakerTurn(0, 1_000, 0), new SpeakerTurn(60_000, 61_000, 1) };
        var result = CallDiarization.Apply([Them(0, 1_000), Them(30_000, 31_000), Them(60_000, 61_000)], turns);
        Assert.Equal(["them A", "them A", "them B"], result.Segments.Select(segment => segment.Speaker));
    }

    [Fact]
    public void AVoiceWithTooLittleSpeechIsNotAPerson()
    {
        // The one-person call 2026.11.06-1021 gave voices of 580 s and 5 s at threshold 0.6.
        var turns = new[] { new SpeakerTurn(0, 60_000, 0), new SpeakerTurn(60_000, 65_000, 4), new SpeakerTurn(65_000, 120_000, 0) };
        var result = CallDiarization.Apply([Them(0, 30_000), Them(60_000, 65_000), Them(70_000, 90_000)], turns);
        Assert.Equal(["them", "them", "them"], result.Segments.Select(segment => segment.Speaker));
        Assert.Equal(1, result.Speakers);
    }

    [Fact]
    public void OnlyShortVoicesAreKeptRatherThanNone()
    {
        var turns = new[] { new SpeakerTurn(0, 3_000, 0), new SpeakerTurn(3_000, 5_000, 1) };
        Assert.Equal(2, CallDiarization.Apply([Them(0, 3_000), Them(3_000, 5_000)], turns).Speakers);
    }

    [Fact]
    public void NoTurnsOrNoCallLinesChangeNothing()
    {
        var segments = new[] { Me(0, 1_000), Them(1_000, 2_000) };
        Assert.Same(segments, CallDiarization.Apply(segments, []).Segments);
        var mine = new[] { Me(0, 1_000) };
        Assert.Same(mine, CallDiarization.Apply(mine, [new SpeakerTurn(0, 1_000, 0)]).Segments);
    }

    [Fact]
    public void LabelsAnEngineGaveAreLeftAlone()
    {
        var segments = new[] { new TranscriptSegment(0, 1_000, "x", "them 1"), new TranscriptSegment(1_000, 2_000, "x", "them 2") };
        Assert.Same(segments, CallDiarization.Apply(segments, [new SpeakerTurn(0, 2_000, 0)]).Segments);
    }
}
