using Amanu.App;
using Xunit;

namespace Amanu.Live.Tests;

public sealed class SpeechGatePiecesTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(480_000)]
    [InlineData(480_001)]
    [InlineData(1_234_567)]
    public void Pieces_tile_the_track_once_and_reach_two_seconds_to_both_sides(int length)
    {
        var pieces = SpeechGate.Pieces(length);
        Assert.Equal((length + 479_999) / 480_000, pieces.Count);
        var next = 0;
        foreach (var (start, end, from, to) in pieces)
        {
            Assert.Equal(next, start);
            Assert.True(end > start && end <= length);
            Assert.Equal(Math.Max(0, start - 32_000), from);
            Assert.Equal(Math.Min(length, end + 32_000), to);
            next = end;
        }
        Assert.Equal(length, next);
    }

    [Fact]
    public void An_empty_track_has_no_pieces()
    {
        Assert.Empty(SpeechGate.Pieces(0));
    }

    [Theory]
    [InlineData(1, 16, 1)]
    [InlineData(100, 16, 8)]
    [InlineData(3, 16, 3)]
    [InlineData(5, 4, 2)]
    [InlineData(5, 1, 1)]
    [InlineData(0, 8, 1)]
    public void Workers_are_half_the_cores_and_never_more_than_the_pieces(int pieces, int processors, int expected)
    {
        Assert.Equal(expected, SpeechGate.Workers(pieces, processors));
    }

    [Fact]
    public void Gain_in_place_is_the_gain_of_a_copy()
    {
        var clean = new float[16_000 * 5];
        for (var i = 3 * 16_000; i < clean.Length; i++) clean[i] = 0.1f * MathF.Sin(i * 0.05f);
        var copy = (float[])clean.Clone();
        var apart = SpeechGate.Gain(clean, 0.15, out var openApart);
        var same = SpeechGate.Gain(copy, 0.15, out var openSame, inPlace: true);
        Assert.Same(copy, same);
        Assert.Equal(openApart, openSame);
        Assert.Equal(apart, same);
    }
}
