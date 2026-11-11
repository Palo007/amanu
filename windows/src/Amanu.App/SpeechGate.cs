using System.IO;
using Amanu.Core.Processing;
using SherpaOnnx;

namespace Amanu.App;

/// <summary>
/// Turns the cleaned mic down where I do not talk. The final engine loses my words in loud
/// noise mostly because of the noise in the stretches where I am silent, not because of the
/// noise over my words: with the noise of the far-end-only parts alone turned down, the
/// quality suite's cafe at 0 dB went from 73 % of my words wrong to 27 %. So the speech
/// itself stays as the echo stage left it, and only the gain between my sentences changes.
/// The denoiser DPDFNet says where I talk: its output is close to silent outside the
/// foreground speech, also in babble, where a level-based detector fails.
/// </summary>
public static class SpeechGate
{
    public const string ModelName = "dpdfnet_baseline.onnx";
    private const int SampleRate = 16_000;

    /// <summary>The denoiser's model beside the echo runtime, or null when this build does not carry it.</summary>
    public static string? ModelPath(string? applicationDirectory = null)
    {
        var path = Path.Combine(applicationDirectory ?? AppContext.BaseDirectory, LocalVqeAssets.RuntimeFolder, ModelName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Applies the gate to <paramref name="audio"/> in place and returns the share of the time it stayed open.</summary>
    public static double Apply(float[] audio, string modelPath, double floor = 0.15, CancellationToken cancellationToken = default)
    {
        var gain = Gain(Denoise(audio, modelPath, cancellationToken), floor, out var open);
        for (var i = 0; i < audio.Length; i++) audio[i] *= gain[i];
        return open;
    }

    /// <summary>
    /// The denoiser's output for the whole signal. Pieces of 30 s run side by side, each with
    /// 2 s of sound before and after it for the model to settle on; only where the output is
    /// loud matters here, so the joins need no cross-fade.
    /// </summary>
    public static float[] Denoise(float[] audio, string modelPath, CancellationToken cancellationToken = default)
    {
        const int core = 30 * SampleRate, context = 2 * SampleRate;
        var clean = new float[audio.Length];
        var pieces = (audio.Length + core - 1) / core;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, Math.Max(1, pieces)),
            CancellationToken = cancellationToken,
        };
        Parallel.For(0, pieces, options, () => Open(modelPath), (piece, _, denoiser) =>
        {
            var start = piece * core;
            var end = Math.Min(audio.Length, start + core);
            var from = Math.Max(0, start - context);
            var to = Math.Min(audio.Length, end + context);
            var output = denoiser.Run(audio[from..to], SampleRate).Samples;
            var count = Math.Min(end - start, output.Length - (start - from));
            if (count > 0) Array.Copy(output, start - from, clean, start, count);
            return denoiser;
        }, denoiser => denoiser.Dispose());
        return clean;
    }

    private static OfflineSpeechDenoiser Open(string modelPath)
    {
        var config = new OfflineSpeechDenoiserConfig();
        config.Model.Dpdfnet.Model = modelPath;
        config.Model.NumThreads = 1;
        config.Model.Provider = "cpu";
        return new OfflineSpeechDenoiser(config);
    }

    /// <summary>
    /// The gain per sample: 1 where the denoiser's output, averaged over 0.3 s, is within 25 dB
    /// of its 99th percentile for at least 0.3 s, kept open 0.3 s on both sides; the floor
    /// elsewhere, with ramps of 30 ms. A short burst is a voice from the noise that the
    /// denoiser let through, not me.
    /// </summary>
    public static float[] Gain(float[] clean, double floor, out double open)
    {
        const int n = 512, hop = 256;
        const double belowDb = 25, smoothSeconds = 0.3, leastSeconds = 0.3, hangSeconds = 0.3;
        var gain = new float[clean.Length];
        open = 1;
        var frames = clean.Length < n ? 0 : 1 + (clean.Length - n) / hop;
        if (frames == 0)
        {
            Array.Fill(gain, 1f);
            return gain;
        }
        var power = new double[frames];
        Parallel.For(0, frames, t =>
        {
            double sum = 0;
            for (var i = t * hop; i < t * hop + n; i++) sum += (double)clean[i] * clean[i];
            power[t] = sum / n;
        });
        var k = Math.Max(1, (int)Math.Round(smoothSeconds * SampleRate / hop));
        var smooth = new double[frames];
        for (var t = 0; t < frames; t++)
        {
            double sum = 0;
            for (var j = 0; j < k; j++) sum += power[Math.Clamp(t - k / 2 + j, 0, frames - 1)];
            smooth[t] = sum / k;
        }
        var sorted = (double[])smooth.Clone();
        Array.Sort(sorted);
        var rank = 0.99 * (frames - 1);
        var low = (int)Math.Floor(rank);
        var top = sorted[low] + (rank - low) * (sorted[Math.Min(frames - 1, low + 1)] - sorted[low]);
        var threshold = top * Math.Pow(10, -belowDb / 10);
        var active = new bool[frames];
        for (var t = 0; t < frames; t++) active[t] = smooth[t] >= threshold;

        for (var start = 0; start < frames;)
        {
            if (!active[start]) { start++; continue; }
            var end = start;
            while (end < frames && active[end]) end++;
            if ((end - start) * hop < leastSeconds * SampleRate) Array.Fill(active, false, start, end - start);
            start = end;
        }
        var hang = (int)Math.Round(hangSeconds * SampleRate / hop);
        var wide = new bool[frames];
        var last = int.MinValue / 2;
        for (var t = 0; t < frames; t++)
        {
            if (active[t]) last = t;
            wide[t] = t - last <= hang;
        }
        last = int.MaxValue / 2;
        for (var t = frames - 1; t >= 0; t--)
        {
            if (active[t]) last = t;
            wide[t] |= last - t <= hang;
        }
        open = wide.Count(value => value) / (double)frames;

        // Straight lines between the frames' centres, then a mean over 30 ms.
        var steps = new float[clean.Length];
        for (var i = 0; i < steps.Length; i++)
        {
            var position = (i - n / 2.0) / hop;
            var before = Math.Clamp((int)Math.Floor(position), 0, frames - 1);
            var after = Math.Min(frames - 1, before + 1);
            var share = Math.Clamp(position - before, 0, 1);
            double a = wide[before] ? 1 : floor, b = wide[after] ? 1 : floor;
            steps[i] = (float)(a + (b - a) * share);
        }
        const int ramp = 480;
        double window = 0;
        for (var j = 0; j < ramp; j++) window += steps[Math.Clamp(j - ramp / 2, 0, steps.Length - 1)];
        for (var i = 0; i < gain.Length; i++)
        {
            gain[i] = (float)(window / ramp);
            window += steps[Math.Clamp(i + ramp / 2, 0, steps.Length - 1)] - steps[Math.Clamp(i - ramp / 2, 0, steps.Length - 1)];
        }
        return gain;
    }
}
