using System.Text.Json;
using Amanu.Core.Configuration;
using Amanu.Core.Processing;

namespace Amanu.Core.Tests;

public sealed class SpeechLevelsTests
{
    private const int Rate = SpeechLevels.SampleRate;

    [Fact]
    public void QuietSpeechIsLiftedTowardTheTarget()
    {
        var samples = Concat(Noise(2, -70), Speech(20, -45, -70), Noise(2, -70));
        var (plan, outcome, output) = Run(samples);

        Assert.Null(plan.Skipped);
        Assert.InRange(plan.SpeechDb, -48, -42);
        Assert.Equal(SpeechLevels.MaxBoostDb, plan.MaxGainDb, 1);
        Assert.InRange(outcome.SpeechDbAfter, -29, -21);
        Assert.Equal(samples.Length, output.Length);
    }

    [Fact]
    public void LoudSpeechIsLoweredButNotMoreThanTheLimit()
    {
        var (plan, _, _) = Run(Speech(20, -5, -60));
        Assert.Null(plan.Skipped);
        Assert.True(plan.MinGainDb >= SpeechLevels.MaxCutDb - 0.01);
        Assert.True(plan.MaxGainDb < 0);
    }

    [Fact]
    public void SpeechAlreadyInRangeIsLeftAsItIs()
    {
        var plan = SpeechLevels.Decide(Blocks(Speech(20, -21, -70)));
        Assert.Equal("already in range", plan.Skipped);
        Assert.Null(plan.GainDb);
    }

    [Fact]
    public void SilenceIsNotSpeech()
    {
        var plan = SpeechLevels.Decide(Blocks(Noise(20, -70)));
        Assert.Equal("no speech", plan.Skipped);
        Assert.Null(plan.GainDb);
    }

    [Fact]
    public void QuietAndLoudSpeakersInOneTrackGetTheirOwnGain()
    {
        var samples = Concat(Speech(15, -18, -70), Speech(15, -42, -70));
        var plan = SpeechLevels.Decide(Blocks(samples));
        Assert.NotNull(plan.GainDb);
        var loud = plan.GainDb![50..100].Average();
        var quiet = plan.GainDb![200..250].Average();
        Assert.True(quiet - loud > 15, $"quiet {quiet:0.0} dB, loud {loud:0.0} dB");
    }

    [Fact]
    public void ALongPauseHoldsTheGainSoHissDoesNotGetLoud()
    {
        var samples = Concat(Speech(10, -10, -50), Noise(20, -50), Speech(10, -10, -50));
        var plan = SpeechLevels.Decide(Blocks(samples));
        Assert.NotNull(plan.GainDb);
        var speechGain = plan.GainDb![50..90].Average();
        var pauseGain = plan.GainDb![120..280].Max();
        Assert.True(pauseGain <= speechGain + 1, $"pause {pauseGain:0.0} dB, speech {speechGain:0.0} dB");
    }

    [Fact]
    public void NoSampleGoesOverTheCeiling()
    {
        var samples = Speech(20, -45, -70);
        // Short clicks far over the speech: the gain lifts them over full scale.
        for (var at = Rate; at < samples.Length; at += Rate * 2)
            for (var index = 0; index < 40; index++) samples[at + index] = index % 2 == 0 ? 0.6f : -0.6f;
        var (_, outcome, output) = Run(samples);
        Assert.True(output.Max(Math.Abs) <= SpeechLevels.CeilingAmplitude);
        Assert.True(outcome.LimitedBlocks > 0);
    }

    [Fact]
    public void TheSettingIsOffByDefaultAndReadsAuto()
    {
        Assert.Equal("off", new AppSettings().Transcription.LevelNormalization);
        Assert.Equal("off", JsonSerializer.Deserialize<AppSettings>("{}")!.Transcription.LevelNormalization);
        Assert.Equal("auto", JsonSerializer.Deserialize<AppSettings>(
            """{"transcription": {"level_normalization": "auto"}}""")!.Transcription.LevelNormalization);
        Assert.Equal(["auto", "off"], SettingsSchema.Find("transcription.level_normalization")!.Options);
    }

    private static (SpeechLevels.Plan Plan, SpeechLevels.Outcome Outcome, float[] Output) Run(float[] samples)
    {
        var plan = SpeechLevels.Decide(Blocks(samples));
        var output = new List<float>();
        var outcome = SpeechLevels.Apply(new ArraySource(samples), plan, block => output.AddRange(block.ToArray()), CancellationToken.None);
        return (plan, outcome, [.. output]);
    }

    private static double[] Blocks(float[] samples)
    {
        var meter = new SpeechLevels.Meter();
        meter.Add(samples);
        return meter.Finish();
    }

    /// <summary>
    /// Phrases of 2 s with a 1 s pause, made of syllable-like bursts of a tone
    /// (RMS of the bursts at <paramref name="levelDb"/>), over noise.
    /// </summary>
    private static float[] Speech(double seconds, double levelDb, double floorDb)
    {
        var samples = Noise(seconds, floorDb);
        var amplitude = Math.Pow(10, levelDb / 20) * Math.Sqrt(2);
        for (var index = 0; index < samples.Length; index++)
        {
            var time = (double)index / Rate;
            if (time % 3 < 2 && time % 0.25 < 0.18) samples[index] += (float)(amplitude * Math.Sin(2 * Math.PI * 220 * time));
        }
        return samples;
    }

    private static float[] Noise(double seconds, double levelDb)
    {
        var random = new Random(7);
        var scale = Math.Pow(10, levelDb / 20) * Math.Sqrt(3);
        return Enumerable.Range(0, (int)(seconds * Rate)).Select(_ => (float)((random.NextDouble() * 2 - 1) * scale)).ToArray();
    }

    private static float[] Concat(params float[][] parts) => parts.SelectMany(part => part).ToArray();

    private sealed class ArraySource(float[] samples) : IMonoSampleSource
    {
        private int position;

        public int Read(Span<float> buffer)
        {
            var count = Math.Min(buffer.Length, samples.Length - position);
            samples.AsSpan(position, count).CopyTo(buffer);
            position += count;
            return count;
        }
    }
}
