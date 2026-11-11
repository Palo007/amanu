using Amanu.Core.Processing;

namespace Amanu.Core.Tests;

public sealed class SpeechRegionsTests
{
    private const int Rate = SpeechRegions.SampleRate;

    private static SpeechRegions.Region Seconds(double from, double to) => new((long)(from * Rate), (long)(to * Rate));

    private static float[] Quiet(double seconds) => new float[(int)(seconds * Rate)];

    [Fact]
    public void Open_runs_are_the_stretches_above_the_level()
    {
        float[] gain = [0.15f, 1, 1, 0.15f, 0.15f, 1, 0.15f, 1];
        Assert.Equal([new(1, 3), new(5, 6), new(7, 8)], SpeechRegions.OpenRuns(gain));
    }

    [Fact]
    public void Runs_closer_than_the_gap_are_joined_and_farther_ones_are_not()
    {
        var merged = SpeechRegions.Merge([Seconds(0, 2), Seconds(3, 4), Seconds(6, 7)], (long)(1.5 * Rate));
        Assert.Equal([Seconds(0, 4), Seconds(6, 7)], merged);
    }

    [Fact]
    public void Padding_is_added_on_both_sides_and_stays_inside_the_track()
    {
        var audio = Quiet(30);
        var regions = SpeechRegions.Plan([Seconds(0.5, 3), Seconds(20, 22)], audio.Length, audio);
        Assert.Equal([Seconds(0, 4), Seconds(19, 23)], regions);
        regions = SpeechRegions.Plan([Seconds(28.5, 29.9)], audio.Length, audio);
        Assert.Equal([Seconds(27.5, 30)], regions);
    }

    [Fact]
    public void Padded_regions_that_touch_become_one_so_that_no_audio_is_sent_twice()
    {
        // 1.8 s apart is more than the 1.5 s merge gap, but 2 x 1 s of padding overlaps.
        var audio = Quiet(30);
        var regions = SpeechRegions.Plan([Seconds(5, 8), Seconds(9.8, 14)], audio.Length, audio);
        Assert.Equal([Seconds(4, 15)], regions);
    }

    [Fact]
    public void Every_open_sample_lies_inside_a_region()
    {
        var audio = Quiet(60);
        var open = new[] { Seconds(1, 2), Seconds(9, 12), Seconds(30, 31), Seconds(40, 59) };
        var regions = SpeechRegions.Plan(open, audio.Length, audio);
        Assert.All(open, run => Assert.Contains(regions, region => region.Start <= run.Start && region.End >= run.End));
    }

    [Fact]
    public void A_long_stretch_is_cut_at_its_quietest_place_and_no_piece_passes_the_cap()
    {
        var audio = Enumerable.Repeat(0.1f, 80 * Rate).ToArray();
        // A pause of 0.3 s at 20 s, which lies in the last 40 % of the first 25 s.
        Array.Clear(audio, 20 * Rate, (int)(0.3 * Rate));
        var regions = SpeechRegions.Plan([Seconds(2, 70)], audio.Length, audio);
        Assert.All(regions, region => Assert.True(region.Length <= 25 * Rate));
        Assert.InRange(regions[0].End, 20 * Rate, (long)(20.4 * Rate));
        Assert.Equal(Seconds(1, 71).Start, regions[0].Start);
        Assert.Equal(Seconds(1, 71).End, regions[^1].End);
        for (var index = 1; index < regions.Count; index++) Assert.Equal(regions[index - 1].End, regions[index].Start);
    }

    [Fact]
    public void A_word_time_inside_a_region_is_placed_at_the_regions_offset_in_the_call()
    {
        var region = Seconds(19, 23);
        Assert.Equal(19_000 + 1_250, SpeechRegions.ToTrackMs(region, 1_250));
        Assert.Equal(19_000, SpeechRegions.ToTrackMs(region, 0));
    }

    [Fact]
    public void A_region_is_brought_to_minus_20_dbfs_over_its_open_samples()
    {
        // 0.01 amplitude square wave where the gate is open, much louder noise around it.
        var audio = Enumerable.Range(0, 10 * Rate).Select(i => i % 2 == 0 ? 0.01f : -0.01f).ToArray();
        var open = new[] { Seconds(2, 8) };
        var gain = SpeechRegions.Gain(audio, Seconds(0, 10), open);
        Assert.Equal(10.0f, gain, 0.01f);
        var rms = Math.Sqrt(audio[(int)(2 * Rate)..(int)(8 * Rate)].Average(sample => (double)sample * sample)) * gain;
        Assert.Equal(-20, 20 * Math.Log10(rms), 0.01);
    }

    [Fact]
    public void The_lift_is_limited_to_30_db_and_the_peak_stays_under_one()
    {
        var faint = Enumerable.Repeat(0.0001f, Rate).ToArray();
        Assert.Equal(Math.Pow(10, 30.0 / 20), SpeechRegions.Gain(faint, Seconds(0, 1), [Seconds(0, 1)]), 0.01);
        var peaky = new float[Rate];
        Array.Fill(peaky, 0.05f);
        peaky[100] = 0.5f;
        Assert.True(SpeechRegions.Gain(peaky, Seconds(0, 1), [Seconds(0, 1)]) * 0.5f <= 0.97f + 1e-4f);
    }

    [Fact]
    public void The_gate_file_round_trips_and_is_refused_for_another_length()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "mic.gate.json");
        SpeechRegions.Gate.From([Seconds(1, 2), Seconds(5, 6)], 10 * Rate).Save(path);
        var gate = SpeechRegions.Gate.TryLoad(path, 10 * Rate);
        Assert.NotNull(gate);
        Assert.Equal([Seconds(1, 2), Seconds(5, 6)], gate!.Runs);
        Assert.Null(SpeechRegions.Gate.TryLoad(path, 11 * Rate));
        Assert.Null(SpeechRegions.Gate.TryLoad(Path.Combine(directory.Path, "none.json"), 10 * Rate));
    }

    [Fact]
    public void The_cut_model_is_the_gate_model_with_regions_and_no_other_model_cuts()
    {
        var cut = LocalVqeAssets.For("solve-gate-cut");
        Assert.Equal("solve-gate-cut", cut.Key);
        Assert.True(cut.Solver!.SpeechGate);
        Assert.True(cut.Solver.CutRegions);
        Assert.NotEqual(LocalVqeAssets.SolveGate.ProcessorVersion, cut.ProcessorVersion);
        Assert.All(LocalVqeAssets.Models.Where(model => model.Key != "solve-gate-cut"), model => Assert.False(model.Solver?.CutRegions ?? false));
        Assert.Same(LocalVqeAssets.SolveFloor, LocalVqeAssets.Default);
    }
}
