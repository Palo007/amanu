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

    // The delay prime, one window at a time, and the start of the recording kept for the warm-up.
    private IEchoDelayPrimer? windowPrimer;
    private int windowFed;
    private readonly List<int> windowShifts = [];
    private float[]? warmMicrophone;
    private float[]? warmReference;
    private int warmFilled;
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
        windowShifts.Clear();
        warmMicrophone = new float[prime.WarmupSeconds * EchoCanceller.SampleRate];
        warmReference = new float[warmMicrophone.Length];
        warmFilled = 0;
        return 0;
    }

    public int FeedDelayPrime(ReadOnlySpan<float> microphone, ReadOnlySpan<float> reference)
    {
        if (windowPrimer is null) return DelayPrime.Unsupported;
        var keep = Math.Min(microphone.Length, warmMicrophone!.Length - warmFilled);
        microphone[..keep].CopyTo(warmMicrophone.AsSpan(warmFilled));
        reference[..keep].CopyTo(warmReference.AsSpan(warmFilled));
        warmFilled += keep;
        // Window edges are multiples of 128 samples, as is every chunk the canceller feeds.
        for (var offset = 0; offset < microphone.Length;)
        {
            var take = Math.Min(microphone.Length - offset, PrimeWindowSamples - windowFed);
            var code = windowPrimer.FeedDelayPrime(microphone.Slice(offset, take), reference.Slice(offset, take));
            if (code < 0) return GiveUp(code);
            offset += take;
            windowFed += take;
            if (windowFed == PrimeWindowSamples)
            {
                windowShifts.Add(windowPrimer.EndDelayPrime());
                code = windowPrimer.BeginDelayPrime();
                if (code < 0) return GiveUp(code);
                windowFed = 0;
            }
        }
        return 0;
    }

    public int EndDelayPrime()
    {
        if (windowPrimer is null) return DelayPrime.Unsupported;
        // A short last window says little; one longer than half a window counts, and so
        // does the only one of a recording shorter than a window, which is the whole signal.
        if (windowFed * 2 > PrimeWindowSamples || (windowShifts.Count == 0 && windowFed > 0))
            windowShifts.Add(windowPrimer.EndDelayPrime());
        if (Agreed(windowShifts) is not { } shift) return GiveUp(DelayPrime.NotConfident);
        ClosePrimers();
        externalDelayMs = shift * 1000 / EchoCanceller.SampleRate;
        Create((tuning is null ? "" : tuning + ";") + "delay.use_external_delay_estimator=true");
        // The warm-up: the start of the recording once, output dropped, so the filter has converged when the stream begins.
        var scratch = new float[Frame];
        for (var start = 0; start + Frame <= warmFilled; start += Frame)
            RunFrame(warmMicrophone.AsSpan(start, Frame), warmReference.AsSpan(start, Frame), scratch);
        warmMicrophone = warmReference = null;
        return shift;
    }

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
        warmMicrophone = warmReference = null;
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

    private void RunFrame(Span<float> microphone, Span<float> reference, Span<float> output)
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
/// The first seconds of the recording run once before the stream, output dropped,
/// so a long filter has converged by the time the stream begins.
/// </param>
public sealed record SonoraPrime(int WarmupSeconds);
