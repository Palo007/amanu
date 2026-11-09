using Amanu.Core.Processing;

namespace Amanu.Core.Tests;

public sealed class GeminiKeyRingTests
{
    private static readonly ProcessingFailure Busy = new(FailureKind.Transient, "busy");

    [Fact]
    public async Task A_used_up_key_hands_the_request_to_the_next_and_the_next_stays_first()
    {
        var ring = new GeminiKeyRing(["one", "two", "three"]);
        var asked = new List<string>();
        Task<(string?, ProcessingFailure?, int)> Send(string key)
        {
            asked.Add(key);
            return Task.FromResult<(string?, ProcessingFailure?, int)>(key == "one" ? (null, Busy, 429) : ("ok", null, 200));
        }

        Assert.Equal("ok", (await ring.SendAsync(Send)).Body);
        Assert.Equal("ok", (await ring.SendAsync(Send)).Body);
        Assert.Equal(["one", "two", "two"], asked);
    }

    [Fact]
    public async Task When_every_key_is_used_up_each_is_asked_once_and_the_failure_comes_back()
    {
        var ring = new GeminiKeyRing(["one", "two"]);
        var asked = new List<string>();
        var (body, busy) = await ring.SendAsync(key =>
        {
            asked.Add(key);
            return Task.FromResult<(string?, ProcessingFailure?, int)>((null, Busy, 429));
        });
        Assert.Null(body);
        Assert.Same(Busy, busy);
        Assert.Equal(["one", "two"], asked);
    }

    [Fact]
    public async Task A_busy_service_is_not_a_used_up_key_so_no_other_key_is_asked()
    {
        var ring = new GeminiKeyRing(["one", "two"]);
        var asked = new List<string>();
        var (body, _) = await ring.SendAsync(key =>
        {
            asked.Add(key);
            return Task.FromResult<(string?, ProcessingFailure?, int)>((null, Busy, 503));
        });
        Assert.Null(body);
        Assert.Equal(["one"], asked);
        Assert.Equal("one", ring.Current);
    }

    [Fact]
    public void Blanks_and_repeats_are_dropped_and_none_at_all_is_refused()
    {
        Assert.Equal(2, new GeminiKeyRing(["a", " ", "a ", "b"]).Count);
        Assert.Throws<ArgumentException>(() => new GeminiKeyRing(["", "  "]));
    }
}
