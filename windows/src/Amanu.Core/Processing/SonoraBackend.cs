using System.Runtime.InteropServices;

namespace Amanu.Core.Processing;

/// <summary>
/// WebRTC AEC3 through Sonora's C API (sonora_ffi.dll, a Rust port of the
/// WebRTC audio processing module). Only the echo canceller runs: noise
/// suppression, gain control and the high-pass filter stay off, so the mic is
/// changed only where the far end echoes in it. AEC3 estimates the echo delay
/// itself, so this backend does not take part in the whole-signal delay prime.
/// </summary>
public sealed unsafe class SonoraBackend : IEchoCancellationBackend
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

    private readonly nint apm;
    private readonly WapStreamConfig stream = new(16_000, 1);
    private readonly delegate* unmanaged[Cdecl]<nint, void> destroy;
    private readonly delegate* unmanaged[Cdecl]<nint, float**, WapStreamConfig, WapStreamConfig, float**, int> processStream;
    private readonly delegate* unmanaged[Cdecl]<nint, float**, WapStreamConfig, WapStreamConfig, float**, int> processReverse;
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

    public int SampleRate => 16_000;
    /// <summary>
    /// The canceller's hop. Like LocalVQE, each call returns the previous hop's
    /// output (the first returns silence), which <see cref="EchoCanceller"/> expects.
    /// </summary>
    public int HopSize => EchoCanceller.FrameSize;

    public SonoraBackend(string libraryPath)
    {
        nint library;
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
        nint Symbol(string name) => NativeLibrary.TryGetExport(library, name, out var address)
            ? address
            : throw new EchoCancellationException($"Sonora is missing required symbol {name}");
        var configDefault = (delegate* unmanaged[Cdecl]<WapConfig>)Symbol("wap_config_default");
        var create = (delegate* unmanaged[Cdecl]<WapConfig, nint>)Symbol("wap_create_with_config");
        var initialize = (delegate* unmanaged[Cdecl]<nint, WapStreamConfig, WapStreamConfig, WapStreamConfig, WapStreamConfig, int>)Symbol("wap_initialize");
        destroy = (delegate* unmanaged[Cdecl]<nint, void>)Symbol("wap_destroy");
        processStream = (delegate* unmanaged[Cdecl]<nint, float**, WapStreamConfig, WapStreamConfig, float**, int>)Symbol("wap_process_stream_f32");
        processReverse = (delegate* unmanaged[Cdecl]<nint, float**, WapStreamConfig, WapStreamConfig, float**, int>)Symbol("wap_process_reverse_stream_f32");

        var config = configDefault();
        config.EchoCancellerEnabled = 1;
        config.EchoCancellerEnforceHighPassFiltering = 0;
        config.HighPassFilterEnabled = 0;
        config.NoiseSuppressionEnabled = 0;
        config.GainController2Enabled = 0;
        config.CaptureLevelAdjustmentEnabled = 0;
        config.PreAmplifierEnabled = 0;
        config.AnalogMicGainEmulationEnabled = 0;
        apm = create(config);
        if (apm == 0) throw new EchoCancellationException("Sonora could not create an audio processor");
        var result = initialize(apm, stream, stream, stream, stream);
        if (result != 0)
        {
            destroy(apm);
            throw new EchoCancellationException($"Sonora could not initialize ({result})");
        }
    }

    public void Process(ReadOnlySpan<float> microphone, ReadOnlySpan<float> reference, Span<float> output)
    {
        if (microphone.Length != HopSize || reference.Length != HopSize || output.Length != HopSize)
            throw new EchoCancellationException($"Sonora needs {HopSize} samples per hop; got mic {microphone.Length}, reference {reference.Length}");
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
            var micIn = mic;
            var micDest = cleaned;
            result = processStream(apm, &micIn, stream, stream, &micDest);
            if (result != 0) throw new EchoCancellationException($"Sonora capture stream failed ({result})");
        }
    }

    public void Dispose() => destroy(apm);
}
