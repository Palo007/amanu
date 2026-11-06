namespace Amanu.Core.Processing;

/// <summary>
/// Speech level normalization for the copy recognition hears (roadmap-speed step 4).
/// A slow AGC (automatic gain control): the gain follows the speech level, so a
/// quiet speaker is lifted and a loud one lowered toward <see cref="TargetDb"/>.
/// Silence keeps the gain it had, so hiss does not get loud, and a peak limiter
/// at the end stops clipping. Works on 16 kHz mono float in two passes: one to
/// measure (<see cref="Meter"/>), one to apply (<see cref="Apply"/>).
/// </summary>
public static class SpeechLevels
{
    public const int SampleRate = 16_000;
    /// <summary>One gain step: 100 ms. A level window is four steps (400 ms).</summary>
    public const int BlockSamples = SampleRate / 10;
    public const double TargetDb = -20;
    public const double RangeLowDb = -26;
    public const double RangeHighDb = -16;
    public const double MaxBoostDb = 20;
    public const double MaxCutDb = -12;
    /// <summary>Time constant of the gain, in seconds, used forward and backward.</summary>
    public const double SmoothingSeconds = 1.5;
    public const float CeilingAmplitude = 0.891f; // −1 dBFS
    private const int LimiterSamples = SampleRate / 100; // 10 ms
    private const double Silence = 1e-12;

    /// <summary>Changes when the method changes, so a cached answer for older audio is not reused.</summary>
    public const string Version = "levels-v1";

    /// <summary>Mean square of each 100 ms block, in reading order.</summary>
    public sealed class Meter
    {
        private readonly List<double> blocks = [];
        private double sum;
        private int count;

        public void Add(ReadOnlySpan<float> samples)
        {
            foreach (var sample in samples)
            {
                sum += (double)sample * sample;
                if (++count == BlockSamples) Close();
            }
        }

        public double[] Finish()
        {
            if (count > 0) Close();
            return [.. blocks];
        }

        private void Close()
        {
            blocks.Add(sum / count);
            sum = 0;
            count = 0;
        }
    }

    /// <summary>
    /// What to do with one track. <see cref="GainDb"/> has one gain per 100 ms
    /// block, or is null when the track is left as it is (<see cref="Skipped"/> says why).
    /// </summary>
    public sealed record Plan(double FloorDb, double SpeechDb, int SpeechBlocks, double[]? GainDb, string? Skipped)
    {
        public double MaxGainDb => GainDb is { Length: > 0 } gain ? gain.Max() : 0;
        public double MinGainDb => GainDb is { Length: > 0 } gain ? gain.Min() : 0;
    }

    /// <summary>
    /// The level of each block: the 400 ms window around it, in dBFS.
    /// </summary>
    public static double[] WindowLevels(IReadOnlyList<double> blocks)
    {
        var levels = new double[blocks.Count];
        for (var index = 0; index < blocks.Count; index++)
        {
            var first = Math.Max(0, index - 1);
            var last = Math.Min(blocks.Count - 1, index + 2);
            var total = 0.0;
            for (var at = first; at <= last; at++) total += blocks[at];
            levels[index] = 10 * Math.Log10(total / (last - first + 1) + Silence);
        }
        return levels;
    }

    /// <summary>
    /// Measures the noise floor (10th percentile of the windows) and the speech
    /// level (median of the windows clearly above the floor), then plans the gain.
    /// <paramref name="always"/> plans a gain even when the speech is already in range.
    /// </summary>
    public static Plan Decide(IReadOnlyList<double> blocks, bool always = false)
    {
        var levels = WindowLevels(blocks);
        if (levels.Length == 0) return new Plan(double.NaN, double.NaN, 0, null, "no audio");
        var floor = Percentile(levels, 0.10);
        var loud = levels.Where(level => level > floor + 10).ToArray();
        if (loud.Length < 30) return new Plan(floor, double.NaN, loud.Length, null, "no speech");
        var speech = Percentile(loud, 0.50);
        var gate = SpeechGate(floor, speech);
        var active = levels.Select(level => level > gate).ToArray();
        var speechBlocks = active.Count(value => value);
        if (!always && speech is >= RangeLowDb and <= RangeHighDb)
            return new Plan(floor, speech, speechBlocks, null, "already in range");

        var start = Math.Clamp(TargetDb - speech, MaxCutDb, MaxBoostDb);
        var alpha = 1 - Math.Exp(-0.1 / SmoothingSeconds);
        var forward = new double[levels.Length];
        var gain = start;
        for (var index = 0; index < levels.Length; index++)
        {
            // Only speech moves the gain; silence holds it.
            if (active[index]) gain += alpha * (Math.Clamp(TargetDb - levels[index], MaxCutDb, MaxBoostDb) - gain);
            forward[index] = gain;
        }
        // A second pass backward removes the lag, so a gain change starts with the speaker, not after.
        var smoothed = new double[levels.Length];
        gain = forward[^1];
        for (var index = levels.Length - 1; index >= 0; index--)
        {
            gain += alpha * (forward[index] - gain);
            smoothed[index] = Math.Clamp(gain, MaxCutDb, MaxBoostDb);
        }
        return new Plan(floor, speech, speechBlocks, smoothed, null);
    }

    /// <summary>What <see cref="Apply"/> did.</summary>
    public sealed record Outcome(long Samples, double SpeechDbAfter, int LimitedBlocks);

    /// <summary>
    /// Writes the track with the planned gain: linear between block centres,
    /// then a peak limiter that looks one 10 ms block ahead and back, so no
    /// sample goes over <see cref="CeilingAmplitude"/>.
    /// </summary>
    public static Outcome Apply(IMonoSampleSource source, Plan plan, Action<ReadOnlySpan<float>> write, CancellationToken cancellationToken)
    {
        if (plan.GainDb is not { Length: > 0 } gainDb) throw new InvalidOperationException("This plan leaves the track as it is.");
        var gains = gainDb.Select(db => Math.Pow(10, db / 20)).ToArray();
        var after = new Meter();
        var limiter = new Limiter(write, after);
        var buffer = new float[LimiterSamples * 20];
        long position = 0;
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0; index < read; index++)
                buffer[index] *= (float)GainAt(gains, position + index);
            position += read;
            limiter.Push(buffer.AsSpan(0, read));
        }
        limiter.Finish();
        var levels = WindowLevels(after.Finish());
        var gate = SpeechGate(plan.FloorDb, plan.SpeechDb);
        return new Outcome(position, SpeechAfter(levels, gainDb, gate), limiter.Limited);
    }

    /// <summary>
    /// Speech is what is well above the floor and not far under the track's own
    /// speech level, so low hum in digital silence never counts as a quiet speaker.
    /// </summary>
    private static double SpeechGate(double floorDb, double speechDb) => Math.Max(floorDb + 10, speechDb - 25);

    /// <summary>The median level, after the gain, of the windows that were speech before it.</summary>
    private static double SpeechAfter(double[] levelsAfter, double[] gainDb, double gate)
    {
        var chosen = new List<double>();
        for (var index = 0; index < levelsAfter.Length && index < gainDb.Length; index++)
            if (levelsAfter[index] - gainDb[index] > gate) chosen.Add(levelsAfter[index]);
        return chosen.Count == 0 ? double.NaN : Percentile(chosen, 0.50);
    }

    /// <summary>The gain for one sample: linear between the centres of the 100 ms blocks.</summary>
    private static double GainAt(double[] gains, long sample)
    {
        var at = (sample - BlockSamples / 2.0) / BlockSamples;
        if (at <= 0) return gains[0];
        var low = (int)Math.Floor(at);
        if (low >= gains.Length - 1) return gains[^1];
        var part = at - low;
        return gains[low] + (gains[low + 1] - gains[low]) * part;
    }

    internal static double Percentile(IReadOnlyCollection<double> values, double fraction)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return double.NaN;
        var position = fraction * (sorted.Length - 1);
        var low = (int)Math.Floor(position);
        var high = Math.Min(low + 1, sorted.Length - 1);
        return sorted[low] + (sorted[high] - sorted[low]) * (position - low);
    }

    /// <summary>
    /// A 10 ms block is written one block late. Its gain ramps from the lowest
    /// limit of blocks (b−2, b−1, b) to the lowest of (b−1, b, b+1): both are at
    /// or under block b's own limit, so every sample stays under the ceiling.
    /// </summary>
    private sealed class Limiter(Action<ReadOnlySpan<float>> write, Meter after)
    {
        private readonly float[] pending = new float[LimiterSamples];
        private int filled;
        private float[]? current;
        private double previousLimit = 1, currentLimit = 1, appliedGain = 1;

        /// <summary>How many 10 ms blocks the limiter turned down.</summary>
        public int Limited { get; private set; }

        public void Push(ReadOnlySpan<float> samples)
        {
            while (samples.Length > 0)
            {
                var take = Math.Min(samples.Length, LimiterSamples - filled);
                samples[..take].CopyTo(pending.AsSpan(filled));
                filled += take;
                samples = samples[take..];
                if (filled == LimiterSamples) Close(LimiterSamples);
            }
        }

        public void Finish()
        {
            if (filled > 0) Close(filled);
            if (current is not null) Emit(current, Math.Min(previousLimit, currentLimit));
            current = null;
        }

        private void Close(int length)
        {
            var block = pending.AsSpan(0, length).ToArray();
            filled = 0;
            var peak = 0f;
            foreach (var sample in block) peak = Math.Max(peak, Math.Abs(sample));
            var limit = peak > CeilingAmplitude ? CeilingAmplitude / peak : 1.0;
            if (current is not null) Emit(current, Math.Min(Math.Min(previousLimit, currentLimit), limit));
            previousLimit = currentLimit;
            currentLimit = limit;
            current = block;
        }

        private void Emit(float[] block, double endGain)
        {
            var startGain = appliedGain;
            if (endGain < 1 || startGain < 1) Limited++;
            for (var index = 0; index < block.Length; index++)
            {
                // The hard clamp only matters for the very first block, which has no block before it.
                var gain = startGain + (endGain - startGain) * (index + 1) / block.Length;
                var value = (float)(block[index] * gain);
                block[index] = Math.Clamp(value, -CeilingAmplitude, CeilingAmplitude);
            }
            appliedGain = endGain;
            after.Add(block);
            write(block);
        }
    }
}
