using System.Runtime.InteropServices;

namespace Amanu.Core.Processing;

/// <summary>
/// WebRTC AEC3 through Sonora's C API (sonora_ffi.dll, a Rust port of the
/// WebRTC audio processing module). Only the echo canceller runs: noise
/// suppression, gain control and the high-pass filter stay off, so the mic is
/// changed only where the far end echoes in it. Without a <see cref="SonoraPrime"/>
/// AEC3 estimates the echo delay itself and this backend does not take part in
/// the whole-signal delay prime.
/// </summary>
public sealed unsafe class SonoraBackend : IEchoCancellationBackend, IEchoDelayPrimer
{
    public const string LibraryName = "sonora_ffi.dll";

    [StructLayout(LayoutKind.Sequential)]
    private struct WapConfig
    {
        public int PipelineMaximumInternalProcessingRate;
        public byte PipelineMultiChannelRender;
        public byte PipelineMultiChannelCapture;
        public int PipelineCaptureDownmixMethod;
        public byte PreAmplifierEnabled;
        public float PreAmplifierFixedGainFactor;
        public byte CaptureLevelAdjustmentEnabled;
        public float CaptureLevelAdjustmentPreGainFactor;
        public float CaptureLevelAdjustmentPostGainFactor;
        public byte AnalogMicGainEmulationEnabled;
        public int AnalogMicGainEmulationInitialLevel;
        public byte HighPassFilterEnabled;
        public byte HighPassFilterApplyInFullBand;
        public byte EchoCancellerEnabled;
        public byte EchoCancellerEnforceHighPassFiltering;
        public byte NoiseSuppressionEnabled;
        public int NoiseSuppressionLevel;
        public byte NoiseSuppressionAnalyzeLinearAecOutputWhenAvailable;
        public byte GainController2Enabled;
        public float GainController2FixedDigitalGainDb;
        public byte GainController2AdaptiveDigitalEnabled;
        public float GainController2AdaptiveDigitalHeadroomDb;
        public float GainController2AdaptiveDigitalMaxGainDb;
        public float GainController2AdaptiveDigitalInitialGainDb;
        public float GainController2AdaptiveDigitalMaxGainChangeDbPerSecond;
        public float GainController2AdaptiveDigitalMaxOutputNoiseLevelDbfs;
        public byte GainController2InputVolumeControllerEnabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WapStreamConfig(int sampleRate, int channels)
    {
        public int SampleRateHz = sampleRate;
        public int NumChannels = channels;
    }

    private static readonly Dictionary<string, nint> Loaded = new(StringComparer.OrdinalIgnoreCase);

    private readonly nint library;
    private nint apm;
    private readonly WapConfig config;
    private readonly string? tuning;
    private readonly SonoraPrime? prime;
    private readonly Func<IEchoDelayPrimer>? openPrimer;
    private readonly WapStreamConfig stream = new(16_000, 1);
    private readonly delegate* unmanaged[Cdecl]<nint, WapStreamConfig, WapStreamConfig, WapStreamConfig, WapStreamConfig, int> initialize;
    private readonly delegate* unmanaged[Cdecl]<nint, void> destroy;
    private readonly delegate* unmanaged[Cdecl]<nint, float**, WapStreamConfig, WapStreamConfig, float**, int> processStream;
    private readonly delegate* unmanaged[Cdecl]<nint, float**, WapStreamConfig, WapStreamConfig, float**, int> processReverse;
    private readonly delegate* unmanaged[Cdecl]<nint, int, int> setStreamDelay;
    private const int Frame = 160;
    private readonly float[] referenceOut = new float[Frame];
    // AEC3 works on 10 ms frames, the canceller hands over 16 ms hops: whole
    // frames run as soon as they are complete, and the rest waits for the next hop.
    private readonly float[] waitingMicrophone = new float[EchoCanceller.FrameSize + Frame];
    private readonly float[] waitingReference = new float[EchoCanceller.FrameSize + Frame];
    private int waiting;
    private readonly float[] cleanedQueue = new float[2 * EchoCanceller.FrameSize + Frame];
    private int cleanedCount;
    private bool first = true;

    // The delay prime, one window at a time, and the stretch of the recording kept for the warm-up.
    private IEchoDelayPrimer? windowPrimer;
    private int windowFed;
    private int windowIndex;
    private readonly List<int> windowShifts = [];
    private WarmupWindow? warmup;
    private int? externalDelayMs;

    public int SampleRate => 16_000;
    /// <summary>
    /// The canceller's hop. Like LocalVQE, each call returns the previous hop's
    /// output (the first returns silence), which <see cref="EchoCanceller"/> expects.
    /// </summary>
    public int HopSize => EchoCanceller.FrameSize;

    /// <summary>The echo delay AEC3 was given, in ms, or null when it estimates the delay itself.</summary>
    public int? ExternalDelayMs => externalDelayMs;

    /// <param name="tuning">
    /// AEC3 settings as <c>path=value</c> entries separated by <c>;</c> (see
    /// <see cref="LocalVqeModel.SonoraTuning"/>); null keeps WebRTC's defaults.
    /// </param>
    /// <param name="prime">
    /// Fix AEC3's delay from the whole signal before the stream (see <see cref="SonoraPrime"/>);
    /// <paramref name="openPrimer"/> then opens the LocalVQE contexts that measure it.
    /// </param>
    /// <param name="openPrimer">Opens one delay-prime context; the backend disposes what it opens.</param>
    public SonoraBackend(string libraryPath, string? tuning = null, SonoraPrime? prime = null, Func<IEchoDelayPrimer>? openPrimer = null)
    {
        if (prime is not null && openPrimer is null)
            throw new ArgumentNullException(nameof(openPrimer), "a Sonora delay prime needs a LocalVQE primer");
        lock (Loaded)
        {
            if (!Loaded.TryGetValue(libraryPath, out library))
            {
                try { library = NativeLibrary.Load(libraryPath); }
                catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
                {
                    throw new EchoCancellationException($"could not load Sonora at {libraryPath}: {exception.Message}", exception);
                }
                Loaded[libraryPath] = library;
            }
        }
        var configDefault = (delegate* unmanaged[Cdecl]<WapConfig>)Symbol("wap_config_default");
        initialize = (delegate* unmanaged[Cdecl]<nint, WapStreamConfig, WapStreamConfig, WapStreamConfig, WapStreamConfig, int>)Symbol("wap_initialize");
        destroy = (delegate* unmanaged[Cdecl]<nint, void>)Symbol("wap_destroy");
        processStream = (delegate* unmanaged[Cdecl]<nint, float**, WapStreamConfig, WapStreamConfig, float**, int>)Symbol("wap_process_stream_f32");
        processReverse = (delegate* unmanaged[Cdecl]<nint, float**, WapStreamConfig, WapStreamConfig, float**, int>)Symbol("wap_process_reverse_stream_f32");
        setStreamDelay = (delegate* unmanaged[Cdecl]<nint, int, int>)Symbol("wap_set_stream_delay_ms");

        var settings = configDefault();
        settings.EchoCancellerEnabled = 1;
        settings.EchoCancellerEnforceHighPassFiltering = 0;
        settings.HighPassFilterEnabled = 0;
        settings.NoiseSuppressionEnabled = 0;
        settings.GainController2Enabled = 0;
        settings.CaptureLevelAdjustmentEnabled = 0;
        settings.PreAmplifierEnabled = 0;
        settings.AnalogMicGainEmulationEnabled = 0;
        config = settings;
        this.tuning = tuning;
        this.prime = prime;
        this.openPrimer = openPrimer;
        // A tuning the DLL does not know fails here, not after a whole prime.
        if (tuning is not null) Check(tuning);
        // With a prime, which settings run is known only once the delay is: they are created then.
        if (prime is null) Create(tuning);
    }

    private nint Symbol(string name) => NativeLibrary.TryGetExport(library, name, out var address)
        ? address
        : throw new EchoCancellationException($"Sonora is missing required symbol {name}");

    /// <summary>Only the patched DLL has the tuning calls: a tuning must never fall back to the defaults unnoticed.</summary>
    private void Check(string text)
    {
        var check = (delegate* unmanaged[Cdecl]<byte*, int>)Symbol("wap_check_aec3_tuning");
        var bytes = System.Text.Encoding.ASCII.GetBytes(text + "\0");
        fixed (byte* pointer = bytes)
        {
            var bad = check(pointer);
            if (bad != 0)
                throw new EchoCancellationException($"Sonora does not understand entry {bad} of the tuning \"{text}\"");
        }
    }

    private void Create(string? text)
    {
        if (text is null)
        {
            var create = (delegate* unmanaged[Cdecl]<WapConfig, nint>)Symbol("wap_create_with_config");
            apm = create(config);
        }
        else
        {
            Check(text);
            var createTuned = (delegate* unmanaged[Cdecl]<WapConfig, byte*, nint>)Symbol("wap_create_with_aec3_tuning");
            var bytes = System.Text.Encoding.ASCII.GetBytes(text + "\0");
            fixed (byte* pointer = bytes) apm = createTuned(config, pointer);
        }
        if (apm == 0) throw new EchoCancellationException("Sonora could not create an audio processor");
        var result = initialize(apm, stream, stream, stream, stream);
        if (result != 0)
        {
            destroy(apm);
            apm = 0;
            throw new EchoCancellationException($"Sonora could not initialize ({result})");
        }
    }

    /// <summary>
    /// The prime runs on windows this long and the delay is theirs only when they agree. A
    /// call on one device keeps one delay; a recording that changes device (as the aec-real
    /// set does, forty devices one after another) does not, and one fixed delay would be
    /// wrong for most of it. Windows instead of one whole-signal prime besides: a second
    /// pass over the recording cost as much again as Sonora itself.
    /// </summary>
    public const int PrimeWindowSamples = 30 * EchoCanceller.SampleRate;

    /// <summary>
    /// After the first <see cref="PrimeFirstWindows"/>, only every fourth window is primed.
    /// One device keeps one delay, so a sample of the windows finds it as well as all of
    /// them, and the prime then costs about a quarter: with every window primed it took
    /// about as long as Sonora itself on a long call.
    /// </summary>
    public const int PrimeWindowStride = 4;

    /// <summary>
    /// The first two minutes are primed window by window, so a short call is not decided by
    /// its first 30 s alone, which often hold a join tone or a silent far end.
    /// </summary>
    public const int PrimeFirstWindows = 4;

    /// <summary>The share of confident windows that must agree with their median.</summary>
    public const double PrimeAgreement = 0.75;

    /// <summary>A window agrees when it is within two AEC3 blocks of the median.</summary>
    public const int PrimeToleranceSamples = 128;

    public int BeginDelayPrime()
    {
        if (prime is null) return DelayPrime.Unsupported;
        if (apm != 0 || windowPrimer is not null) throw new EchoCancellationException("the Sonora delay prime must come once, before the stream");
        windowPrimer = openPrimer!();
        var code = windowPrimer.BeginDelayPrime();
        if (code < 0) return GiveUp(code);
        windowFed = 0;
        windowIndex = 0;
        windowShifts.Clear();
        warmup = new WarmupWindow(prime.WarmupSeconds * EchoCanceller.SampleRate);
        return 0;
    }

    public int FeedDelayPrime(ReadOnlySpan<float> microphone, ReadOnlySpan<float> reference)
    {
        if (windowPrimer is null) return DelayPrime.Unsupported;
        warmup!.Add(microphone, reference);
        // Window edges are multiples of 128 samples, as is every chunk the canceller feeds.
        for (var offset = 0; offset < microphone.Length;)
        {
            var take = Math.Min(microphone.Length - offset, PrimeWindowSamples - windowFed);
            if (Primed(windowIndex))
            {
                var code = windowPrimer.FeedDelayPrime(microphone.Slice(offset, take), reference.Slice(offset, take));
                if (code < 0) return GiveUp(code);
            }
            offset += take;
            windowFed += take;
            if (windowFed == PrimeWindowSamples)
            {
                if (Primed(windowIndex))
                {
                    windowShifts.Add(windowPrimer.EndDelayPrime());
                    var code = windowPrimer.BeginDelayPrime();
                    if (code < 0) return GiveUp(code);
                }
                windowFed = 0;
                windowIndex++;
            }
        }
        return 0;
    }

    public int EndDelayPrime()
    {
        if (windowPrimer is null) return DelayPrime.Unsupported;
        // A short last window says little; one longer than half a window counts, and so
        // does the only one of a recording shorter than a window, which is the whole signal.
        if (Primed(windowIndex) && (windowFed * 2 > PrimeWindowSamples || (windowShifts.Count == 0 && windowFed > 0)))
            windowShifts.Add(windowPrimer.EndDelayPrime());
        if (Agreed(windowShifts) is not { } shift) return GiveUp(DelayPrime.NotConfident);
        ClosePrimers();
        externalDelayMs = shift * 1000 / EchoCanceller.SampleRate;
        Create((tuning is null ? "" : tuning + ";") + "delay.use_external_delay_estimator=true");
        // The warm-up: the stretch where the far end plays most, once, output dropped, so the
        // filter has converged when the stream begins.
        var scratch = new float[Frame];
        var warmMicrophone = warmup!.BestMicrophone;
        var warmReference = warmup.BestReference;
        for (var start = 0; start + Frame <= warmMicrophone.Length; start += Frame)
            RunFrame(warmMicrophone.Slice(start, Frame), warmReference.Slice(start, Frame), scratch);
        warmup = null;
        return shift;
    }

    private static bool Primed(int window) => window < PrimeFirstWindows || window % PrimeWindowStride == 0;

    /// <summary>
    /// The one delay that fits the whole recording, or null: the median of the windows that
    /// found a delay (the earlier of two middle ones, as an early delay only costs filter
    /// length), when most of them found the same one.
    /// </summary>
    public static int? Agreed(IReadOnlyCollection<int> windowShifts)
    {
        var confident = windowShifts.Where(window => window >= 0).Order().ToList();
        if (confident.Count == 0) return null;
        var median = confident[(confident.Count - 1) / 2];
        var agreeing = confident.Count(window => Math.Abs(window - median) <= PrimeToleranceSamples);
        return agreeing >= PrimeAgreement * confident.Count ? median : null;
    }

    /// <summary>No usable delay: plain Sonora, with AEC3's own delay estimate and WebRTC's defaults.</summary>
    private int GiveUp(int code)
    {
        ClosePrimers();
        warmup = null;
        if (apm == 0) Create(null);
        return code;
    }

    private void ClosePrimers()
    {
        (windowPrimer as IDisposable)?.Dispose();
        windowPrimer = null;
    }

    public void Process(ReadOnlySpan<float> microphone, ReadOnlySpan<float> reference, Span<float> output)
    {
        if (microphone.Length != HopSize || reference.Length != HopSize || output.Length != HopSize)
            throw new EchoCancellationException($"Sonora needs {HopSize} samples per hop; got mic {microphone.Length}, reference {reference.Length}");
        // A primed variant whose prime never ran is plain Sonora.
        if (apm == 0) GiveUp(DelayPrime.NotConfident);
        microphone.CopyTo(waitingMicrophone.AsSpan(waiting));
        reference.CopyTo(waitingReference.AsSpan(waiting));
        waiting += HopSize;
        var start = 0;
        for (; start + Frame <= waiting; start += Frame)
        {
            RunFrame(waitingMicrophone.AsSpan(start, Frame), waitingReference.AsSpan(start, Frame), cleanedQueue.AsSpan(cleanedCount, Frame));
            cleanedCount += Frame;
        }
        waitingMicrophone.AsSpan(start, waiting - start).CopyTo(waitingMicrophone);
        waitingReference.AsSpan(start, waiting - start).CopyTo(waitingReference);
        waiting -= start;
        if (first)
        {
            first = false;
            output.Clear();
            return;
        }
        // Hop k returns samples [256(k-1), 256k): at least 256k have been processed by now.
        cleanedQueue.AsSpan(0, HopSize).CopyTo(output);
        cleanedQueue.AsSpan(HopSize, cleanedCount - HopSize).CopyTo(cleanedQueue);
        cleanedCount -= HopSize;
    }

    private void RunFrame(ReadOnlySpan<float> microphone, ReadOnlySpan<float> reference, Span<float> output)
    {
        fixed (float* far = reference)
        fixed (float* farOut = referenceOut)
        fixed (float* mic = microphone)
        fixed (float* cleaned = output)
        {
            var farIn = far;
            var farDest = farOut;
            var result = processReverse(apm, &farIn, stream, stream, &farDest);
            if (result != 0) throw new EchoCancellationException($"Sonora reverse stream failed ({result})");
            // AEC3 reads the external delay with each capture frame.
            if (externalDelayMs is { } delay) setStreamDelay(apm, delay);
            var micIn = mic;
            var micDest = cleaned;
            result = processStream(apm, &micIn, stream, stream, &micDest);
            if (result != 0) throw new EchoCancellationException($"Sonora capture stream failed ({result})");
        }
    }

    public void Dispose()
    {
        ClosePrimers();
        if (apm != 0) destroy(apm);
        apm = 0;
    }
}

/// <summary>
/// How a Sonora variant fixes AEC3's echo delay before the stream. LocalVQE's delay
/// prime, which the LocalVQE models run on the whole signal, measures it on 30 s
/// windows, and AEC3 takes it as an external estimate instead of estimating it itself: in the
/// quality suite's reverberant rooms its own estimate jumped about 90 times in
/// 150 s, and each jump reset the linear filter, which then never converged.
/// When the delay is not the same over the whole recording, plain Sonora runs instead.
/// </summary>
/// <param name="WarmupSeconds">
/// The length of the stretch of the recording in which the far end plays most; it runs
/// once before the stream, output dropped, so a long filter has converged by the time
/// the stream begins. The first seconds of a call were a poor choice: in the suite they
/// held 20 s of far-end speech in 60 s, and the filter was 10 dB short when the stream began.
/// </param>
public sealed record SonoraPrime(int WarmupSeconds);

/// <summary>
/// Finds, in one pass over the recording, the stretch of <c>length</c> samples in which the
/// far end plays most and keeps its audio: a ring of the last <c>length</c> samples, the
/// activity of its 10 ms frames, and a copy taken at the end of a chunk (about 15 s in
/// Amanu) when the ring holds more far-end speech than any stretch before it. Stretches
/// that end inside a chunk are not considered, which costs at most a chunk's worth of
/// the best stretch and saves a copy per frame. One stretch, not the active frames from all over the
/// recording: AEC3 adapts wrongly at every join where its render history does not match
/// the mic, and joining many short pieces left the filter worse than the first seconds did.
/// </summary>
public sealed class WarmupWindow
{
    public const int FrameSamples = 160;

    /// <summary>A 10 ms frame of the far end is active above this mean power, -50 dBFS.</summary>
    public const double ActivePower = 1e-5;

    private readonly int length;
    private readonly float[] ringMicrophone;
    private readonly float[] ringReference;
    private readonly bool[] ringActive;
    private readonly float[] bestMicrophone;
    private readonly float[] bestReference;
    private long seen;
    private int active;
    private int bestActive = -1;
    private int bestLength;
    private double framePower;

    public WarmupWindow(int length)
    {
        if (length <= 0 || length % FrameSamples != 0)
            throw new ArgumentException($"the warm-up must be whole {FrameSamples}-sample frames", nameof(length));
        this.length = length;
        ringMicrophone = new float[length];
        ringReference = new float[length];
        ringActive = new bool[length / FrameSamples];
        bestMicrophone = new float[length];
        bestReference = new float[length];
    }

    /// <summary>The far end's active frames in the best stretch so far.</summary>
    public int BestActiveFrames => Math.Max(bestActive, 0);

    /// <summary>The mic of the best stretch so far, in order; shorter than the window only for a shorter recording.</summary>
    public ReadOnlySpan<float> BestMicrophone => bestMicrophone.AsSpan(0, bestLength);

    /// <summary>The far end of the best stretch so far.</summary>
    public ReadOnlySpan<float> BestReference => bestReference.AsSpan(0, bestLength);

    /// <summary>The next samples of the recording, in order; chunks may split frames anywhere.</summary>
    public void Add(ReadOnlySpan<float> microphone, ReadOnlySpan<float> reference)
    {
        for (var index = 0; index < microphone.Length; index++)
        {
            var at = (int)(seen % length);
            ringMicrophone[at] = microphone[index];
            ringReference[at] = reference[index];
            framePower += reference[index] * (double)reference[index];
            seen++;
            if (seen % FrameSamples != 0) continue;
            var frame = (int)((seen / FrameSamples - 1) % ringActive.Length);
            if (ringActive[frame]) active--;
            ringActive[frame] = framePower / FrameSamples > ActivePower;
            if (ringActive[frame]) active++;
            framePower = 0;
        }
        Consider();
    }

    /// <summary>Copies the ring out when it holds more far-end speech than the best stretch so far.</summary>
    private void Consider()
    {
        if (seen <= length)
        {
            // Not a full window yet: the recording so far is the stretch.
            bestLength = (int)seen;
            ringMicrophone.AsSpan(0, bestLength).CopyTo(bestMicrophone);
            ringReference.AsSpan(0, bestLength).CopyTo(bestReference);
            bestActive = active;
            return;
        }
        if (active <= bestActive) return;
        var start = (int)(seen % length);
        ringMicrophone.AsSpan(start).CopyTo(bestMicrophone);
        ringMicrophone.AsSpan(0, start).CopyTo(bestMicrophone.AsSpan(length - start));
        ringReference.AsSpan(start).CopyTo(bestReference);
        ringReference.AsSpan(0, start).CopyTo(bestReference.AsSpan(length - start));
        bestLength = length;
        bestActive = active;
    }
}
