using Amanu.Core.Processing;
using Xunit;

namespace Amanu.Core.Tests;

public sealed class OfflineEchoSolverTests
{
    private const int Rate = OfflineEchoSolver.SampleRate;

    /// <summary>Noise in bursts of 0.3 to 1.5 s with pauses, a little low-passed: speech-like enough for the delay tracker.</summary>
    private static float[] Bursts(int length, int seed, int from, int to)
    {
        var random = new Random(seed);
        var signal = new float[length];
        var position = from;
        while (position < to)
        {
            var on = (int)((0.3 + 1.2 * random.NextDouble()) * Rate);
            for (var i = position; i < Math.Min(to, position + on); i++)
                signal[i] = (float)((random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5) * 0.3);
            position += on + (int)((0.2 + 0.4 * random.NextDouble()) * Rate);
        }
        var smooth = new float[length];
        for (var i = 3; i < length; i++) smooth[i] = (signal[i] + signal[i - 1] + signal[i - 2] + signal[i - 3]) / 4;
        return smooth;
    }

    /// <summary>The call as the mic hears it: a direct path after <paramref name="delay"/> samples and a tail of 100 ms, with the speaker's clock off by <paramref name="drift"/>.</summary>
    private static float[] Echo(float[] far, int delay, double drift = 0)
    {
        var random = new Random(7);
        var room = new float[1600];
        room[0] = 1;
        for (var k = 1; k < room.Length; k++)
            room[k] = (float)((random.NextDouble() - 0.5) * 0.4 * Math.Exp(-k / 400.0));
        var played = new float[far.Length];
        for (var n = 0; n < far.Length; n++)
        {
            var position = n * (1 + drift) - delay;
            var whole = (int)Math.Floor(position);
            if (whole < 0 || whole + 1 >= far.Length) continue;
            var share = (float)(position - whole);
            played[n] = far[whole] * (1 - share) + far[whole + 1] * share;
        }
        var echo = new float[far.Length];
        Parallel.For(0, far.Length, n =>
        {
            float sum = 0;
            for (var k = 0; k < room.Length && k <= n; k++) sum += room[k] * played[n - k];
            echo[n] = sum * 0.5f;
        });
        return echo;
    }

    private static double Db(float[] a, float[] b, int from, int to)
    {
        double pa = 0, pb = 0;
        for (var i = from; i < to; i++)
        {
            pa += (double)a[i] * a[i];
            pb += (double)b[i] * b[i];
        }
        return 10 * Math.Log10((pa + 1e-20) / (pb + 1e-20));
    }

    private static float[] Difference(float[] a, float[] b) => a.Zip(b, (x, y) => x - y).ToArray();

    [Fact]
    public void The_solver_removes_a_reverberant_echo_and_keeps_my_voice_in_double_talk()
    {
        // Far end alone for 14 s, me alone for 6 s, both for 12 s.
        var length = 32 * Rate;
        var far = Difference(Bursts(length, 1, 0, 14 * Rate), Bursts(length, 2, 20 * Rate, length).Select(x => -x).ToArray());
        var near = Bursts(length, 3, 14 * Rate, length);
        var echo = Echo(far, 800);
        var mic = near.Zip(echo, (a, b) => a + b).ToArray();

        var result = OfflineEchoSolver.Cancel(mic, far);

        var section = Assert.Single(result.Sections);
        Assert.False(result.ManyPaths);
        Assert.InRange(section.Lag, 800 - 24, 800 + 24);
        Assert.InRange(section.Slope / Rate * 1e6, -3, 3);
        // Far end alone: the echo goes down by 25 dB and more.
        Assert.True(Db(mic, result.Cleaned, 2 * Rate, 13 * Rate) > 25, $"removed {Db(mic, result.Cleaned, 2 * Rate, 13 * Rate):0.0} dB");
        // Me alone: nothing to remove, so nothing changes.
        Assert.True(Db(Difference(result.Cleaned, mic), mic, 15 * Rate, 19 * Rate) < -40);
        // Both: what is left beside my voice is 15 dB and more below the echo that was there.
        var left = Difference(result.Cleaned, near);
        Assert.True(Db(echo, left, 21 * Rate, 31 * Rate) > 15, $"double talk {Db(echo, left, 21 * Rate, 31 * Rate):0.0} dB");
        // The suppressor may only take away: my voice alone keeps its level.
        var suppressed = OfflineEchoSolver.Suppress(result.Cleaned, result.Echo);
        Assert.InRange(Db(suppressed, mic, 15 * Rate, 19 * Rate), -0.5, 0.1);
        Assert.True(Db(mic, suppressed, 2 * Rate, 13 * Rate) > Db(mic, result.Cleaned, 2 * Rate, 13 * Rate));
    }

    [Fact]
    public void The_solver_follows_a_clock_that_runs_100_ppm_off()
    {
        var length = 60 * Rate;
        var far = Bursts(length, 4, 0, length);
        var echo = Echo(far, 1200, drift: 100e-6);
        var mic = echo.Zip(Bursts(length, 5, 40 * Rate, length), (a, b) => a + b).ToArray();

        var result = OfflineEchoSolver.Cancel(mic, far);

        var section = Assert.Single(result.Sections);
        // The mic hears the call 100 ppm fast, so the delay shrinks by 1.6 samples a second.
        Assert.InRange(section.Slope / Rate * 1e6, -105, -95);
        Assert.True(Db(mic, result.Cleaned, 2 * Rate, 38 * Rate) > 20, $"removed {Db(mic, result.Cleaned, 2 * Rate, 38 * Rate):0.0} dB");
    }

    [Fact]
    public void Without_an_echo_the_mic_comes_back_as_it_was()
    {
        var length = 30 * Rate;
        var far = Bursts(length, 6, 0, length);
        var mic = Bursts(length, 8, 0, length);

        var result = OfflineEchoSolver.Cancel(mic, far);

        Assert.Empty(result.Sections);
        Assert.False(result.ManyPaths);
        Assert.Equal(mic, result.Cleaned);
    }

    [Fact]
    public void A_long_call_solved_in_blocks_is_as_clean_as_one_solved_whole()
    {
        var length = 90 * Rate;
        var far = Bursts(length, 9, 0, length);
        var mic = Echo(far, 640);

        var whole = OfflineEchoSolver.Cancel(mic, far, blockSeconds: 300);
        var blocks = OfflineEchoSolver.Cancel(mic, far, blockSeconds: 30);

        Assert.True(Db(mic, whole.Cleaned, 2 * Rate, 88 * Rate) > 25);
        Assert.True(Db(mic, blocks.Cleaned, 2 * Rate, 88 * Rate) > Db(mic, whole.Cleaned, 2 * Rate, 88 * Rate) - 3,
            $"blocks {Db(mic, blocks.Cleaned, 2 * Rate, 88 * Rate):0.0} dB, whole {Db(mic, whole.Cleaned, 2 * Rate, 88 * Rate):0.0} dB");
    }

    [Fact]
    public void The_solver_models_are_behind_the_echo_model_setting()
    {
        Assert.Same(LocalVqeAssets.Solve, LocalVqeAssets.For("solve"));
        Assert.Null(LocalVqeAssets.Solve.Solver!.SuppressorFloor);
        Assert.Equal(0.15, LocalVqeAssets.For("solve-floor").Solver!.SuppressorFloor);
        Assert.True(LocalVqeAssets.For("solve-gate").Solver!.SpeechGate);
        // Many devices in one recording go to plain Sonora, so the models carry its library and no tuning.
        Assert.All(LocalVqeAssets.Models.Where(model => model.Solver is not null), model =>
        {
            Assert.True(model.IsSonora);
            Assert.Null(model.SonoraTuning);
            Assert.Null(model.SonoraPrime);
        });
        Assert.Null(LocalVqeAssets.Sonora.Solver);
    }
}
