using System.Numerics;

namespace Amanu.Core.Processing;

/// <summary>
/// An echo canceller for a finished recording. An adaptive filter chases the echo path
/// sample by sample; this one has the whole mic track and the whole call track, so it
/// solves the path instead. It finds the echo's delay, the clock drift between the two
/// tracks and a change of device from windowed cross-correlations, warps the call track
/// onto the mic's clock, and fits, per STFT bin, a short filter across frames by weighted
/// least squares over each stretch. The weights are one over the power of what is left
/// (my voice and the noise), so frames where I talk count little and the fit needs no
/// double-talk detector, no step size and no convergence time.
/// The lab version is windows/tests/quality/lab/solve.py; the two must stay in step.
/// </summary>
public static class OfflineEchoSolver
{
    public const int SampleRate = 16_000;

    /// <summary>Samples the echo lags the warped call track: room for a direct sound found late.</summary>
    private const int Lead = 192;

    /// <summary>One stretch of the call with one device and one clock drift: the echo lags by Lag + Slope * t samples.</summary>
    public readonly record struct Section(double Start, double End, double Lag, double Slope);

    /// <param name="ManyPaths">
    /// The recording shows clear echo that no line of delay explains: it joins many devices.
    /// The mic comes back unchanged, and the caller should use an adaptive canceller.
    /// </param>
    public sealed record Result(float[] Cleaned, float[] Echo, IReadOnlyList<Section> Sections, bool ManyPaths = false);

    /// <summary>
    /// A track the solver reads a piece at a time, so that a two-hour call need not sit in memory
    /// twice over: at 16 kHz one track is 460 MB as floats, and the solver needs the mic, the call
    /// track and its own output.
    /// </summary>
    public interface ITrack
    {
        /// <summary>The length in samples; what lies beyond it is silence.</summary>
        int Length { get; }

        /// <summary>Fills <paramref name="destination"/> with the samples from <paramref name="start"/> on; before sample 0 and after <see cref="Length"/> there is silence. Many threads may call this at once.</summary>
        void Read(long start, Span<float> destination);
    }

    /// <summary>A track held in an array, as long as <paramref name="length"/>: the part the array does not reach is silence.</summary>
    public sealed class ArrayTrack(float[] samples, int length) : ITrack
    {
        public int Length { get; } = length;

        public void Read(long start, Span<float> destination)
        {
            destination.Clear();
            var from = Math.Max(0, start);
            var to = Math.Min(Math.Min(samples.Length, Length), start + destination.Length);
            if (to > from) samples.AsSpan((int)from, (int)(to - from)).CopyTo(destination[(int)(from - start)..]);
        }
    }

    private static float[] Slice(ITrack track, long start, int count)
    {
        var slice = new float[Math.Max(0, count)];
        track.Read(start, slice);
        return slice;
    }

    /// <summary>
    /// The mic with the linear echo of the call track taken out, and the echo estimate.
    /// Frames of 256 ms with a hop of 64 ms fit as well as short ones and cost far less.
    /// A long stretch is fitted in blocks of about five minutes: the room changes over a long
    /// call, and only one block's spectra are in memory at a time, not those of two hours.
    /// With no echo found, the mic comes back unchanged.
    /// </summary>
    public static Result Cancel(float[] mic, float[] far, CancellationToken cancellationToken = default, double blockSeconds = 300)
    {
        var echo = Estimate(new ArrayTrack(mic, mic.Length), new ArrayTrack(far, Math.Max(far.Length, mic.Length)), cancellationToken, blockSeconds, out var sections, out var manyPaths);
        if (echo is null) return new Result((float[])mic.Clone(), new float[mic.Length], sections, manyPaths);
        var cleaned = new float[mic.Length];
        for (var i = 0; i < mic.Length; i++) cleaned[i] = mic[i] - echo[i];
        return new Result(cleaned, echo, sections);
    }

    /// <summary>
    /// <see cref="Cancel"/> and, when <paramref name="suppressorFloor"/> is given, <see cref="Suppress"/> for
    /// tracks read piece by piece, keeping one array of the call's length instead of five: the echo
    /// estimate is turned into the cleaned mic in place, and the suppressor works on it in place, taking
    /// the echo it needs back as mic minus cleaned. The result's Echo is empty. When the recording joins
    /// many devices the result's Cleaned is empty too, as no one echo path can be solved.
    /// </summary>
    public static Result Solve(ITrack mic, ITrack far, double? suppressorFloor = null, CancellationToken cancellationToken = default, double blockSeconds = 300)
    {
        var echo = Estimate(mic, far, cancellationToken, blockSeconds, out var sections, out var manyPaths);
        if (manyPaths) return new Result([], [], sections, true);
        if (echo is null) return new Result(Slice(mic, 0, mic.Length), [], sections);
        var buffer = new float[1 << 16];
        for (var position = 0; position < echo.Length; position += buffer.Length)
        {
            var count = Math.Min(buffer.Length, echo.Length - position);
            mic.Read(position, buffer.AsSpan(0, count));
            for (var i = 0; i < count; i++) echo[position + i] = buffer[i] - echo[position + i];
        }
        if (suppressorFloor is { } floor)
            SuppressInPlace(echo, (from, part) =>
            {
                var micPart = Slice(mic, from, part.Length);
                for (var i = 0; i < micPart.Length; i++) micPart[i] -= part[i];
                return micPart;
            }, floor, 2.0, blockSeconds);
        return new Result(echo, [], sections);
    }

    /// <summary>The echo estimate of the whole call, or null when no echo was found.</summary>
    private static float[]? Estimate(ITrack mic, ITrack far, CancellationToken cancellationToken, double blockSeconds, out IReadOnlyList<Section> sections, out bool manyPaths)
    {
        const int n = FrameLength, hop = FrameHop;
        (sections, manyPaths) = Track(mic, far, cancellationToken);
        if (sections.Count == 0) return null;

        var frames = (mic.Length + n) / hop + 1;
        var search = 12 * SampleRate / hop;
        // The first frame whose centre is at or after a time.
        int FrameAt(double seconds) => (int)Math.Clamp(Math.Ceiling((seconds * SampleRate + n / 2.0) / hop), 0, frames);

        var echo = new float[mic.Length];
        Block? pending = null;      // the last block of the stretch before, until it is known where the two meet
        var started = false;
        for (var k = 0; k < sections.Count; k++)
        {
            var section = sections[k];
            int first = FrameAt(section.Start), end = FrameAt(section.End);
            var count = end - first;
            if (count <= 0) continue;
            var blocks = Math.Max(1, (int)Math.Round(count * (double)hop / SampleRate / blockSeconds));
            var lastSection = k == sections.Count - 1;
            var from = first;
            for (var j = 0; j < blocks; j++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CollectBlockGarbage();
                var to = from + count / blocks + (j < count % blocks ? 1 : 0);
                var lastBlock = j == blocks - 1;
                // A stretch's first and last blocks also cover the frames around the place where
                // it meets its neighbour, or up to the call's ends.
                var low = j > 0 ? from : started ? Math.Max(0, first - search) : 0;
                var high = !lastBlock ? to : lastSection ? frames : Math.Min(frames, FrameAt(sections[k + 1].Start) + search);
                var block = Fit(mic, far, section, low, high, from, to, sections.Count > 1);
                block.From = j == 0 && !started ? 0 : from;
                if (j == 0 && pending is not null)
                {
                    // The stretches meet where the earlier model's residual before the frame plus the
                    // later model's after it is least, within 12 s of where the delay points put it.
                    var lo = Math.Max(Math.Max(first - search, pending.Low), block.Low);
                    var hi = Math.Max(lo, Math.Min(Math.Min(first + search, pending.High), block.High));
                    var before = new double[hi - lo + 1];
                    var after = new double[hi - lo + 1];
                    for (var t = lo; t < hi; t++) before[t - lo + 1] = before[t - lo] + pending.Residual[t - pending.Low];
                    for (var t = hi - 1; t >= lo; t--) after[t - lo] = after[t - lo + 1] + block.Residual[t - block.Low];
                    var best = 0;
                    for (var i = 1; i <= hi - lo; i++)
                        if (before[i] + after[i] < before[best] + after[best]) best = i;
                    Stft.AddInverse(pending.Estimate, pending.Low, pending.From, lo + best, echo, n, hop);
                    block.From = lo + best;
                    pending = null;
                }
                started = true;
                if (lastBlock && !lastSection) pending = block;
                else Stft.AddInverse(block.Estimate, block.Low, block.From, lastBlock ? frames : to, echo, n, hop);
                from = to;
            }
        }
        if (pending is not null) Stft.AddInverse(pending.Estimate, pending.Low, pending.From, pending.High, echo, n, hop);
        pending = null;
        CollectBlockGarbage();
        return echo;
    }

    private const int FrameLength = 4096, FrameHop = 1024;

    /// <summary>
    /// A block's spectra are arrays of tens of megabytes. The collector would let those of a dozen
    /// blocks pile up, since it counts them against a budget that grows as the run goes on: a
    /// two-hour call then took 4 GB although it never needed more than 0.8 GB at once.
    /// </summary>
    private static void CollectBlockGarbage() => GC.Collect(2, GCCollectionMode.Forced, blocking: true);

    /// <summary>One block's echo estimate over the frames low..high of the call, and what it leaves of the mic per frame.</summary>
    private sealed record Block(Stft.Spectrum Estimate, double[] Residual, int Low, int High)
    {
        public int From { get; set; }
    }

    /// <summary>The mic's frames low..high; only the stretch of it they cover is read.</summary>
    private static Stft.Spectrum MicSpectrum(ITrack mic, int low, int high)
    {
        const int n = FrameLength, hop = FrameHop;
        var start = low * hop - n;
        return Stft.Forward(Slice(mic, start, (high - low - 1) * hop + n), n, hop, low, high - low, start);
    }

    /// <summary>The frames first..first + count of the call track warped onto the mic's clock.</summary>
    private static Stft.Spectrum CallSpectrum(ITrack far, Section section, int first, int count, int limit)
    {
        const int n = FrameLength, hop = FrameHop;
        var origin = first * hop - n;
        return Stft.Forward(Warp(far, section.Lag, section.Slope, origin, (count - 1) * hop + n, limit, Lead), n, hop, first, count, origin);
    }

    /// <summary>
    /// Fits the echo path of one stretch on the frames from..to and applies it to low..high.
    /// The weights are one over the residual's power, so the frames where I talk count less:
    /// two rounds are as good as three.
    /// </summary>
    private static Block Fit(ITrack mic, ITrack far, Section section, int low, int high, int from, int to, bool wantResidual)
    {
        const int hop = FrameHop, pre = 1, iterations = 2;
        const double tailSeconds = 0.75, ridge = 1e-4, floor = 1e-4;
        var taps = (int)Math.Ceiling(tailSeconds * SampleRate / hop);
        var y = MicSpectrum(mic, low, high);
        // The call track's frames reach taps - 1 before the mic's first frame and `pre` after its last.
        var x = CallSpectrum(far, section, low - taps + 1, high - low + taps - 1 + pre, mic.Length);
        CollectBlockGarbage();                                             // the pieces of the two tracks the spectra were made from
        var rows = new bool[high - low];
        for (var t = Math.Max(from, low); t < Math.Min(to, high); t++) rows[t - low] = true;
        float[]? weights = null;
        Stft.Spectrum d = null!;
        for (var round = 0; round < iterations; round++)
        {
            d = FitAndApply(y, x, taps, pre, weights, ridge, rows);
            // The weights of the last round would not be used, so they are not made.
            if (round < iterations - 1)
            {
                weights = InverseResidualPower(y, d, rows, floor);
                d = null!;
                CollectBlockGarbage();
            }
        }
        var residual = new double[high - low];
        if (wantResidual)
            for (var t = 0; t < residual.Length; t++) residual[t] = ResidualPower(y, d, t);
        return new Block(d, residual, low, high);
    }

    /// <summary>
    /// What the linear stage left of the echo, turned down. The echo left over is taken as a
    /// share of the echo estimate's power, per bin, measured in the frames where only the far
    /// end talks: the frames with a loud estimate in which the residual against the estimate
    /// is within 6 dB of its 10th percentile, with the noise floor taken off. Each bin of each
    /// frame gets the gain 1 - over * (echo left) / (residual power), never below the floor.
    /// </summary>
    public static float[] Suppress(float[] cleaned, float[] echo, double gainFloor = 0.15, double over = 2.0, double blockSeconds = 300)
    {
        var output = (float[])cleaned.Clone();
        SuppressInPlace(output, (from, part) => echo.AsSpan(from, part.Length).ToArray(), gainFloor, over, blockSeconds);
        return output;
    }

    /// <summary>
    /// <see cref="Suppress"/> over the array it is given. <paramref name="echoOf"/> returns the echo
    /// under the stretch of the cleaned mic it is handed, which starts at the sample `from`.
    /// </summary>
    private static void SuppressInPlace(float[] cleaned, Func<int, float[], float[]> echoOf, double gainFloor, double over, double blockSeconds)
    {
        // A long call goes in blocks of about five minutes, each with its own measure of the
        // echo left and of the noise, joined by a short cross-fade.
        var blocks = Math.Max(1, (int)Math.Round(cleaned.Length / (blockSeconds * SampleRate)));
        if (blocks == 1)
        {
            SuppressBlock(cleaned, echoOf(0, cleaned), gainFloor, over).CopyTo(cleaned, 0);
            return;
        }
        const int margin = 2 * SampleRate, fade = 512;
        // The blocks overlap by the margin, and the one before has already written over the start of
        // this one's input, so the input there is kept from before it wrote.
        var kept = Array.Empty<float>();
        for (var b = 0; b < blocks; b++)
        {
            CollectBlockGarbage();
            var start = (int)((long)cleaned.Length * b / blocks);
            var end = (int)((long)cleaned.Length * (b + 1) / blocks);
            var from = Math.Max(0, start - margin);
            var to = Math.Min(cleaned.Length, end + margin);
            var input = cleaned[from..to];
            if (b > 0) kept.AsSpan(0, Math.Min(kept.Length, input.Length)).CopyTo(input);
            var next = cleaned[Math.Max(0, end - margin)..Math.Min(cleaned.Length, end + fade)];
            var part = SuppressBlock(input, echoOf(from, input), gainFloor, over);
            for (var i = start; i < Math.Min(cleaned.Length, end + fade); i++)
            {
                var weight = b > 0 && i < start + fade ? (i - start + 0.5f) / fade : 1f;
                cleaned[i] = weight == 1f ? part[i - from] : cleaned[i] * (1 - weight) + part[i - from] * weight;
            }
            kept = next;
        }
    }

    private static float[] SuppressBlock(float[] cleaned, float[] echo, double gainFloor, double over)
    {
        const int n = 512, hop = 128;
        var e = Stft.Forward(cleaned, n, hop);
        var d = Stft.Forward(echo, n, hop);
        echo = null!;
        int bins = e.Bins, frames = e.Frames;
        // The power of a bin of a frame is made from the spectra where it is wanted, not kept in
        // arrays of doubles twice the size of the spectra (and as many again for their smoothing).
        double Pd(int i) => (double)d.Re[i] * d.Re[i] + (double)d.Im[i] * d.Im[i];
        double Pe(int i) => (double)e.Re[i] * e.Re[i] + (double)e.Im[i] * e.Im[i];
        var fd = new double[frames];
        var fe = new double[frames];
        for (var f = 0; f < bins; f++)
            for (var t = 0; t < frames; t++)
            {
                fd[t] += Pd(f * frames + t);
                fe[t] += Pe(f * frames + t);
            }
        var top = fd.Max();
        if (top <= 0) return cleaned;

        var quiet = Enumerable.Range(0, frames).Where(t => fd[t] <= 1e-6 * top).OrderBy(t => fe[t]).ToArray();
        var noise = new double[bins];
        if (quiet.Length >= 20)
        {
            var count = Math.Max(10, quiet.Length * 3 / 10);
            for (var f = 0; f < bins; f++)
            {
                double sum = 0;
                for (var i = 0; i < count; i++) sum += Pe(f * frames + quiet[i]);
                noise[f] = sum / count;
            }
        }
        var active = Enumerable.Range(0, frames).Where(t => fd[t] > 1e-6 * top).ToArray();
        var median = Percentile(active.Select(t => fd[t]).ToArray(), 50);
        var loud = active.Where(t => fd[t] > median).ToArray();
        if (loud.Length == 0) return cleaned;
        var limit = 4 * Percentile(loud.Select(t => fe[t] / fd[t]).ToArray(), 10);
        var farOnly = loud.Where(t => fe[t] / fd[t] <= limit).ToArray();
        var share = new double[bins];
        for (var f = 0; f < bins; f++)
        {
            double se = 0, sd = 0;
            foreach (var t in farOnly)
            {
                se += Pe(f * frames + t);
                sd += Pd(f * frames + t);
            }
            share[f] = Math.Max(se / farOnly.Length - noise[f], 0) / (sd / farOnly.Length + 1e-20);
        }
        var smoothShare = new double[bins];
        for (var f = 0; f < bins; f++)
        {
            double sum = 0;
            for (var j = -2; j <= 2; j++) sum += share[Math.Clamp(f + j, 0, bins - 1)];
            smoothShare[f] = sum / 5;
        }

        // The mean over three frames of a bin's power; the echo's is smoothed over frames alone, the
        // signal's over three bins too. The gains wait in an array because the neighbours of a bin
        // must see the signal as it was.
        void Across(Func<int, double> power, int bin, double[] row)
        {
            for (var k = 0; k < frames; k++)
            {
                double sum = 0;
                for (var j = -1; j <= 1; j++) sum += power(bin * frames + Math.Clamp(k + j, 0, frames - 1));
                row[k] = sum / 3;
            }
        }
        var gains = new float[bins * frames];
        CollectBlockGarbage();
        // Each thread keeps its four rows: a row is 300 KB, so one new row per bin and use would put
        // a gigabyte through the large-object heap in a two-hour call.
        Parallel.For(0, bins, () => (new double[frames], new double[frames], new double[frames], new double[frames]), (f, _, rows) =>
        {
            var (before, now, after, pdSmooth) = rows;
            Across(Pe, Math.Max(f - 1, 0), before);
            Across(Pe, f, now);
            Across(Pe, Math.Min(f + 1, bins - 1), after);
            Across(Pd, f, pdSmooth);
            double left = 0;
            for (var t = 0; t < frames; t++)
            {
                // The tail of the room rings on after the estimate ends: the echo left decays over frames.
                left = Math.Max(smoothShare[f] * pdSmooth[t], 0.8 * left);
                double sum = 0;
                sum += before[t];
                sum += now[t];
                sum += after[t];
                var peSmooth = sum / 3;
                gains[f * frames + t] = (float)Math.Clamp(1 - over * left / (peSmooth + 1e-20), gainFloor, 1.0);
            }
            return rows;
        }, _ => { });
        for (var i = 0; i < gains.Length; i++)
        {
            e.Re[i] *= gains[i];
            e.Im[i] *= gains[i];
        }
        gains = null!;
        d = null!;
        CollectBlockGarbage();
        return Stft.Inverse(e, cleaned.Length, n, hop);
    }

    // ------------------------------------------------------------ delay, drift, device change

    /// <summary>
    /// The echo's delay over the call: coarse lines from 2 s windows (which a drift of 200 ppm
    /// smears by 6 samples only), then, with the call track warped by them, fine lines from
    /// 8 s windows.
    /// </summary>
    public static (IReadOnlyList<Section> Sections, bool ManyPaths) Track(float[] mic, float[] far, CancellationToken cancellationToken = default) =>
        Track(new ArrayTrack(mic, mic.Length), new ArrayTrack(far, Math.Max(far.Length, mic.Length)), cancellationToken);

    private static (IReadOnlyList<Section> Sections, bool ManyPaths) Track(ITrack mic, ITrack far, CancellationToken cancellationToken)
    {
        var all = GccPoints(mic, far, 2.0, 1.0, 0.6, cancellationToken);
        var coarse = Sections(all, 24.0);
        // Pure noise gives a peak about 6 times the median; an echo, 10 times and more. Many
        // clear peaks that no line explains mean a recording that joins many devices.
        if (coarse.Count == 0) return (coarse, all.Length > 0 && all.Count(p => p.Sharpness >= 10.0) >= 0.3 * all.Length);
        const int lead = 1024;
        var warped = new WarpedTrack(far, coarse, mic.Length, lead);
        var points = GccPoints(mic, warped, 8.0, 4.0, 2.0 * lead / SampleRate, cancellationToken);
        var fine = new List<Section>();
        foreach (var section in coarse)
        {
            var inside = points.Where(p => p.Time >= section.Start && p.Time < section.End && p.Sharpness >= 4.0).ToArray();
            double lag = 0, slope = 0;
            if (inside.Length >= 3)
            {
                var t = inside.Select(p => p.Time).ToArray();
                var d = inside.Select(p => p.Lag - lead).ToArray();           // what the coarse line left
                var ppms = t.Max() - t.Min() >= 120 ? Enumerable.Range(-30, 61).ToArray() : [0];
                (lag, slope, _) = Vote(t, d, ppms, 3.0);
            }
            fine.Add(section with { Lag = section.Lag + lag, Slope = section.Slope + slope });
        }
        return (fine, false);
    }

    private readonly record struct DelayPoint(double Time, double Lag, double Sharpness);

    /// <summary>
    /// Per window: the lag of the echo in samples, to a fraction, and how sharp its peak is.
    /// The lag is the peak of the phase-weighted cross-correlation between 300 and 6000 Hz.
    /// </summary>
    private static DelayPoint[] GccPoints(ITrack mic, ITrack far, double windowSeconds, double hopSeconds, double maxSeconds, CancellationToken cancellationToken)
    {
        int window = (int)(windowSeconds * SampleRate), hop = (int)(hopSeconds * SampleRate), maxLag = (int)(maxSeconds * SampleRate);
        var size = (int)BitOperations.RoundUpToPowerOf2((uint)(window + maxLag));
        double total = 0;
        var chunk = new float[1 << 16];
        for (var position = 0; position < far.Length; position += chunk.Length)
        {
            far.Read(position, chunk);
            for (var i = 0; i < chunk.Length && position + i < far.Length; i++) total += (double)chunk[i] * chunk[i];
        }
        var level = total / Math.Max(1, far.Length) * 0.05;
        var count = mic.Length >= window ? (mic.Length - window) / hop + 1 : 0;
        var points = new DelayPoint?[count];
        int lowBin = (int)Math.Ceiling(300.0 * size / SampleRate), highBin = (int)Math.Floor(6000.0 * size / SampleRate);
        Parallel.For(0, count, new ParallelOptions { CancellationToken = cancellationToken }, index =>
        {
            var start = index * hop;
            var fr = new float[size];
            far.Read(start, fr.AsSpan(0, window));
            double power = 0;
            for (var i = 0; i < window; i++) power += (double)fr[i] * fr[i];
            if (power / window < level) return;
            float[] mr = new float[size], mi = new float[size], fi = new float[size];
            mic.Read(start, mr.AsSpan(0, window));
            Fft.Transform(mr, mi, false);
            Fft.Transform(fr, fi, false);
            for (var k = 0; k < size; k++)
            {
                var bin = k <= size / 2 ? k : size - k;
                if (bin <= lowBin || bin >= highBin) { mr[k] = 0; mi[k] = 0; continue; }
                // mic times the conjugate of far, with the magnitude taken out.
                var re = mr[k] * fr[k] + mi[k] * fi[k];
                var im = mi[k] * fr[k] - mr[k] * fi[k];
                var magnitude = MathF.Sqrt(re * re + im * im) + 1e-20f;
                mr[k] = re / magnitude;
                mi[k] = im / magnitude;
            }
            Fft.Transform(mr, mi, true);
            var c = new float[maxLag];
            var peak = 0;
            for (var k = 0; k < maxLag; k++)
            {
                c[k] = MathF.Abs(mr[k]);
                if (c[k] > c[peak]) peak = k;
            }
            double fraction = 0;
            if (peak > 0 && peak < maxLag - 1)
            {
                double l = c[peak - 1], m = c[peak], r = c[peak + 1];
                fraction = 0.5 * (l - r) / (l - 2 * m + r + 1e-20);
            }
            var height = c[peak];
            Array.Sort(c);
            var median = (c[(maxLag - 1) / 2] + c[maxLag / 2]) / 2.0;
            points[index] = new DelayPoint((start + window / 2.0) / SampleRate, peak + fraction, height / (median + 1e-20));
        });
        return points.Where(p => p is not null).Select(p => p!.Value).ToArray();
    }

    /// <summary>
    /// The line d = i + s t that the most points lie within tol of, by a vote over slopes
    /// (ppm) and offsets, then least squares on those points.
    /// </summary>
    private static (double Lag, double Slope, bool[] Near) Vote(double[] t, double[] d, int[] ppms, double tol)
    {
        int bestCount = 0;
        double slope = 0, lag = 0;
        var r = new double[t.Length];
        foreach (var ppm in ppms)
        {
            var s = ppm * 1e-6 * SampleRate;
            for (var i = 0; i < t.Length; i++) r[i] = d[i] - s * t[i];
            Array.Sort(r);
            int bestStart = 0, bestEnd = 0, end = 0;
            for (var start = 0; start < r.Length; start++)
            {
                while (end < r.Length && r[end] <= r[start] + 2 * tol) end++;
                if (end - start > bestEnd - bestStart) { bestStart = start; bestEnd = end; }
            }
            if (bestEnd - bestStart > bestCount)
            {
                bestCount = bestEnd - bestStart;
                slope = s;
                var span = bestEnd - bestStart;
                lag = (r[bestStart + (span - 1) / 2] + r[bestStart + span / 2]) / 2;
            }
        }
        var near = new bool[t.Length];
        for (var round = 0; round < 3; round++)
        {
            var inside = Enumerable.Range(0, t.Length).Where(i => Math.Abs(d[i] - (lag + slope * t[i])) <= tol).ToArray();
            if (inside.Length == 0) break;
            var tt = inside.Select(i => t[i]).ToArray();
            var dd = inside.Select(i => d[i]).ToArray();
            if (inside.Length >= 6 && tt.Max() - tt.Min() >= 20)
            {
                double mt = tt.Average(), md = dd.Average(), stt = 0, std = 0;
                for (var i = 0; i < tt.Length; i++)
                {
                    stt += (tt[i] - mt) * (tt[i] - mt);
                    std += (tt[i] - mt) * (dd[i] - md);
                }
                slope = std / stt;
                lag = md - slope * mt;
            }
            else lag = Percentile(inside.Select(i => d[i] - slope * t[i]).ToArray(), 50);
        }
        for (var i = 0; i < t.Length; i++) near[i] = Math.Abs(d[i] - (lag + slope * t[i])) <= tol;
        return (lag, slope, near);
    }

    /// <summary>
    /// Splits the delay points into stretches that each lie on one line (one device, one clock
    /// drift). Points that agree with no line (noise, my own voice, a loud reflection) are left
    /// out; a call with no echo gives no line at all. A second line counts as another device
    /// only when its points come in one run of five or more that the first line's points do
    /// not enclose: a reflection of the same room shows up between points of the direct sound.
    /// </summary>
    private static List<Section> Sections(DelayPoint[] all, double tol, int minPoints = 8)
    {
        var points = all.Where(p => p.Sharpness >= 4.0).ToArray();
        var t = points.Select(p => p.Time).ToArray();
        var d = points.Select(p => p.Lag).ToArray();
        var label = Enumerable.Repeat(-1, points.Length).ToArray();
        var found = new List<(double Lag, double Slope, double Span)>();
        var slopes = Enumerable.Range(0, 61).Select(i => -300 + 10 * i).ToArray();
        for (var k = 0; k < 3; k++)
        {
            var free = Enumerable.Range(0, points.Length).Where(i => label[i] < 0).ToArray();
            if (free.Length < minPoints) break;
            var (lag, slope, near) = Vote(free.Select(i => t[i]).ToArray(), free.Select(i => d[i]).ToArray(), slopes, tol);
            var members = free.Where((_, j) => near[j]).ToArray();
            var needed = k == 0 ? Math.Max(minPoints, 0.2 * points.Length) : Math.Max(6, 0.1 * points.Length);
            if (members.Length < needed) break;
            foreach (var i in members) label[i] = k;
            found.Add((lag, slope, members.Max(i => t[i]) - members.Min(i => t[i])));
        }
        if (found.Count == 0) return [];
        for (var k = 1; k < found.Count; k++)
        {
            if (found[k].Span >= 120) continue;                            // a short stretch borrows the main line's drift
            var slope = found[0].Slope;
            var members = Enumerable.Range(0, points.Length).Where(i => label[i] == k).Select(i => d[i] - slope * t[i]).ToArray();
            found[k] = (Percentile(members, 50), slope, found[k].Span);
        }

        var runs = new List<(int Label, double First, double Last, int Count)>();
        for (var i = 0; i < points.Length; i++)
        {
            if (label[i] < 0) continue;
            if (runs.Count > 0 && runs[^1].Label == label[i]) runs[^1] = (label[i], runs[^1].First, t[i], runs[^1].Count + 1);
            else runs.Add((label[i], t[i], t[i], 1));
        }
        var changed = true;
        while (changed)                                                    // drop short runs and runs inside another line's runs
        {
            changed = false;
            for (var k = 0; k < runs.Count; k++)
            {
                var inside = k > 0 && k < runs.Count - 1 && runs[k - 1].Label == runs[k + 1].Label && runs[k].Count < runs[k - 1].Count + runs[k + 1].Count;
                if (runs.Count > 1 && (runs[k].Count < 5 || inside))
                {
                    runs.RemoveAt(k);
                    changed = true;
                    break;
                }
            }
            for (var k = runs.Count - 1; k > 0; k--)
                if (runs[k].Label == runs[k - 1].Label)
                {
                    runs[k - 1] = (runs[k].Label, runs[k - 1].First, runs[k].Last, runs[k - 1].Count + runs[k].Count);
                    runs.RemoveAt(k);
                }
        }
        var sections = new List<Section>();
        for (var k = 0; k < runs.Count; k++)
        {
            var start = k == 0 ? 0.0 : (runs[k - 1].Last + runs[k].First) / 2;
            var end = k == runs.Count - 1 ? double.PositiveInfinity : (runs[k].Last + runs[k + 1].First) / 2;
            sections.Add(new Section(start, end, found[runs[k].Label].Lag, found[runs[k].Label].Slope));
        }
        return sections;
    }

    private const int SincTaps = 32, SincPhases = 1024;
    private static readonly float[] SincTable = BuildSincTable();

    private static float[] BuildSincTable()
    {
        var table = new float[(SincPhases + 1) * SincTaps];
        for (var p = 0; p <= SincPhases; p++)
            for (var j = 0; j < SincTaps; j++)
            {
                var x = j - SincTaps / 2 + 1 - (double)p / SincPhases;
                var sinc = Math.Abs(x) < 1e-12 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
                table[p * SincTaps + j] = (float)(sinc * (0.5 + 0.5 * Math.Cos(Math.PI * x / (SincTaps / 2 + 1))));
            }
        return table;
    }

    /// <summary>
    /// The call track as the mic heard it: sample g of the call is far(g - lag(g) + lead), by
    /// windowed-sinc interpolation. The result holds the samples origin..origin + length of
    /// the call; those before the call's start or at and after `limit` are silence.
    /// </summary>
    private static float[] Warp(ITrack far, double lag, double slope, int origin, int length, int limit, int lead)
    {
        var output = new float[length];
        // The call track is read only where the interpolation reaches: its lag is a few seconds at most,
        // and a whole call of it need not be in memory for a stretch of five minutes.
        var first = Math.Max(origin, 0);
        var last = Math.Min(origin + length, limit) - 1;
        if (last < first) return output;
        double Position(long g) => g - (lag + slope * g / SampleRate) + lead;
        var low = (long)Math.Floor(Math.Min(Position(first), Position(last))) - SincTaps / 2;
        var high = (long)Math.Floor(Math.Max(Position(first), Position(last))) + SincTaps / 2 + 1;
        low = Math.Max(low, 0);
        high = Math.Min(high, far.Length);
        var source = Slice(far, low, (int)Math.Max(0, high - low));
        Parallel.For(0, (length + 65535) / 65536, block =>
        {
            var end = Math.Min(length, (block + 1) * 65536);
            for (var i = block * 65536; i < end; i++)
            {
                var g = origin + i;
                if (g < 0 || g >= limit) continue;
                var position = Position(g);
                if (position < 0 || position >= far.Length) continue;
                var whole = (int)Math.Floor(position);
                var row = (int)Math.Round((position - whole) * SincPhases) * SincTaps;
                float sum = 0;
                for (var j = 0; j < SincTaps; j++)
                {
                    var index = whole + j - SincTaps / 2 + 1;
                    if ((uint)index < (uint)far.Length) sum += source[index - low] * SincTable[row + j];
                }
                output[i] = sum;
            }
        });
        return output;
    }

    /// <summary>The call track warped by the coarse lines of the delay, each over its own stretch, made a window at a time.</summary>
    private sealed class WarpedTrack(ITrack far, IReadOnlyList<Section> sections, int length, int lead) : ITrack
    {
        public int Length { get; } = length;

        public void Read(long start, Span<float> destination)
        {
            destination.Clear();
            foreach (var section in sections)
            {
                var from = (int)Math.Clamp(section.Start * SampleRate, 0, Length);
                var to = double.IsInfinity(section.End) ? Length : (int)Math.Clamp(section.End * SampleRate, 0, Length);
                var low = (int)Math.Max(from, start);
                var high = (int)Math.Min(to, start + destination.Length);
                if (high <= low) continue;
                Warp(far, section.Lag, section.Slope, low, high - low, Length, lead).CopyTo(destination[(int)(low - start)..]);
            }
        }
    }

    // ------------------------------------------------------------ the fit

    /// <summary>
    /// Per bin, the filter G with Y[t] ~ sum_k G[k] X[t + pre - k] by weighted least squares
    /// over the frames in `rows`, applied to every frame of y: the echo estimate. x starts
    /// taps - 1 frames before y and ends `pre` frames after it, so tap k of y's frame t is
    /// x's frame t + taps + pre - 1 - k.
    /// </summary>
    private static Stft.Spectrum FitAndApply(Stft.Spectrum y, Stft.Spectrum x, int taps, int pre, float[]? weights, double ridge, bool[] rows)
    {
        int bins = y.Bins, frames = y.Frames, wide = x.Frames, lt = pre + taps;
        var energy = new double[wide];
        for (var f = 0; f < bins; f++)
            for (var t = 0; t < wide; t++)
                energy[t] += Math.Abs(x.Re[f * wide + t]) + Math.Abs(x.Im[f * wide + t]);
        // A frame that no sound of the call reaches says nothing about the echo path.
        var active = Enumerable.Range(0, frames).Where(t => rows[t] && Enumerable.Range(t, lt).Any(source => energy[source] > 0)).ToArray();
        var d = new Stft.Spectrum(bins, frames);
        if (active.Length < lt) return d;

        Parallel.For(0, bins, f =>
        {
            int offset = f * frames, wideOffset = f * wide + lt - 1;
            var rr = new double[lt * lt];
            var ri = new double[lt * lt];
            double[] pr = new double[lt], pi = new double[lt];
            float[] ar = new float[lt], ai = new float[lt];
            foreach (var t in active)
            {
                var w = weights is null ? 1f : weights[offset + t];
                for (var k = 0; k < lt; k++)
                {
                    ar[k] = x.Re[wideOffset + t - k];
                    ai[k] = x.Im[wideOffset + t - k];
                }
                float yr = y.Re[offset + t], yi = y.Im[offset + t];
                for (var a = 0; a < lt; a++)
                {
                    // R += w conj(A[a]) A[b] on and below the diagonal, p += w conj(A[a]) y.
                    float car = ar[a] * w, cai = -ai[a] * w;
                    pr[a] += car * yr - cai * yi;
                    pi[a] += car * yi + cai * yr;
                    var row = a * lt;
                    for (var b = 0; b <= a; b++)
                    {
                        rr[row + b] += car * ar[b] - cai * ai[b];
                        ri[row + b] += car * ai[b] + cai * ar[b];
                    }
                }
            }
            double trace = 0;
            for (var a = 0; a < lt; a++) trace += rr[a * lt + a];
            var load = ridge * trace / lt + 1e-12;
            for (var a = 0; a < lt; a++) rr[a * lt + a] += load;
            SolveHermitian(rr, ri, pr, pi, lt);
            for (var t = 0; t < frames; t++)
            {
                double sr = 0, si = 0;
                for (var k = 0; k < lt; k++)
                {
                    float xr = x.Re[wideOffset + t - k], xi = x.Im[wideOffset + t - k];
                    sr += pr[k] * xr - pi[k] * xi;
                    si += pr[k] * xi + pi[k] * xr;
                }
                d.Re[offset + t] = (float)sr;
                d.Im[offset + t] = (float)si;
            }
        });
        return d;
    }

    /// <summary>
    /// Solves R g = p in place (the answer lands in p) by a Cholesky factorization R = L L^H.
    /// Only the lower triangle of R is read.
    /// </summary>
    private static void SolveHermitian(double[] rr, double[] ri, double[] pr, double[] pi, int n)
    {
        for (var j = 0; j < n; j++)
        {
            var diagonal = rr[j * n + j];
            for (var k = 0; k < j; k++) diagonal -= rr[j * n + k] * rr[j * n + k] + ri[j * n + k] * ri[j * n + k];
            diagonal = Math.Sqrt(Math.Max(diagonal, 1e-30));
            rr[j * n + j] = diagonal;
            ri[j * n + j] = 0;
            for (var i = j + 1; i < n; i++)
            {
                double sr = rr[i * n + j], si = ri[i * n + j];
                for (var k = 0; k < j; k++)
                {
                    // L[i,k] conj(L[j,k])
                    sr -= rr[i * n + k] * rr[j * n + k] + ri[i * n + k] * ri[j * n + k];
                    si -= ri[i * n + k] * rr[j * n + k] - rr[i * n + k] * ri[j * n + k];
                }
                rr[i * n + j] = sr / diagonal;
                ri[i * n + j] = si / diagonal;
            }
        }
        for (var i = 0; i < n; i++)                                        // L z = p
        {
            double sr = pr[i], si = pi[i];
            for (var k = 0; k < i; k++)
            {
                sr -= rr[i * n + k] * pr[k] - ri[i * n + k] * pi[k];
                si -= rr[i * n + k] * pi[k] + ri[i * n + k] * pr[k];
            }
            pr[i] = sr / rr[i * n + i];
            pi[i] = si / rr[i * n + i];
        }
        for (var i = n - 1; i >= 0; i--)                                   // L^H g = z
        {
            double sr = pr[i], si = pi[i];
            for (var k = i + 1; k < n; k++)
            {
                // conj(L[k,i]) g[k]
                sr -= rr[k * n + i] * pr[k] + ri[k * n + i] * pi[k];
                si -= rr[k * n + i] * pi[k] - ri[k * n + i] * pr[k];
            }
            pr[i] = sr / rr[i * n + i];
            pi[i] = si / rr[i * n + i];
        }
    }

    /// <summary>One over the residual's power, smoothed over 5 frames and 3 bins, with a floor 40 dB under each bin's mean over the stretch.</summary>
    private static float[] InverseResidualPower(Stft.Spectrum y, Stft.Spectrum d, bool[] rows, double floor)
    {
        int bins = y.Bins, frames = y.Frames;
        // One bin's smoothed power at a time, from y and d, with its neighbours' two rows beside it:
        // an array of doubles for the whole block would be as large as y and d together.
        double[] Across(int bin)
        {
            var row = new double[frames];
            for (var k = 0; k < frames; k++)
            {
                double sum = 0;
                for (var j = -2; j <= 2; j++)
                {
                    var i = bin * frames + Math.Clamp(k + j, 0, frames - 1);
                    double re = y.Re[i] - d.Re[i], im = y.Im[i] - d.Im[i];
                    sum += re * re + im * im;
                }
                row[k] = sum / 5;
            }
            return row;
        }
        var weights = new float[bins * frames];
        var count = Math.Max(1, rows.Count(r => r));
        var smooth = new double[frames];
        double[] before = Across(0), now = before, after = bins > 1 ? Across(1) : now;
        for (var f = 0; f < bins; f++)
        {
            double mean = 0;
            for (var t = 0; t < frames; t++)
            {
                double sum = 0;
                sum += before[t];
                sum += now[t];
                sum += after[t];
                smooth[t] = sum / 3;
                if (rows[t]) mean += smooth[t];
            }
            var least = floor * mean / count;
            for (var t = 0; t < frames; t++) weights[f * frames + t] = (float)(1.0 / Math.Max(Math.Max(smooth[t], least), 1e-30));
            before = now;
            now = after;
            after = f + 2 < bins ? Across(f + 2) : now;
        }
        return weights;
    }

    private static double ResidualPower(Stft.Spectrum y, Stft.Spectrum d, int frame)
    {
        double sum = 0;
        for (var f = 0; f < y.Bins; f++)
        {
            double re = y.Re[f * y.Frames + frame] - d.Re[f * y.Frames + frame], im = y.Im[f * y.Frames + frame] - d.Im[f * y.Frames + frame];
            sum += re * re + im * im;
        }
        return sum;
    }

    /// <summary>The q-th percentile with linear interpolation between ranks, as numpy's.</summary>
    private static double Percentile(double[] values, double q)
    {
        if (values.Length == 0) return 0;
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        var rank = q / 100 * (sorted.Length - 1);
        var low = (int)Math.Floor(rank);
        var high = Math.Min(sorted.Length - 1, low + 1);
        return sorted[low] + (rank - low) * (sorted[high] - sorted[low]);
    }
}

/// <summary>A short-time Fourier transform with a square-root Hann window, and its inverse.</summary>
public static class Stft
{
    /// <summary>Bins by frames, real and imaginary parts apart: element (f, t) is at f * Frames + t.</summary>
    public sealed class Spectrum(int bins, int frames)
    {
        public int Bins { get; } = bins;
        public int Frames { get; } = frames;
        public float[] Re { get; } = new float[bins * frames];
        public float[] Im { get; } = new float[bins * frames];
    }

    private static float[] Window(int n)
    {
        var w = new float[n];
        for (var i = 0; i < n; i++) w[i] = (float)Math.Sqrt(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n));
        return w;
    }

    /// <summary>Frames of n samples every hop samples, over the signal with n samples of silence on both sides.</summary>
    public static Spectrum Forward(float[] x, int n, int hop) => Forward(x, n, hop, 0, (x.Length + n) / hop + 1);

    /// <summary>
    /// The frames firstFrame..firstFrame + frames of that same grid. x[0] is sample `origin`
    /// of the signal, and what x does not hold is silence.
    /// </summary>
    public static Spectrum Forward(float[] x, int n, int hop, int firstFrame, int frames, int origin = 0)
    {
        var bins = n / 2 + 1;
        var spectrum = new Spectrum(bins, frames);
        var window = Window(n);
        Parallel.For(0, frames, () => (new float[n], new float[n]), (t, _, buffers) =>
        {
            var (re, im) = buffers;
            Array.Clear(im);
            var start = (firstFrame + t) * hop - n - origin;
            for (var i = 0; i < n; i++)
                re[i] = (uint)(start + i) < (uint)x.Length ? x[start + i] * window[i] : 0;
            Fft.Transform(re, im, false);
            for (var f = 0; f < bins; f++)
            {
                spectrum.Re[f * frames + t] = re[f];
                spectrum.Im[f * frames + t] = im[f];
            }
            return buffers;
        }, _ => { });
        return spectrum;
    }

    /// <summary>
    /// Adds the grid's frames from..to as a signal to `output`. The spectrum's first frame is
    /// frame `first` of the grid, so the blocks of one call can be added one after the other.
    /// </summary>
    public static void AddInverse(Spectrum spectrum, int first, int from, int to, float[] output, int n, int hop)
    {
        int frames = spectrum.Frames, bins = spectrum.Bins;
        var window = Window(n);
        var phases = n / hop;
        var scale = (float)(1.0 / (n / hop / 2.0));
        // Frames that share a phase do not overlap, so each phase can be added by many threads.
        for (var phase = 0; phase < phases; phase++)
        {
            var start = from + ((phase - from) % phases + phases) % phases;
            if (start >= to) continue;
            Parallel.For(0, (to - start + phases - 1) / phases, () => (new float[n], new float[n]), (index, _, buffers) =>
            {
                var t = start + index * phases;
                var local = t - first;
                var (re, im) = buffers;
                for (var f = 0; f < bins; f++)
                {
                    re[f] = spectrum.Re[f * frames + local];
                    im[f] = spectrum.Im[f * frames + local];
                }
                for (var f = bins; f < n; f++)
                {
                    re[f] = re[n - f];
                    im[f] = -im[n - f];
                }
                Fft.Transform(re, im, true);
                var target = t * hop - n;
                for (var i = 0; i < n; i++)
                    if ((uint)(target + i) < (uint)output.Length) output[target + i] += re[i] * window[i] * scale;
                return buffers;
            }, _ => { });
        }
    }

    public static float[] Inverse(Spectrum spectrum, int length, int n, int hop)
    {
        int frames = spectrum.Frames, bins = spectrum.Bins;
        var window = Window(n);
        var sum = new double[(frames - 1) * hop + n];
        var phases = n / hop;
        // Frames that share a phase do not overlap, so each phase can be added by many threads.
        for (var phase = 0; phase < phases; phase++)
        {
            var first = phase;
            Parallel.For(0, (frames - first + phases - 1) / phases, () => (new float[n], new float[n]), (index, _, buffers) =>
            {
                var t = first + index * phases;
                var (re, im) = buffers;
                for (var f = 0; f < bins; f++)
                {
                    re[f] = spectrum.Re[f * frames + t];
                    im[f] = spectrum.Im[f * frames + t];
                }
                for (var f = bins; f < n; f++)
                {
                    re[f] = re[n - f];
                    im[f] = -im[n - f];
                }
                Fft.Transform(re, im, true);
                for (var i = 0; i < n; i++) sum[t * hop + i] += re[i] * window[i];
                return buffers;
            }, _ => { });
        }
        var output = new float[length];
        var scale = 1.0 / (n / hop / 2.0);
        for (var i = 0; i < length && n + i < sum.Length; i++) output[i] = (float)(sum[n + i] * scale);
        return output;
    }
}

/// <summary>An in-place radix-2 FFT. The inverse divides by the length.</summary>
public static class Fft
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (float[] Cos, float[] Sin, int[] Reverse)> Tables = new();

    public static void Transform(float[] re, float[] im, bool inverse)
    {
        var n = re.Length;
        if (!BitOperations.IsPow2(n)) throw new ArgumentException("the FFT length must be a power of two", nameof(re));
        var (cos, sin, reverse) = Tables.GetOrAdd(n, Build);
        for (var i = 0; i < n; i++)
        {
            var j = reverse[i];
            if (j <= i) continue;
            (re[i], re[j]) = (re[j], re[i]);
            (im[i], im[j]) = (im[j], im[i]);
        }
        for (var size = 2; size <= n; size <<= 1)
        {
            int half = size >> 1, step = n / size;
            for (var start = 0; start < n; start += size)
                for (var k = 0; k < half; k++)
                {
                    float wr = cos[k * step], wi = inverse ? sin[k * step] : -sin[k * step];
                    int a = start + k, b = a + half;
                    float tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                }
        }
        if (!inverse) return;
        var scale = 1f / n;
        for (var i = 0; i < n; i++)
        {
            re[i] *= scale;
            im[i] *= scale;
        }
    }

    private static (float[], float[], int[]) Build(int n)
    {
        var cos = new float[n / 2];
        var sin = new float[n / 2];
        for (var k = 0; k < n / 2; k++)
        {
            cos[k] = (float)Math.Cos(2 * Math.PI * k / n);
            sin[k] = (float)Math.Sin(2 * Math.PI * k / n);
        }
        var bits = BitOperations.Log2((uint)n);
        var reverse = new int[n];
        for (var i = 0; i < n; i++)
        {
            var r = 0;
            for (var b = 0; b < bits; b++)
                if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            reverse[i] = r;
        }
        return (cos, sin, reverse);
    }
}
