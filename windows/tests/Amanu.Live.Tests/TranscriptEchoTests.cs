using Amanu.App;
using Amanu.Core.Configuration;
using Xunit;

namespace Amanu.Live.Tests;

public sealed class TranscriptEchoTests
{
    private static readonly SessionAudio Call = new(@"C:\session", "t", "mic.wav", "system.wav", null, 0, 0);

    [Fact]
    public void Echo_off_is_recorded_as_off()
    {
        var echo = ProcessingCoordinator.DescribeEcho(new AppSettings { OfflineEchoCancellation = false }, Call, null);
        Assert.Equal("off", echo.Model);
        Assert.False(echo.Ran);
    }

    [Fact]
    public void A_recording_with_one_track_names_the_model_but_says_it_did_not_run()
    {
        var echo = ProcessingCoordinator.DescribeEcho(new AppSettings { EchoModel = "sonora" }, Call with { System = null }, null);
        Assert.Equal("sonora", echo.Model);
        Assert.False(echo.Ran);
        Assert.False(echo.TranscriptFilter);
    }

    [Fact]
    public void A_solver_run_is_recorded_with_its_processor()
    {
        var record = new EchoCancellationRecord("solver-v1", Mode: "echo_solver");
        var echo = ProcessingCoordinator.DescribeEcho(new AppSettings { EchoModel = "solve-gate-cut" }, Call, record);
        Assert.Equal(new Amanu.Core.Processing.TranscriptEcho("solve-gate-cut", true, null, "solver-v1", true), echo);
    }

    [Fact]
    public void A_solver_model_that_fell_back_to_its_stream_says_so()
    {
        var record = new EchoCancellationRecord("solver-v1", Mode: "sonora");
        var echo = ProcessingCoordinator.DescribeEcho(new AppSettings { EchoModel = "solve-gate" }, Call, record);
        Assert.True(echo.Ran);
        Assert.Contains("plain sonora", echo.Note);
    }

    [Fact]
    public void A_skipped_step_and_a_silent_call_track_are_not_reported_as_run()
    {
        var settings = new AppSettings { EchoModel = "sonora" };
        Assert.False(ProcessingCoordinator.DescribeEcho(settings, Call, new EchoCancellationRecord("v", Skipped: "no DLL")).Ran);
        Assert.False(ProcessingCoordinator.DescribeEcho(settings, Call, new EchoCancellationRecord("v", Mode: "silent_reference_passthrough")).Ran);
    }
}
