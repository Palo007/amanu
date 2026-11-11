using System.Text.Json;

namespace Amanu.Core.Processing;

/// <summary>
/// The stretches of the cleaned mic that the engine is given one by one in the
/// <c>solve-gate-cut</c> echo model: the speech gate's open runs, merged across short
/// pauses, padded, and cut at a length the engine copes with. In the quality lab this
/// (the notebook's setting c) left 10.0 % of my words wrong on the held-out set against
/// 11.8 % when the same track went through in 60 s chunks. The engine normalizes its
/// input per file, so a file that holds one stretch of speech is judged by that
/// speech and not by the noise of the minute around it.
/// </summary>
public static class SpeechRegions
{
    public const int SampleRate = 16_000;

    /// <summary>Open runs closer together than this are one region.</summary>
    public const double MergeSeconds = 1.5;

    /// <summary>Context added on each side of a region; the gate's own edges are tight.</summary>
    public const double PadSeconds = 1.0;

    /// <summary>The longest region; the engine's attention memory is bounded.</summary>
    public const double CapSeconds = 25;

    /// <summary>The level each region is brought to, as <see cref="SpeechLevels.TargetDb"/>.</summary>
    public const double TargetDb = SpeechLevels.TargetDb;

    /// <summary>The most a region is lifted, so a region of near silence is not turned into noise.</summary>
    public const double MaxBoostDb = 30;

    /// <summary>A half-open sample range [Start, End) of the track.</summary>
    public readonly record struct Region(long Start, long End)
    {
        public long Length => End - Start;
    }

    /// <summary>The runs of samples where the gate's gain is above <paramref name="level"/>.</summary>
    public static IReadOnlyList<Region> OpenRuns(ReadOnlySpan<float> gain, float level = 0.5f)
    {
        var runs = new List<Region>();
        long start = -1;
        for (var index = 0; index < gain.Length; index++)
        {
            var open = gain[index] > level;
            if (open && start < 0) start = index;
            else if (!open && start >= 0)
            {
                runs.Add(new Region(start, index));
                start = -1;
            }
        }
        if (start >= 0) runs.Add(new Region(start, gain.Length));
        return runs;
    }

    /// <summary>Joins neighbours whose gap is shorter than <paramref name="gap"/> samples.</summary>
    public static IReadOnlyList<Region> Merge(IEnumerable<Region> runs, long gap)
    {
        var merged = new List<Region>();
        foreach (var run in runs)
        {
            if (merged.Count > 0 && run.Start - merged[^1].End < gap) merged[^1] = new Region(merged[^1].Start, run.End);
            else merged.Add(run);
        }
        return merged;
    }

    /// <summary>
    /// The regions of a track of <paramref name="frames"/> samples. Padding is added after
    /// merging and the padded runs are merged again where they touch, so two regions never
    /// share audio. <paramref name="audio"/> is read only to find the quietest place to cut
    /// a stretch that is longer than the cap.
    /// </summary>
    public static IReadOnlyList<Region> Plan(IReadOnlyList<Region> open, long frames, ReadOnlySpan<float> audio,
        double mergeSeconds = MergeSeconds, double padSeconds = PadSeconds, double capSeconds = CapSeconds)
    {
        var pad = (long)(padSeconds * SampleRate);
        var padded = Merge(Merge(open, (long)(mergeSeconds * SampleRate))
            .Select(run => new Region(Math.Max(0, run.Start - pad), Math.Min(frames, run.End + pad))), 1);
        var regions = new List<Region>();
        foreach (var run in padded) regions.AddRange(SplitLong(run, audio, (long)(capSeconds * SampleRate)));
        return regions;
    }

    /// <summary>
    /// Cuts a run into pieces of at most <paramref name="cap"/> samples. Each cut is at the
    /// quietest 100 ms in the last 40 % of what the piece may still take, so a word is cut
    /// only when the speaker never pauses for that long.
    /// </summary>
    public static IReadOnlyList<Region> SplitLong(Region run, ReadOnlySpan<float> audio, long cap)
    {
        var pieces = new List<Region>();
        const int window = SampleRate / 10, step = window / 2;
        var start = run.Start;
        while (run.End - start > cap)
        {
            var low = start + (long)(cap * 0.6);
            var high = start + cap;
            var from = (int)Math.Min(low, audio.Length);
            var segment = audio[from..(int)Math.Min(high + window, audio.Length)];
            var count = Math.Max((segment.Length - window) / step, 1);
            var best = 0;
            var bestPower = double.MaxValue;
            for (var index = 0; index < count; index++)
            {
                var frame = segment.Slice(Math.Min(index * step, segment.Length), Math.Min(window, Math.Max(0, segment.Length - index * step)));
                double sum = 0;
                foreach (var sample in frame) sum += (double)sample * sample;
                var power = frame.Length == 0 ? 0 : sum / frame.Length;
                if (power < bestPower) { bestPower = power; best = index; }
            }
            var cut = low + best * step + step;
            pieces.Add(new Region(start, cut));
            start = cut;
        }
        pieces.Add(new Region(start, run.End));
        return pieces;
    }

    /// <summary>
    /// The gain that brings <paramref name="region"/> to <see cref="TargetDb"/> RMS, measured
    /// over the samples where the gate was open (over all of it when almost none were), at
    /// most <see cref="MaxBoostDb"/>, and low enough that the peak stays under 0.97.
    /// </summary>
    public static float Gain(ReadOnlySpan<float> audio, Region region, IReadOnlyList<Region> open)
    {
        var segment = audio[(int)region.Start..(int)region.End];
        double openSum = 0, allSum = 0, peak = 0;
        long openCount = 0;
        foreach (var run in open)
        {
            var from = Math.Max(run.Start, region.Start);
            var to = Math.Min(run.End, region.End);
            for (var index = from; index < to; index++)
            {
                var sample = audio[(int)index];
                openSum += (double)sample * sample;
                openCount++;
            }
        }
        foreach (var sample in segment)
        {
            allSum += (double)sample * sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }
        var rms = openCount > SampleRate / 20
            ? Math.Sqrt(openSum / openCount)
            : segment.Length > 0 ? Math.Sqrt(allSum / segment.Length) : 0;
        rms += 1e-12;
        var gain = Math.Min(Math.Pow(10, TargetDb / 20) / rms, Math.Pow(10, MaxBoostDb / 20));
        if (peak * gain > 0.97) gain *= 0.97 / (peak * gain);
        return (float)gain;
    }

    /// <summary>
    /// A region's time inside its own file, back to the call: the words of a file that starts
    /// at sample <paramref name="region"/>.Start are that much later.
    /// </summary>
    public static long ToTrackMs(Region region, long regionMs) => region.Start * 1000 / SampleRate + regionMs;

    /// <summary>The gate's decisions, which the echo stage hands to recognition in a small file beside the cleaned mic.</summary>
    public sealed record Gate(int Rate, long Frames, long[][] Open)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public IReadOnlyList<Region> Runs => Open.Select(pair => new Region(pair[0], pair[1])).ToArray();

        public static Gate From(IReadOnlyList<Region> runs, long frames) =>
            new(SampleRate, frames, runs.Select(run => new[] { run.Start, run.End }).ToArray());

        public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this));

        /// <summary>Null when there is no file, it is unreadable, or it is not for a track of this many samples.</summary>
        public static Gate? TryLoad(string path, long frames)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var gate = JsonSerializer.Deserialize<Gate>(File.ReadAllText(path));
                return gate is { Open: not null } && gate.Rate == SampleRate && gate.Frames == frames
                    && gate.Open.All(pair => pair is { Length: 2 } && pair[0] >= 0 && pair[1] > pair[0] && pair[1] <= frames) ? gate : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }
}
