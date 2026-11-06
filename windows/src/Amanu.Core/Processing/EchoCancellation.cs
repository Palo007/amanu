using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Amanu.Core.Processing;

/// <summary>
/// The LocalVQE build Amanu ships: a CPU-only DLL and its echo model under
/// <c>echo-runtime\</c> beside the app, never downloaded at run time. Kept
/// apart from <c>local-runtime\</c>, whose ggml is a different build.
/// </summary>
public static class LocalVqeAssets
{
    public const string RuntimeFolder = "echo-runtime";
    public const string LibraryName = "liblocalvqe.dll";
    public const string ModelName = "localvqe-v1.4-aec-200K-f32.gguf";
    public const string ModelSha256 = "b6e43138588a83bfe903ab5e143b4020b91c1e1629f5a575ac5855ff0003c731";
    /// <summary>
    /// The audio path's revision: changing it changes this name, and with it the
    /// .echo-* cache folder. v3 is macOS's; v4 adds the whole-signal delay prime.
    /// </summary>
    public const string ProcessorVersion = "localvqe-v1.4-aec-200k-f32-amanu-v4-delay-prime";

    public sealed record Paths(string Library, string Model);

    public static Paths Resolve(string? applicationDirectory = null)
    {
        var root = Path.Combine(applicationDirectory ?? AppContext.BaseDirectory, RuntimeFolder);
        var paths = new Paths(Path.Combine(root, LibraryName), Path.Combine(root, ModelName));
        foreach (var path in new[] { paths.Library, paths.Model })
            if (!File.Exists(path)) throw new EchoCancellationException($"LocalVQE asset is missing: {path}");
        return paths;
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

public sealed class EchoCancellationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One 256-sample hop in, the model's output for it out.</summary>
public interface IEchoCancellationBackend : IDisposable
{
    int SampleRate { get; }
    int HopSize { get; }
    void Process(ReadOnlySpan<float> microphone, ReadOnlySpan<float> reference, Span<float> output);
}

/// <summary>
/// A backend that can fix the echo delay from the whole signal before streaming.
/// Begin/Feed return 0 or a negative code; End returns the shift in samples
/// (&gt;= 0) or a negative code. Codes follow <see cref="DelayPrime"/>.
/// </summary>
public interface IEchoDelayPrimer
{
    int BeginDelayPrime();
    int FeedDelayPrime(ReadOnlySpan<float> microphone, ReadOnlySpan<float> reference);
    int EndDelayPrime();
}

/// <summary>What the whole-signal delay prime decided: a frozen shift, or why online acquisition was kept.</summary>
public sealed record DelayPrime(int? ShiftSamples, string Status)
{
    public const int NotConfident = -2;
    public const int NoFrontEnd = -3;
    public const int Unsupported = -4;
    /// <summary>Chunks fed to the native prime must be a multiple of this.</summary>
    public const int Block = 128;

    public bool Applied => ShiftSamples is not null;

    /// <summary>For processing.json: the shift in samples, or the reason there is none.</summary>
    public string Describe() => ShiftSamples is { } shift ? shift.ToString(System.Globalization.CultureInfo.InvariantCulture) : Status;

    public static DelayPrime FromCode(int code) => code switch
    {
        >= 0 => new DelayPrime(code, "primed"),
        NotConfident => new DelayPrime(null, "not_confident"),
        NoFrontEnd => new DelayPrime(null, "no_daf_frontend"),
        Unsupported => new DelayPrime(null, "unsupported_library"),
        _ => new DelayPrime(null, $"error_{code}"),
    };
}

/// <summary>
/// liblocalvqe.dll, loaded by full path so it can never pick up, or be picked
/// up as, another ggml in the process. Noise gate off: echo-only.
/// </summary>
public sealed unsafe class LocalVqeBackend : IEchoCancellationBackend, IEchoDelayPrimer
{
    private readonly nint library;
    private nint context;
    private readonly delegate* unmanaged[Cdecl]<nint, int> primeBegin;
    private readonly delegate* unmanaged[Cdecl]<nint, float*, float*, int, int> primeFeed;
    private readonly delegate* unmanaged[Cdecl]<nint, int> primeEnd;
    private readonly delegate* unmanaged[Cdecl]<nint, void> free;
    private readonly delegate* unmanaged[Cdecl]<nint, float*, float*, int, float*, int> processFrame;
    private readonly delegate* unmanaged[Cdecl]<nint, byte*> lastError;

    public int SampleRate { get; }
    public int HopSize { get; }

    public LocalVqeBackend(string libraryPath, string modelPath)
    {
        if (LocalVqeAssets.Sha256(modelPath) != LocalVqeAssets.ModelSha256)
            throw new EchoCancellationException($"LocalVQE model failed its SHA-256 check: {modelPath}");
        try { library = NativeLibrary.Load(libraryPath); }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
        {
            throw new EchoCancellationException($"could not load LocalVQE library at {libraryPath}: {exception.Message}", exception);
        }
        string? copied = null;
        try
        {
            nint Symbol(string name) => NativeLibrary.TryGetExport(library, name, out var address)
                ? address
                : throw new EchoCancellationException($"LocalVQE library is missing required symbol {name}");
            var create = (delegate* unmanaged[Cdecl]<byte*, nint>)Symbol("localvqe_new");
            free = (delegate* unmanaged[Cdecl]<nint, void>)Symbol("localvqe_free");
            processFrame = (delegate* unmanaged[Cdecl]<nint, float*, float*, int, float*, int>)Symbol("localvqe_process_frame_f32");
            lastError = (delegate* unmanaged[Cdecl]<nint, byte*>)Symbol("localvqe_last_error");
            var sampleRate = (delegate* unmanaged[Cdecl]<nint, int>)Symbol("localvqe_sample_rate");
            var hopLength = (delegate* unmanaged[Cdecl]<nint, int>)Symbol("localvqe_hop_length");
            var setNoiseGate = (delegate* unmanaged[Cdecl]<nint, int, float, int>)Symbol("localvqe_set_noise_gate");
            // Optional: a library without the whole-signal prime keeps online delay acquisition.
            if (NativeLibrary.TryGetExport(library, "localvqe_prime_delay_begin", out var begin)
                && NativeLibrary.TryGetExport(library, "localvqe_prime_delay_feed", out var feed)
                && NativeLibrary.TryGetExport(library, "localvqe_prime_delay_end", out var end))
            {
                primeBegin = (delegate* unmanaged[Cdecl]<nint, int>)begin;
                primeFeed = (delegate* unmanaged[Cdecl]<nint, float*, float*, int, int>)feed;
                primeEnd = (delegate* unmanaged[Cdecl]<nint, int>)end;
            }

            var native = NativeModelPath(modelPath, out copied);
            fixed (byte* path = native) context = create(path);
            if (context == 0) throw new EchoCancellationException("could not load the LocalVQE echo model: native loader returned an empty context");
            SampleRate = sampleRate(context);
            HopSize = hopLength(context);
            if (SampleRate != EchoCanceller.SampleRate || HopSize != EchoCanceller.FrameSize)
                throw new EchoCancellationException($"unsupported LocalVQE format: {SampleRate} Hz, {HopSize}-sample hops");
            var gate = setNoiseGate(context, 0, -45f);
            if (gate != 0) throw new EchoCancellationException($"LocalVQE could not disable the residual noise gate ({gate})");
        }
        catch
        {
            if (context != 0) free(context);
            context = 0;
            NativeLibrary.Free(library);
            throw;
        }
        finally
        {
            // The model is read whole by localvqe_new; a copy is not needed after.
            if (copied is not null) TryDelete(copied);
        }
    }

    public void Process(ReadOnlySpan<float> microphone, ReadOnlySpan<float> reference, Span<float> output)
    {
        ObjectDisposedException.ThrowIf(context == 0, this);
        if (microphone.Length != HopSize || reference.Length != HopSize || output.Length != HopSize)
            throw new EchoCancellationException($"LocalVQE needs {HopSize} samples per hop; got mic {microphone.Length}, reference {reference.Length}");
        int result;
        fixed (float* mic = microphone)
        fixed (float* far = reference)
        fixed (float* cleaned = output)
            result = processFrame(context, mic, far, HopSize, cleaned);
        if (result != 0)
        {
            var message = Marshal.PtrToStringUTF8((nint)lastError(context));
            throw new EchoCancellationException($"LocalVQE processing failed ({result}): {message ?? "unknown error"}");
        }
        foreach (var sample in output)
            if (!float.IsFinite(sample)) throw new EchoCancellationException("LocalVQE returned non-finite audio");
    }

    public int BeginDelayPrime()
    {
        ObjectDisposedException.ThrowIf(context == 0, this);
        return primeBegin == null ? DelayPrime.Unsupported : primeBegin(context);
    }

    public int FeedDelayPrime(ReadOnlySpan<float> microphone, ReadOnlySpan<float> reference)
    {
        ObjectDisposedException.ThrowIf(context == 0, this);
        if (primeFeed == null) return DelayPrime.Unsupported;
        if (microphone.Length != reference.Length || microphone.Length % DelayPrime.Block != 0)
            throw new EchoCancellationException($"LocalVQE delay prime needs equal chunks of a multiple of {DelayPrime.Block} samples");
        fixed (float* mic = microphone)
        fixed (float* far = reference)
            return primeFeed(context, mic, far, microphone.Length);
    }

    public int EndDelayPrime()
    {
        ObjectDisposedException.ThrowIf(context == 0, this);
        return primeEnd == null ? DelayPrime.Unsupported : primeEnd(context);
    }

    public void Dispose()
    {
        if (context == 0) return;
        free(context);
        context = 0;
        NativeLibrary.Free(library);
    }

    /// <summary>
    /// localvqe_new takes a narrow path that the MinGW C runtime opens in the
    /// ANSI code page; UTF-8 bytes of a non-ASCII path do not open. In order:
    /// the path itself when ASCII or representable in the ANSI code page, its
    /// 8.3 short name, else a verified copy in an ASCII-only folder.
    /// </summary>
    internal static byte[] NativeModelPath(string modelPath, out string? copied)
    {
        copied = null;
        var full = Path.GetFullPath(modelPath);
        if (Ascii.IsValid(full)) return Terminated(Encoding.ASCII.GetBytes(full));
        if (OperatingSystem.IsWindows())
        {
            var codePage = (int)GetACP();
            if (codePage == 65001) return Terminated(Encoding.UTF8.GetBytes(full));
            try
            {
                var ansi = CodePagesEncodingProvider.Instance.GetEncoding(codePage,
                    EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
                if (ansi is not null) return Terminated(ansi.GetBytes(full));
            }
            catch (EncoderFallbackException) { }
            var buffer = new char[32_768];
            var length = GetShortPathNameW(full, buffer, (uint)buffer.Length);
            if (length > 0 && length < buffer.Length && Ascii.IsValid(buffer.AsSpan(0, (int)length)))
                return Terminated(Encoding.ASCII.GetBytes(buffer, 0, (int)length));
        }
        foreach (var folder in AsciiFolders())
        {
            try
            {
                Directory.CreateDirectory(folder);
                var copy = Path.Combine(folder, $"localvqe-{Guid.NewGuid():N}.gguf");
                File.Copy(full, copy);
                if (LocalVqeAssets.Sha256(copy) != LocalVqeAssets.ModelSha256)
                {
                    TryDelete(copy);
                    continue;
                }
                copied = copy;
                return Terminated(Encoding.ASCII.GetBytes(copy));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        throw new EchoCancellationException($"LocalVQE cannot open a model at a non-ASCII path: {full}");
    }

    private static IEnumerable<string> AsciiFolders()
    {
        var candidates = new[]
        {
            Path.Combine(Path.GetTempPath(), "Amanu"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Amanu", "echo"),
            Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "AmanuTemp"),
        };
        return candidates.Where(folder => Ascii.IsValid(folder) && Path.IsPathFullyQualified(folder));
    }

    private static byte[] Terminated(byte[] bytes) => [.. bytes, 0];

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, [Out] char[] shortPath, uint length);
}

/// <summary>
/// The macOS EchoCanceller, hop for hop. LocalVQE's v1.4-AEC file model emits
/// the preceding 256-sample hop: one input hop stays pending, the startup
/// output is dropped, and <see cref="Finish"/> feeds one zero hop so the final
/// 16 ms is not lost and the cleaned microphone stays sample-aligned.
/// </summary>
public sealed class EchoCanceller : IDisposable
{
    public const int SampleRate = 16_000;
    public const int FrameSize = 256;
    /// <summary>One second of reference silence the model keeps control for: device delay and room decay.</summary>
    public const int ReferenceHangoverSamples = SampleRate;

    private enum Preservation { None, All, Mixed }

    private readonly IEchoCancellationBackend backend;
    private readonly float[] candidate = new float[FrameSize];
    private readonly float[] pendingMicrophone = new float[FrameSize];
    private readonly bool[] pendingMask = new bool[FrameSize];
    private Preservation pendingPreservation;
    private bool hasPending;
    private bool hasSeenActiveReference;
    private int silentReferenceSamples;
    private bool finished;

    public EchoCanceller(IEchoCancellationBackend backend)
    {
        if (backend.SampleRate != SampleRate || backend.HopSize != FrameSize)
            throw new EchoCancellationException($"unsupported LocalVQE format: {backend.SampleRate} Hz, {backend.HopSize}-sample hops");
        this.backend = backend;
    }

    /// <summary>
    /// Before any hop: fixes the echo delay from the whole signal, read from
    /// sources on the same timeline the stream will see. The online estimate
    /// freezes after ~3.5 s and can lock a wrong delay for the whole file. A
    /// backend that cannot prime, or is not confident, keeps online acquisition.
    /// </summary>
    public DelayPrime PrimeDelay(IMonoSampleSource microphone, IMonoSampleSource reference, long frames, CancellationToken cancellationToken = default)
    {
        if (hasPending || finished) throw new EchoCancellationException("the LocalVQE delay prime must come before the stream");
        if (backend is not IEchoDelayPrimer primer) return DelayPrime.FromCode(DelayPrime.Unsupported);
        var code = primer.BeginDelayPrime();
        if (code < 0) return DelayPrime.FromCode(code);
        var mic = new float[OfflineEcho.WindowSamples];
        var far = new float[OfflineEcho.WindowSamples];
        for (long position = 0; position < frames; position += OfflineEcho.WindowSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(OfflineEcho.WindowSamples, frames - position);
            OfflineEcho.Fill(microphone, mic.AsSpan(0, count));
            OfflineEcho.Fill(reference, far.AsSpan(0, count));
            // The native prime ignores a partial final block anyway.
            var whole = count - count % DelayPrime.Block;
            if (whole == 0) continue;
            code = primer.FeedDelayPrime(mic.AsSpan(0, whole), far.AsSpan(0, whole));
            if (code < 0) return DelayPrime.FromCode(code);
        }
        return DelayPrime.FromCode(primer.EndDelayPrime());
    }

    /// <summary>
    /// Writes the preceding input hop, now aligned with the model's output, to
    /// <paramref name="output"/>. False on the first call: that output is startup state.
    /// </summary>
    public bool Process(ReadOnlySpan<float> microphone, ReadOnlySpan<float> reference, Span<float> output)
    {
        if (finished) throw new EchoCancellationException("LocalVQE stream was already finished");
        if (microphone.Length != FrameSize || reference.Length != FrameSize)
            throw new EchoCancellationException($"LocalVQE needs {FrameSize} samples per hop; got mic {microphone.Length}, reference {reference.Length}");
        backend.Process(microphone, reference, candidate);
        var had = hasPending;
        if (had) Select(output);
        microphone.CopyTo(pendingMicrophone);
        pendingPreservation = Classify(reference);
        hasPending = true;
        return had;
    }

    /// <summary>Feeds one zero hop to obtain the delayed output for the final input.</summary>
    public bool Finish(Span<float> output)
    {
        if (finished) return false;
        finished = true;
        if (!hasPending) return false;
        hasPending = false;
        Span<float> zeros = stackalloc float[FrameSize];
        zeros.Clear();
        backend.Process(zeros, zeros, candidate);
        Select(output);
        return true;
    }

    /// <summary>
    /// A zero reference since the start proves no playback has happened, so the
    /// microphone is returned exactly. After playback the model keeps control for
    /// one second of reference silence; counted in samples so a transition in
    /// the middle of a hop neither lengthens nor shortens it.
    /// </summary>
    private Preservation Classify(ReadOnlySpan<float> reference)
    {
        bool? first = null;
        var mixed = false;
        for (var index = 0; index < reference.Length; index++)
        {
            bool preserve;
            if (reference[index] != 0)
            {
                hasSeenActiveReference = true;
                silentReferenceSamples = 0;
                preserve = false;
            }
            else if (!hasSeenActiveReference) preserve = true;
            else
            {
                silentReferenceSamples = Math.Min(silentReferenceSamples + 1, ReferenceHangoverSamples + 1);
                preserve = silentReferenceSamples > ReferenceHangoverSamples;
            }
            pendingMask[index] = preserve;
            if (first is null) first = preserve;
            else if (preserve != first) mixed = true;
        }
        return mixed ? Preservation.Mixed : first == true ? Preservation.All : Preservation.None;
    }

    private void Select(Span<float> output)
    {
        switch (pendingPreservation)
        {
            case Preservation.None:
                candidate.CopyTo(output);
                break;
            case Preservation.All:
                pendingMicrophone.CopyTo(output);
                break;
            default:
                for (var index = 0; index < FrameSize; index++)
                    output[index] = pendingMask[index] ? pendingMicrophone[index] : candidate[index];
                break;
        }
    }

    public void Dispose() => backend.Dispose();
}

/// <summary>Sequential mono 16 kHz samples. Fewer than asked means the source has ended.</summary>
public interface IMonoSampleSource
{
    int Read(Span<float> buffer);
}

public delegate void SampleWriter(ReadOnlySpan<float> samples);

/// <summary>An in-memory source, for tests and for already-decoded audio.</summary>
public sealed class ArraySampleSource(float[] samples) : IMonoSampleSource
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

/// <summary>A track that starts later on the shared timeline: <paramref name="lead"/> samples of silence, then the track.</summary>
public sealed class DelayedSampleSource(IMonoSampleSource inner, long lead) : IMonoSampleSource
{
    private long remainingLead = Math.Max(0, lead);

    public int Read(Span<float> buffer)
    {
        var silence = (int)Math.Min(buffer.Length, remainingLead);
        buffer[..silence].Clear();
        remainingLead -= silence;
        return silence == buffer.Length ? silence : silence + inner.Read(buffer[silence..]);
    }
}

/// <summary>
/// The offline pass of the macOS OfflineEchoAudio, over two sources already on
/// one timeline: reads bounded windows of 937 complete hops, keeps a single
/// model context warm across them, pads only at the true end, and writes a
/// cleaned microphone and the unchanged system audio of exactly
/// <c>frames</c> samples each.
/// </summary>
public static class OfflineEcho
{
    /// <summary>14.992 seconds: 937 complete hops, so no window boundary ever pads a hop.</summary>
    public const int WindowSamples = 15 * EchoCanceller.SampleRate - 15 * EchoCanceller.SampleRate % EchoCanceller.FrameSize;

    /// <summary>
    /// Whether the reference has any nonzero sample. A reference silent for the
    /// whole recording skips the model and keeps the microphone exact; any
    /// playback at all runs the model from t=0, leading silence included.
    /// </summary>
    public static bool HasReferenceActivity(IMonoSampleSource reference, CancellationToken cancellationToken = default)
    {
        var buffer = new float[WindowSamples];
        int read;
        while ((read = reference.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var sample in buffer.AsSpan(0, read))
                if (sample != 0) return true;
        }
        return false;
    }

    /// <param name="canceller">Null when the reference is silent throughout: the microphone is copied exactly.</param>
    public static void Run(
        IMonoSampleSource microphone, IMonoSampleSource reference, long frames, EchoCanceller? canceller,
        SampleWriter writeMicrophone, SampleWriter writeSystem, CancellationToken cancellationToken = default)
    {
        const int hop = EchoCanceller.FrameSize;
        var mic = new float[WindowSamples];
        var far = new float[WindowSamples];
        var local = new float[hop];
        var remote = new float[hop];
        var cleaned = new float[hop];
        int? pendingFrames = null;
        for (long position = 0; position < frames; position += WindowSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(WindowSamples, frames - position);
            Fill(microphone, mic.AsSpan(0, count));
            Fill(reference, far.AsSpan(0, count));
            for (var offset = 0; offset < count; offset += hop)
            {
                var length = Math.Min(hop, count - offset);
                Array.Clear(local);
                Array.Clear(remote);
                mic.AsSpan(offset, length).CopyTo(local);
                far.AsSpan(offset, length).CopyTo(remote);
                writeSystem(remote.AsSpan(0, length));
                if (canceller is null)
                {
                    writeMicrophone(local.AsSpan(0, length));
                    continue;
                }
                if (canceller.Process(local, remote, cleaned))
                    writeMicrophone(cleaned.AsSpan(0, pendingFrames ?? throw new EchoCancellationException("LocalVQE output came before its input")));
                pendingFrames = length;
            }
        }
        if (canceller is not null && canceller.Finish(cleaned))
            writeMicrophone(cleaned.AsSpan(0, pendingFrames ?? throw new EchoCancellationException("LocalVQE flushed without input")));
    }

    /// <summary>Reads exactly the span's length, with silence past the end of the source.</summary>
    internal static void Fill(IMonoSampleSource source, Span<float> buffer)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = source.Read(buffer[filled..]);
            if (read <= 0) break;
            filled += read;
        }
        buffer[filled..].Clear();
    }
}
