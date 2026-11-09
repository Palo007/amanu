using Amanu.App;
using Xunit;

namespace Amanu.Live.Tests;

public sealed class PassProgressTests
{
    [Theory]
    [InlineData(true, false, 0.30)]
    [InlineData(true, true, 0.30)]
    [InlineData(false, true, 0.08)]
    [InlineData(false, false, 0.05)]
    public void Recognition_starts_after_the_steps_that_run_before_it(bool echo, bool levels, double expected) =>
        Assert.Equal(expected, PassProgress.RecognitionStart(echo, levels), 3);

    [Theory]
    [InlineData(0.0, 0.30)]
    [InlineData(0.5, 0.60)]
    [InlineData(1.0, 0.90)]
    [InlineData(-1.0, 0.30)]
    [InlineData(2.0, 0.90)]
    public void Recognition_fills_its_share_and_never_runs_past_the_names(double share, double expected) =>
        Assert.Equal(expected, PassProgress.Recognition(0.30, share), 3);

    [Fact]
    public void Every_step_comes_after_the_one_before_it() =>
        Assert.True(PassProgress.Start < PassProgress.Echo
            && PassProgress.Echo < PassProgress.Levels
            && PassProgress.Levels < PassProgress.RecognitionStart(true, true)
            && PassProgress.SpeakerNames < PassProgress.Summary
            && PassProgress.Summary < 1);
}
