using System.Diagnostics;
using System.Text.Json;
using Amanu.Core.Configuration;
using Amanu.Core.Processing;
using Xunit;
using Xunit.Abstractions;

namespace Amanu.Core.Tests;

public sealed class OfflineEchoTests(ITestOutputHelper output)
{
    /// <summary>LocalVQE's one-hop latency with a known transform: emits the previous mic hop, halved.</summary>
    private sealed class DelayedBackend(float gain = 0.5f) : IEchoCancellationBackend
    {
        private readonly float[] pending = new float[256];
        public List<float[]> Microphones { get; } = [];
        public List<float[]> References { get; } = [];
        public int SampleRate => 16_000;
        public int HopSize => 256;

        public void Process(ReadOnlySpan<float> microphone, ReadOnlySpan<float> reference, Span<float> output)
        {
            pending.CopyTo(output);
            Microphones.Add(microphone.ToArray());
            References.Add(reference.ToArray());
            for (var index = 0; index < pending.Length; index++) pending[index] = microphone[index] * gain;
        }

        public void Dispose() { }
    }

    /// <summary>A perfect echo suppressor with LocalVQE's latency: any raw sample in the output came from the selector.</summary>
    private sealed class DelayedSilenceBackend : IEchoCancellationBackend
    {
        public int SampleRate => 16_000;
        public int HopSize => 256;
        public void Process(ReadOnlySpan<float> microphone, ReadOnlySpan<float> reference, Span<float> output) => output.Clear();
        public void Dispose() { }
    }

    private static float[] Signal(ulong seed, int count)
    {
        var state = seed;
        float low = 0;
        var result = new float[count];
        for (var index = 0; index < count; index++)
        {
            state = unchecked(state * 6364136223846793005UL + 1);
            var noise = (int)(state >> 32) / (float)int.MaxValue;
            low = 0.7f * low + 0.3f * noise;
            result[index] = low * 0.6f;
        }
        return result;
    }

    private static (float[] Mic, float[] System) Run(float[] mic, float[] reference, IEchoCancellationBackend? backend, long? frames = null) =>
        Run(mic, reference, backend, out _, frames);

    /// <summary>As production does it: the delay prime over the whole aligned signal, then the stream from t=0.</summary>
    private static (float[] Mic, float[] System) Run(float[] mic, float[] reference, IEchoCancellationBackend? backend, out DelayPrime? prime, long? frames = null)
    {
        var cleaned = new List<float>();
        var system = new List<float>();
        var length = frames ?? Math.Max(mic.Length, reference.Length);
        using var canceller = backend is null ? null : new EchoCanceller(backend);
        prime = canceller?.PrimeDelay(new ArraySampleSource(mic), new ArraySampleSource(reference), length);
        OfflineEcho.Run(new ArraySampleSource(mic), new ArraySampleSource(reference), length,
            canceller, samples => cleaned.AddRange(samples.ToArray()), samples => system.AddRange(samples.ToArray()));
        return ([.. cleaned], [.. system]);
    }

    /// <summary>Several blocks at once must give the same samples as one stream, hangover and partial last hop included.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void Parallel_blocks_match_one_stream(int workers)
    {
        const int frames = 16_000 * 9 + 137;
        var mic = Signal(11, frames);
        var reference = new float[frames];
        // Playback early, then silence longer than the one-second hangover, then playback again.
        Array.Copy(Signal(12, 20_000), 3_000, reference, 3_000, 17_000);
        Array.Copy(Signal(13, 20_000), 0, reference, 100_000, 20_000);
        var (expected, _) = Run(mic, reference, new DelayedBackend());
        var cancellers = Enumerable.Range(0, workers).Select(_ => new EchoCanceller(new DelayedBackend())).ToList();
        var cleaned = new float[frames];
        try
        {
            OfflineEcho.RunParallel(() => new ArraySampleSource(mic), () => new ArraySampleSource(reference), frames, cancellers, 1_024, cleaned);
        }
        finally { foreach (var canceller in cancellers) canceller.Dispose(); }
        Assert.Equal(expected, cleaned);
    }

    /// <summary>Records the order of calls; End answers with <paramref name="endCode"/>, Begin with <paramref name="beginCode"/>.</summary>
    private sealed class PrimingBackend(int beginCode, int endCode) : IEchoCancellationBackend, IEchoDelayPrimer
    {
        public List<string> Calls { get; } = [];
        public long PrimedSamples { get; private set; }
        public int SampleRate => 16_000;
        public int HopSize => 256;

        public int BeginDelayPrime() { Calls.Add("begin"); return beginCode; }

        public int FeedDelayPrime(ReadOnlySpan<float> microphone, ReadOnlySpan<float> reference)
        {
            Assert.Equal(microphone.Length, reference.Length);
            Assert.Equal(0, microphone.Length % DelayPrime.Block);
            Calls.Add("feed");
            PrimedSamples += microphone.Length;
            return 0;
        }

        public int EndDelayPrime() { Calls.Add("end"); return endCode; }

        public void Process(ReadOnlySpan<float> microphone, ReadOnlySpan<float> reference, Span<float> output)
        {
            Calls.Add("process");
            // LocalVQE's one-hop latency, unchanged audio.
            previous.CopyTo(output);
            microphone.CopyTo(previous);
        }

        private readonly float[] previous = new float[256];

        public void Dispose() { }
    }

    [Fact]
    public void The_delay_prime_scans_the_whole_signal_before_any_hop_is_processed()
    {
        var frames = 2 * OfflineEcho.WindowSamples + 1000;
        var backend = new PrimingBackend(0, 480);
        var (cleaned, _) = Run(Signal(3, frames), Signal(4, frames), backend, out var prime);

        Assert.Equal(new DelayPrime(480, "primed"), prime);
        Assert.Equal("480", prime!.Describe());
        var firstProcess = backend.Calls.IndexOf("process");
        Assert.Equal(["begin", "feed", "feed", "feed", "end"], backend.Calls.Take(firstProcess));
        Assert.DoesNotContain("begin", backend.Calls.Skip(firstProcess));
        // Every whole 128-sample block once; only the partial tail is left out.
        Assert.Equal(frames - frames % DelayPrime.Block, backend.PrimedSamples);
        Assert.Equal(frames, cleaned.Length);
    }

    [Theory]
    [InlineData(0, DelayPrime.NotConfident, "not_confident")]
    [InlineData(DelayPrime.NoFrontEnd, 0, "no_daf_frontend")]
    public void A_failed_delay_prime_falls_back_to_online_acquisition(int begin, int end, string status)
    {
        var frames = OfflineEcho.WindowSamples / 2;
        var mic = Signal(5, frames);
        var backend = new PrimingBackend(begin, end);
        var (cleaned, _) = Run(mic, Signal(6, frames), backend, out var prime);

        Assert.False(prime!.Applied);
        Assert.Equal(status, prime.Status);
        Assert.Equal(status, prime.Describe());
        // The stream still runs and is still exact for this pass-through backend.
        Assert.Equal(mic, cleaned);
        if (begin < 0) Assert.DoesNotContain("feed", backend.Calls);
    }

    [Fact]
    public void A_backend_without_the_prime_keeps_online_acquisition()
    {
        using var canceller = new EchoCanceller(new DelayedBackend());
        var prime = canceller.PrimeDelay(new ArraySampleSource(new float[1024]), new ArraySampleSource(new float[1024]), 1024);
        Assert.Equal("unsupported_library", prime.Describe());

        var output = new float[256];
        canceller.Process(new float[256], new float[256], output);
        Assert.Throws<EchoCancellationException>(() => canceller.PrimeDelay(new ArraySampleSource([]), new ArraySampleSource([]), 0));
    }

    [Fact]
    public void Windows_are_937_whole_hops()
    {
        Assert.Equal(239_872, OfflineEcho.WindowSamples);
        Assert.Equal(0, OfflineEcho.WindowSamples % EchoCanceller.FrameSize);
        Assert.Equal(937, OfflineEcho.WindowSamples / EchoCanceller.FrameSize);
    }

    [Fact]
    public void The_first_delayed_hop_is_discarded_and_flushing_keeps_the_final_hop()
    {
        using var canceller = new EchoCanceller(new DelayedBackend());
        var first = Enumerable.Repeat(0.4f, 256).ToArray();
        var second = Enumerable.Repeat(0.8f, 256).ToArray();
        var reference = Enumerable.Repeat(0.2f, 256).ToArray();
        var output = new float[256];

        Assert.False(canceller.Process(first, reference, output));
        Assert.True(canceller.Process(second, reference, output));
        Assert.Equal(first.Select(x => x * 0.5f), output);
        Assert.True(canceller.Finish(output));
        Assert.Equal(second.Select(x => x * 0.5f), output);
        Assert.False(canceller.Finish(output));
        Assert.Throws<EchoCancellationException>(() => canceller.Process(first, reference, output));
    }

    [Fact]
    public void Reference_absent_from_stream_start_keeps_the_mic_exact()
    {
        using var canceller = new EchoCanceller(new DelayedBackend());
        var local = Signal(52, 256);
        var following = Signal(53, 256);
        var silence = new float[256];
        var output = new float[256];

        Assert.False(canceller.Process(local, silence, output));
        Assert.True(canceller.Process(following, silence, output));
        Assert.Equal(local, output);
        Assert.True(canceller.Finish(output));
        Assert.Equal(following, output);
    }

    [Fact]
    public void Delayed_echo_stays_suppressed_for_one_second_after_the_reference_stops()
    {
        using var canceller = new EchoCanceller(new DelayedSilenceBackend());
        const int frame = EchoCanceller.FrameSize;
        var active = new float[frame];
        active[^1] = 0.2f;
        var silent = new float[frame];
        var hop = new float[frame];
        var result = new List<float>();

        Assert.False(canceller.Process(new float[frame], active, hop));
        // 13 hops approximate a 200 ms acoustic tail; any raw mic sample here is the echo returning.
        for (var index = 0; index < 62; index++)
            if (canceller.Process(Enumerable.Repeat(index < 13 ? 0.8f : 0f, frame).ToArray(), silent, hop)) result.AddRange(hop);
        // The 63rd silent hop crosses exactly 16,000 samples since the last active sample.
        var quiet = Enumerable.Repeat(0.03f, frame).ToArray();
        if (canceller.Process(quiet, silent, hop)) result.AddRange(hop);
        if (canceller.Finish(hop)) result.AddRange(hop);

        Assert.Equal(64 * frame, result.Count);
        Assert.All(result.Skip(frame).Take(13 * frame), sample => Assert.Equal(0f, sample));
        var last = result.TakeLast(frame).ToArray();
        Assert.All(last.Take(128), sample => Assert.Equal(0f, sample));
        Assert.Equal(quiet.Skip(128), last.Skip(128));
    }

    [Fact]
    public void Output_is_aligned_and_as_long_as_the_source_across_windows_and_a_partial_final_hop()
    {
        // Two full windows, a partial third, and a final hop of 17 samples.
        var frames = 2 * OfflineEcho.WindowSamples + 3 * 256 + 17;
        var mic = Signal(1, frames);
        var reference = Signal(2, frames);
        var backend = new DelayedBackend(gain: 1f);
        var (cleaned, system) = Run(mic, reference, backend);

        Assert.Equal(frames, cleaned.Length);
        Assert.Equal(frames, system.Length);
        // A backend that returns its previous input unchanged must give back the mic exactly:
        // one hop of latency removed, nothing shifted, nothing lost at the end.
        Assert.Equal(mic, cleaned);
        Assert.Equal(reference, system);
        // One warm stream: every hop fed in order with zero padding only at the end, plus one zero flush hop.
        var fed = backend.Microphones.SelectMany(x => x).ToArray();
        Assert.Equal((frames + 255) / 256 + 1, backend.Microphones.Count);
        Assert.Equal(mic, fed.Take(frames));
        Assert.All(fed.Skip(frames), sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void A_silent_reference_skips_the_model_and_passes_the_mic_through_bit_exact()
    {
        var mic = Signal(7, OfflineEcho.WindowSamples + 1234);
        var reference = new float[mic.Length];
        Assert.False(OfflineEcho.HasReferenceActivity(new ArraySampleSource(reference)));
        var (cleaned, system) = Run(mic, reference, backend: null);
        Assert.Equal(mic, cleaned);
        Assert.Equal(reference, system);
        Assert.True(mic.AsSpan().SequenceEqual(cleaned));
    }

    [Fact]
    public void Leading_reference_silence_still_advances_the_model_from_the_start()
    {
        var frames = 4 * OfflineEcho.WindowSamples / 3;
        var mic = Signal(11, frames);
        var reference = new float[frames];
        var playback = OfflineEcho.WindowSamples + 1000;
        Signal(12, frames - playback).CopyTo(reference, playback);
        Assert.True(OfflineEcho.HasReferenceActivity(new ArraySampleSource(reference)));

        var backend = new DelayedBackend(gain: 0f);
        var (cleaned, _) = Run(mic, reference, backend);

        // The model saw the whole recording from t=0, not just from the first playback.
        Assert.Equal(mic, backend.Microphones.SelectMany(x => x).Take(frames));
        // Before any playback the mic is kept exactly; after it the model's (zero) output is used.
        Assert.Equal(mic.Take(playback), cleaned.Take(playback));
        Assert.All(cleaned.Skip(playback), sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void A_later_track_is_delayed_onto_the_shared_timeline()
    {
        var source = new DelayedSampleSource(new ArraySampleSource([1f, 2f, 3f]), 5);
        var buffer = new float[16];
        var read = source.Read(buffer);
        Assert.Equal(8, read);
        Assert.Equal(new[] { 0f, 0f, 0f, 0f, 0f, 1f, 2f, 3f }, buffer.Take(8));
        Assert.Equal(0, source.Read(buffer));
    }

    [Fact]
    public void Offline_echo_cancellation_is_on_by_default()
    {
        Assert.True(new AppSettings().OfflineEchoCancellation);
        Assert.True(JsonSerializer.Deserialize<AppSettings>("{}")!.OfflineEchoCancellation);
        Assert.False(JsonSerializer.Deserialize<AppSettings>("""{"offline_echo_cancellation": false}""")!.OfflineEchoCancellation);
    }

    [Fact]
    public void The_solver_with_its_suppressor_is_the_default_echo_remover_and_the_setting_picks_the_others()
    {
        Assert.Equal("solve-floor", new AppSettings().EchoModel);
        Assert.Same(LocalVqeAssets.SolveFloor, LocalVqeAssets.For(new AppSettings().EchoModel));
        Assert.Same(LocalVqeAssets.Sonora, LocalVqeAssets.For("sonora"));
        Assert.True(LocalVqeAssets.Sonora.IsSonora);
        Assert.Same(LocalVqeAssets.Fast, LocalVqeAssets.For("fast"));
        Assert.Same(LocalVqeAssets.Thorough, LocalVqeAssets.For(JsonSerializer.Deserialize<AppSettings>("""{"echo_model": "thorough"}""")!.EchoModel));
        Assert.Same(LocalVqeAssets.SolveFloor, LocalVqeAssets.For("nonsense"));
        Assert.Equal(AppSettings.EchoModels, LocalVqeAssets.Models.Select(model => model.Key));
    }

    [Fact]
    public void A_wrong_model_fails_its_checksum_before_any_native_call()
    {
        var model = Path.Combine(Path.GetTempPath(), $"amanu-echo-{Guid.NewGuid():N}.gguf");
        File.WriteAllBytes(model, [1, 2, 3]);
        try
        {
            var error = Assert.Throws<EchoCancellationException>(() => new LocalVqeBackend("does-not-exist.dll", model));
            Assert.Contains("SHA-256", error.Message);
        }
        finally { File.Delete(model); }
    }

    [Fact]
    public void Missing_assets_are_reported_not_thrown_as_io_errors()
    {
        var error = Assert.Throws<EchoCancellationException>(() => LocalVqeAssets.Resolve(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
        Assert.Contains("missing", error.Message);
    }

    /// <summary>Runs where the build copied LocalVQE into echo-runtime\; skipped elsewhere.</summary>
    public sealed class LocalVqeFactAttribute : FactAttribute
    {
        public LocalVqeFactAttribute()
        {
            try { LocalVqeAssets.Resolve(model: LocalVqeAssets.Fast); }
            catch (EchoCancellationException) { Skip = "LocalVQE is not in echo-runtime\\ beside the tests."; }
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) Skip = "LocalVQE is built for 64-bit Windows.";
        }
    }

    /// <summary>Like <see cref="LocalVqeFactAttribute"/>, for a test run once per model.</summary>
    public sealed class LocalVqeTheoryAttribute : TheoryAttribute
    {
        public LocalVqeTheoryAttribute() => Skip = new LocalVqeFactAttribute().Skip;
    }

    [LocalVqeTheory]
    [InlineData("fast")]
    [InlineData("thorough")]
    public void LocalVQE_removes_far_end_echo_and_keeps_near_end_speech(string key)
    {
        const int rate = 16_000;
        var frames = 14 * rate;
        var farEnd = Signal(99, frames);
        var reference = new float[frames];
        var mic = new float[frames];
        var farEnds = 8 * rate;
        // Far end plays for 8 s; the mic hears it 30 ms later at half level, as a room echo.
        Array.Copy(farEnd, reference, farEnds);
        const int delay = 30 * rate / 1000;
        for (var index = delay; index < farEnds + delay; index++) mic[index] += 0.5f * farEnd[index - delay];
        // Near-end only, after the far end stops: a voiced 200 Hz tone with harmonics.
        var nearFrom = 9 * rate + rate / 2;
        for (var index = nearFrom; index < frames; index++)
        {
            var t = (double)index / rate;
            mic[index] += (float)(0.08 * Math.Sin(2 * Math.PI * 200 * t) + 0.04 * Math.Sin(2 * Math.PI * 400 * t) + 0.02 * Math.Sin(2 * Math.PI * 600 * t));
        }

        var assets = LocalVqeAssets.Resolve(model: LocalVqeAssets.For(key));
        var timer = Stopwatch.StartNew();
        var (cleaned, system) = Run(mic, reference, new LocalVqeBackend(assets.Library, assets.Model), out var prime);
        timer.Stop();

        static double Db(float[] samples, int from, int to)
        {
            double sum = 0;
            for (var index = from; index < to; index++) sum += samples[index] * (double)samples[index];
            return 10 * Math.Log10(sum / (to - from) + 1e-20);
        }
        // Far-only region after the delay estimate has settled.
        var echoBefore = Db(mic, 3 * rate, farEnds);
        var echoAfter = Db(cleaned, 3 * rate, farEnds);
        var nearBefore = Db(mic, nearFrom + rate / 2, frames);
        var nearAfter = Db(cleaned, nearFrom + rate / 2, frames);
        output.WriteLine($"delay prime {prime?.Describe()}; echo {echoBefore:0.0} -> {echoAfter:0.0} dB; near end {nearBefore:0.0} -> {nearAfter:0.0} dB; " +
                         $"{frames / (double)rate / timer.Elapsed.TotalSeconds:0.0}x real time");

        Assert.Equal(frames, cleaned.Length);
        Assert.Equal(reference, system);
        Assert.True(echoBefore - echoAfter > 10, $"echo only dropped {echoBefore - echoAfter:0.0} dB");
        Assert.True(Math.Abs(nearBefore - nearAfter) < 1, $"near-end speech changed by {nearAfter - nearBefore:0.0} dB");
    }

    [Theory]
    [InlineData(new[] { 1152, -2, 1152, 1152, 1152 }, 1152)]      // a suite room: near-only windows are not confident
    [InlineData(new[] { 1152, 0, 1152, 1152, 1152 }, 1152)]       // four of five agree
    [InlineData(new[] { 1280, 1216, 1088, 1152 }, 1152)]          // within two blocks of the earlier median
    [InlineData(new[] { 0, 2816, 2816, 256, 128, 768, 512, 0 }, null)] // devices change, as in aec-real
    [InlineData(new[] { 1152, 0, 0, 1152 }, null)]
    [InlineData(new[] { -2, -2 }, null)]                          // no window was confident
    [InlineData(new int[0], null)]
    public void Sonora_takes_the_primed_delay_only_when_the_windows_agree(int[] windows, int? expected) =>
        Assert.Equal(expected, SonoraBackend.Agreed(windows));

    /// <summary>A delay prime that answers each window from a script and records how much it was fed.</summary>
    private sealed class ScriptedPrimer(Queue<int> answers, List<long> fed) : IEchoDelayPrimer, IDisposable
    {
        private long samples;
        public bool Disposed { get; private set; }
        public int BeginDelayPrime() { samples = 0; return 0; }
        public int FeedDelayPrime(ReadOnlySpan<float> microphone, ReadOnlySpan<float> reference)
        {
            Assert.Equal(0, microphone.Length % DelayPrime.Block);
            samples += microphone.Length;
            return 0;
        }
        public int EndDelayPrime() { fed.Add(samples); return answers.Dequeue(); }
        public void Dispose() => Disposed = true;
    }

    [Theory]
    [InlineData(new[] { 1152, 1152, 1152, 1152, 1152 }, 72)]   // all primed windows agree: AEC3 is told 72 ms
    [InlineData(new[] { 1152, 1152, 3000, 4000, 5000 }, null)] // most disagree: plain Sonora
    public void Sonora_primes_the_first_four_windows_then_every_fourth_and_falls_back_when_they_disagree(int[] answers, int? expectedMs)
    {
        var library = Path.Combine(AppContext.BaseDirectory, LocalVqeAssets.RuntimeFolder, SonoraBackend.LibraryName);
        if (!File.Exists(library)) return;
        // 230 s: seven windows of 30 s and one of 20 s. Windows 0 to 3 and 4 are primed, 5 and
        // 6 are not, and the last (7, not primed either) does not count.
        const int frames = 230 * 16_000;
        var fed = new List<long>();
        var primers = new List<ScriptedPrimer>();
        IEchoDelayPrimer Open()
        {
            var primer = new ScriptedPrimer(new Queue<int>(answers), fed);
            primers.Add(primer);
            return primer;
        }
        using var backend = new SonoraBackend(library, LocalVqeAssets.SonoraDelayLinear.SonoraTuning, new SonoraPrime(5), Open);
        using var canceller = new EchoCanceller(backend);
        var prime = canceller.PrimeDelay(new ArraySampleSource(new float[frames]), new ArraySampleSource(new float[frames]), frames);

        Assert.Equal(new long[] { 30 * 16_000, 30 * 16_000, 30 * 16_000, 30 * 16_000, 30 * 16_000 }, fed);
        var primer = Assert.Single(primers);
        Assert.True(primer.Disposed);
        Assert.Equal(expectedMs, backend.ExternalDelayMs);
        Assert.Equal(expectedMs is null ? "not_confident" : "primed", prime.Status);
        var hop = new float[256];
        canceller.Process(hop, hop, hop);
    }

    [Fact]
    public void The_warm_up_of_a_short_recording_is_all_of_it()
    {
        var window = new WarmupWindow(60 * 16_000);
        var mic = Enumerable.Range(0, 10 * 16_000).Select(index => (float)index).ToArray();
        window.Add(mic, new float[mic.Length]);
        Assert.Equal(mic, window.BestMicrophone.ToArray());
    }

    [Fact]
    public void The_warm_up_is_the_one_stretch_where_the_far_end_plays_most()
    {
        // 200 s; the far end plays only from 100 to 150 s. The mic counts samples, so the
        // stretch kept shows where it came from and that it is in one piece.
        const int rate = 16_000;
        var mic = Enumerable.Range(0, 200 * rate).Select(index => (float)index).ToArray();
        var far = new float[mic.Length];
        var noise = Signal(7, 50 * rate);
        Array.Copy(noise, 0, far, 100 * rate, noise.Length);
        var window = new WarmupWindow(60 * rate);
        for (var start = 0; start < mic.Length; start += OfflineEcho.WindowSamples)
        {
            var count = Math.Min(OfflineEcho.WindowSamples, mic.Length - start);
            window.Add(mic.AsSpan(start, count), far.AsSpan(start, count));
        }

        var kept = window.BestMicrophone.ToArray();
        Assert.Equal(60 * rate, kept.Length);
        var from = (int)kept[0];
        Assert.All(Enumerable.Range(0, kept.Length), index => Assert.Equal(from + index, (int)kept[index]));
        Assert.InRange(from, 40 * rate, 100 * rate);
        Assert.True(window.BestActiveFrames >= 4_900, $"only {window.BestActiveFrames} active frames");
        Assert.Equal(far.AsSpan(from, kept.Length).ToArray(), window.BestReference.ToArray());
    }

    [Fact]
    public void A_short_last_window_does_not_count_and_a_failed_prime_or_none_at_all_runs_plain_Sonora()
    {
        var library = Path.Combine(AppContext.BaseDirectory, LocalVqeAssets.RuntimeFolder, SonoraBackend.LibraryName);
        if (!File.Exists(library)) return;
        var tuning = LocalVqeAssets.SonoraDelayFloor.SonoraTuning;

        // 125 s: four whole windows, then 5 s of a fifth, which is too short to count.
        const int frames = 125 * 16_000;
        var fed = new List<long>();
        using (var backend = new SonoraBackend(library, tuning, new SonoraPrime(5), () => new ScriptedPrimer(new Queue<int>([1152, 1152, 1152, 1152, 9999]), fed)))
        using (var canceller = new EchoCanceller(backend))
        {
            var prime = canceller.PrimeDelay(new ArraySampleSource(new float[frames]), new ArraySampleSource(new float[frames]), frames);
            Assert.Equal(new long[] { 30 * 16_000, 30 * 16_000, 30 * 16_000, 30 * 16_000 }, fed);
            Assert.Equal(72, backend.ExternalDelayMs);
            Assert.Equal("primed", prime.Status);
        }

        // A LocalVQE without its front end answers -3 at the start: plain Sonora, and the record says why.
        using (var backend = new SonoraBackend(library, tuning, new SonoraPrime(5), () => new FailingPrimer()))
        using (var canceller = new EchoCanceller(backend))
        {
            var prime = canceller.PrimeDelay(new ArraySampleSource(new float[16_000]), new ArraySampleSource(new float[16_000]), 16_000);
            Assert.Equal("no_daf_frontend", prime.Status);
            Assert.Null(backend.ExternalDelayMs);
            var hop = new float[256];
            canceller.Process(hop, hop, hop);
        }

        // Never primed at all, as a caller that skips the prime would: plain Sonora, no error.
        using (var backend = new SonoraBackend(library, tuning, new SonoraPrime(5), () => throw new InvalidOperationException("must not be opened")))
        {
            var hop = new float[256];
            backend.Process(hop, hop, hop);
            Assert.Null(backend.ExternalDelayMs);
        }
    }

    private sealed class FailingPrimer : IEchoDelayPrimer, IDisposable
    {
        public int BeginDelayPrime() => DelayPrime.NoFrontEnd;
        public int FeedDelayPrime(ReadOnlySpan<float> microphone, ReadOnlySpan<float> reference) => DelayPrime.NoFrontEnd;
        public int EndDelayPrime() => DelayPrime.NoFrontEnd;
        public void Dispose() { }
    }

    [Fact]
    public void Sonora_refuses_a_tuning_it_does_not_understand()
    {
        var library = Path.Combine(AppContext.BaseDirectory, LocalVqeAssets.RuntimeFolder, SonoraBackend.LibraryName);
        if (!File.Exists(library)) return;
        var error = Assert.Throws<EchoCancellationException>(() =>
            new SonoraBackend(library, "suppressor.dominant_nearend_detection.enr_threshold=1;suppressor.no_such_knob=2"));
        Assert.Contains("entry 2", error.Message);
    }

    [Theory]
    [InlineData("sonora")]
    [InlineData("sonora-linear")]
    [InlineData("sonora-nearend")]
    [InlineData("sonora-gentle")]
    [InlineData("sonora-long")]
    [InlineData("sonora-long80")]
    [InlineData("sonora-long80-nearend")]
    [InlineData("sonora-delay-linear")]
    [InlineData("sonora-delay-nearend")]
    [InlineData("sonora-delay-floor")]
    [InlineData("sonora-delay-nearend-floor")]
    [InlineData("sonora-delay-floor70")]
    [InlineData("sonora-delay-nearend-nf70")]
    [InlineData("sonora-delay-nearend-floor70")]
    public void Sonora_removes_far_end_echo_and_keeps_near_end_speech(string key)
    {
        var model = LocalVqeAssets.For(key);
        Assert.Equal(key, model.Key);
        var library = Path.Combine(AppContext.BaseDirectory, LocalVqeAssets.RuntimeFolder, SonoraBackend.LibraryName);
        if (!File.Exists(library)) return;
        // A primed variant needs LocalVQE for its delay prime; without it there is nothing to test.
        LocalVqeAssets.Paths? primer = null;
        if (model.SonoraPrime is not null)
        {
            if (new LocalVqeFactAttribute().Skip is not null) return;
            primer = LocalVqeAssets.Resolve(model: LocalVqeAssets.Fast);
        }
        const int rate = 16_000;
        var frames = 14 * rate;
        var farEnd = Signal(99, frames);
        var reference = new float[frames];
        var mic = new float[frames];
        var farEnds = 8 * rate;
        Array.Copy(farEnd, reference, farEnds);
        const int delay = 30 * rate / 1000;
        for (var index = delay; index < farEnds + delay; index++) mic[index] += 0.5f * farEnd[index - delay];
        var nearFrom = 9 * rate + rate / 2;
        for (var index = nearFrom; index < frames; index++)
        {
            var t = (double)index / rate;
            mic[index] += (float)(0.08 * Math.Sin(2 * Math.PI * 200 * t) + 0.04 * Math.Sin(2 * Math.PI * 400 * t) + 0.02 * Math.Sin(2 * Math.PI * 600 * t));
        }

        var timer = Stopwatch.StartNew();
        var backend = primer is null
            ? new SonoraBackend(library, model.SonoraTuning)
            : new SonoraBackend(library, model.SonoraTuning, model.SonoraPrime, () => new LocalVqeBackend(primer.Library, primer.Model, 1));
        var (cleaned, system) = Run(mic, reference, backend, out var prime);
        timer.Stop();
        if (primer is not null)
        {
            // The echo is 30 ms late; the prime lands up to a few blocks early, never late.
            Assert.NotNull(backend.ExternalDelayMs);
            Assert.InRange(backend.ExternalDelayMs!.Value, 8, 30);
            output.WriteLine($"{key}: delay prime {prime?.Describe()}, AEC3 told {backend.ExternalDelayMs} ms");
        }

        static double Db(float[] samples, int from, int to)
        {
            double sum = 0;
            for (var index = from; index < to; index++) sum += samples[index] * (double)samples[index];
            return 10 * Math.Log10(sum / (to - from) + 1e-20);
        }
        var echoBefore = Db(mic, 3 * rate, farEnds);
        var echoAfter = Db(cleaned, 3 * rate, farEnds);
        var nearBefore = Db(mic, nearFrom + rate / 2, frames);
        var nearAfter = Db(cleaned, nearFrom + rate / 2, frames);
        output.WriteLine($"{key}: echo {echoBefore:0.0} -> {echoAfter:0.0} dB; near end {nearBefore:0.0} -> {nearAfter:0.0} dB; " +
                         $"{frames / (double)rate / timer.Elapsed.TotalSeconds:0.0}x real time");

        Assert.Equal(frames, cleaned.Length);
        Assert.Equal(reference, system);
        Assert.True(echoBefore - echoAfter > 10, $"echo only dropped {echoBefore - echoAfter:0.0} dB");
        Assert.True(Math.Abs(nearBefore - nearAfter) < 1, $"near-end speech changed by {nearAfter - nearBefore:0.0} dB");
    }

    [LocalVqeFact]
    public void LocalVQE_loads_its_model_from_a_non_ascii_folder()
    {
        var assets = LocalVqeAssets.Resolve(model: LocalVqeAssets.Fast);
        var folder = Path.Combine(Path.GetTempPath(), $"amanu-эхо-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var model = Path.Combine(folder, LocalVqeAssets.Fast.FileName);
            File.Copy(assets.Model, model);
            using var backend = new LocalVqeBackend(assets.Library, model);
            Assert.Equal(256, backend.HopSize);
            var hop = new float[256];
            backend.Process(hop, hop, hop);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
