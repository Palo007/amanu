using Amanu.App;
using Xunit;

namespace Amanu.Live.Tests;

public sealed class LocalAsrThreadsTests
{
    [Theory]
    [InlineData(null, 16, 16)]
    [InlineData("", 16, 16)]
    [InlineData("abc", 16, 16)]
    [InlineData("0", 16, 16)]
    [InlineData("-3", 16, 16)]
    [InlineData("8", 16, 8)]
    [InlineData("32", 16, 16)]
    [InlineData(null, 0, 1)]
    public void Thread_count_uses_a_valid_override_and_never_exceeds_the_processors(string? setting, int processors, int expected) =>
        Assert.Equal(expected, LocalTranscriptionEngine.ThreadCount(setting, processors));

    [Theory]
    [InlineData(16, 2, 8)]
    [InlineData(16, 1, 16)]
    [InlineData(15, 2, 7)]
    [InlineData(1, 2, 1)]
    [InlineData(8, 0, 8)]
    public void Tracks_that_run_together_share_the_threads_evenly(int threads, int tracks, int expected) =>
        Assert.Equal(expected, LocalTranscriptionEngine.ShareThreads(threads, tracks));
}
