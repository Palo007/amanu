using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Amanu.Core.Processing;

/// <summary>
/// The LocalVQE build Amanu ships: a CPU-only DLL and its echo model under
/// <c>echo-runtime\</c> beside the app, never downloaded at run time. Kept
/// apart from <c>local-runtime\</c>, whose ggml is a different build.
/// </summary>
/// <param name="ProcessorVersion">
/// The audio path's revision with this model: changing it changes the .echo-* cache
/// folder's name. v3 is macOS's; v4 adds the whole-signal delay prime.
/// </param>
/// <param name="SonoraTuning">
/// For Sonora only: AEC3 settings changed from WebRTC's defaults, as
/// <c>path=value</c> entries separated by <c>;</c>; null keeps the defaults.
/// </param>
/// <param name="SonoraPrime">
/// For Sonora only: fix AEC3's echo delay with LocalVQE's delay prime first; null lets AEC3 estimate it.
/// </param>
public sealed record LocalVqeModel(string Key, string FileName, string Sha256, string ProcessorVersion, string? SonoraTuning = null,
    SonoraPrime? SonoraPrime = null)
{
    /// <summary>Sonora is not a LocalVQE model but shares this record, so the setting, the cache identity and the ledger treat them all alike.</summary>
    public bool IsSonora => FileName == SonoraBackend.LibraryName;
}

public static class LocalVqeAssets
{
    public const string RuntimeFolder = "echo-runtime";
    public const string LibraryName = "liblocalvqe.dll";

    /// <summary>
    /// The adaptive filter alone, without the neural mask. It removed echo 2.2 times
    /// faster than the 200K model on two real calls (65 s against 145 s, 78 s against
    /// 169 s) with the same word errors within three words.
    /// </summary>
    /// <summary>
    /// Sonora, a Rust port of WebRTC's AEC3 echo canceller (sonora_ffi.dll, BSD-3-Clause).
    /// On two real calls it removed echo 13 to 15 times faster than the 2.7K model
    /// (4.5 s against 66.5 s, 5.9 s against 74.4 s) with the same word errors within
    /// three words, so it is the default. FileName is the DLL itself: it has no model file.
    /// </summary>
    public static readonly LocalVqeModel Sonora = new("sonora", SonoraBackend.LibraryName, SonoraSha256,
        "sonora-aec3-1632d47-amanu-v4");

    /// <summary>
    /// sonora_ffi.dll built with Amanu's patch (windows/native/sonora/amanu-aec3-tuning.patch),
    /// which adds wap_create_with_aec3_tuning and wap_aec3_debug. Without a tuning it runs exactly as before.
    /// </summary>
    public const string SonoraSha256 = "35f84d9855dd22ed7a27fe99e444099d80f6a203bee0518c79801d8839e9bab5";

    /// <summary>
    /// AEC3's linear filter alone: its output before the nonlinear suppressor, which is
    /// what lowers the near voice while both people talk. On the quality suite it kept the
    /// voice but removed only 2.7 dB of echo, so the default 52 ms filter does little here.
    /// </summary>
    public static readonly LocalVqeModel SonoraLinear = new("sonora-linear", SonoraBackend.LibraryName, SonoraSha256,
        "sonora-aec3-1632d47-amanu-v4-linear", "output=linear");

    /// <summary>
    /// The suppressor treats a block as near speech sooner (echo below the voice instead
    /// of a quarter of it, 4 blocks instead of 12, and holds it 400 ms), and while it does,
    /// it lets bands through until the echo is well above the voice.
    /// </summary>
    public static readonly LocalVqeModel SonoraNearend = new("sonora-nearend", SonoraBackend.LibraryName, SonoraSha256,
        "sonora-aec3-1632d47-amanu-v4-nearend", NearendTuning);

    /// <summary>The near-end tuning, and the normal tuning also suppresses a band only at twice the default echo-to-voice ratio.</summary>
    public static readonly LocalVqeModel SonoraGentle = new("sonora-gentle", SonoraBackend.LibraryName, SonoraSha256,
        "sonora-aec3-1632d47-amanu-v4-gentle", NearendTuning +
        ";suppressor.normal_tuning.mask_lf.enr_transparent=0.6;suppressor.normal_tuning.mask_lf.enr_suppress=0.8" +
        ";suppressor.normal_tuning.mask_hf.enr_transparent=0.14;suppressor.normal_tuning.mask_hf.enr_suppress=0.2");

    /// <summary>
    /// A 160 ms linear filter instead of 52 ms, because the test rooms ring for 300 to 550 ms
    /// and the short filter cleaned the voice by only 1 to 4 dB, leaving the suppressor to cut the rest.
    /// </summary>
    public static readonly LocalVqeModel SonoraLong = new("sonora-long", SonoraBackend.LibraryName, SonoraSha256,
        "sonora-aec3-1632d47-amanu-v4-long", FilterBlocks(40));

    /// <summary>A 320 ms linear filter.</summary>
    public static readonly LocalVqeModel SonoraLong80 = new("sonora-long80", SonoraBackend.LibraryName, SonoraSha256,
        "sonora-aec3-1632d47-amanu-v4-long80", FilterBlocks(80));

    /// <summary>The 320 ms filter and the near-end tuning together.</summary>
    public static readonly LocalVqeModel SonoraLong80Nearend = new("sonora-long80-nearend", SonoraBackend.LibraryName, SonoraSha256,
        "sonora-aec3-1632d47-amanu-v4-long80-nearend", FilterBlocks(80) + ";" + NearendTuning);

    /// <summary>
    /// AEC3 takes its echo delay from the delay prime instead of estimating it, with a 256 ms
    /// filter and the first 60 s run once before the stream. In the quality suite's rooms
    /// AEC3's own delay estimate jumped about 90 times in 150 s, and every jump reset the
    /// linear filter, so the filter never covered the 300 to 550 ms room tails. Output is the
    /// linear filter's, before the suppressor, which lowered the near voice in double talk.
    /// When the delay is not the same over the recording, plain Sonora runs instead.
    /// </summary>
    public static readonly LocalVqeModel SonoraDelayLinear = new("sonora-delay-linear", SonoraBackend.LibraryName, SonoraSha256,
        "sonora-aec3-1632d47-amanu-v4-delay-linear", FilterBlocks(64) + ";output=linear", new SonoraPrime(60));

    /// <summary>The delay prime, filter and warm-up of <see cref="SonoraDelayLinear"/>, with the suppressor in its near-end tuning.</summary>
    public static readonly LocalVqeModel SonoraDelayNearend = new("sonora-delay-nearend", SonoraBackend.LibraryName, SonoraSha256,
        "sonora-aec3-1632d47-amanu-v4-delay-nearend", FilterBlocks(64) + ";" + NearendTuning, new SonoraPrime(60));

    /// <summary>
    /// The delay prime, filter and warm-up of <see cref="SonoraDelayLinear"/> with WebRTC's
    /// suppressor, which may lower no band below half its level (6 dB): the linear output
    /// alone left enough echo in loud rooms to be transcribed as words.
    /// </summary>
    public static readonly LocalVqeModel SonoraDelayFloor = new("sonora-delay-floor", SonoraBackend.LibraryName, SonoraSha256,
        "sonora-aec3-1632d47-amanu-v4-delay-floor", FilterBlocks(64) + GainFloor, new SonoraPrime(60));

    /// <summary><see cref="SonoraDelayFloor"/> with the suppressor in its near-end tuning.</summary>
    public static readonly LocalVqeModel SonoraDelayNearendFloor = new("sonora-delay-nearend-floor", SonoraBackend.LibraryName, SonoraSha256,
        "sonora-aec3-1632d47-amanu-v4-delay-nearend-floor", FilterBlocks(64) + ";" + NearendTuning + GainFloor, new SonoraPrime(60));

    /// <summary>The suppressor keeps at least half of every band.</summary>
    private const string GainFloor = ";suppressor.amanu_gain_floor=0.5";

    /// <summary>All four filters at the same length: AEC3 runs the initial pair for its first 2.5 s.</summary>
    private static string FilterBlocks(int blocks) =>
        $"filter.refined.length_blocks={blocks};filter.coarse.length_blocks={blocks}" +
        $";filter.refined_initial.length_blocks={blocks};filter.coarse_initial.length_blocks={blocks}";

    private const string NearendTuning =
        "suppressor.dominant_nearend_detection.enr_threshold=1" +
        ";suppressor.dominant_nearend_detection.snr_threshold=10" +
        ";suppressor.dominant_nearend_detection.trigger_threshold=4" +
        ";suppressor.dominant_nearend_detection.hold_duration=100" +
        ";suppressor.nearend_tuning.mask_lf.enr_transparent=1.5;suppressor.nearend_tuning.mask_lf.enr_suppress=3" +
        ";suppressor.nearend_tuning.mask_hf.enr_transparent=0.5;suppressor.nearend_tuning.mask_hf.enr_suppress=1.5";

    public static readonly LocalVqeModel Fast = new("fast", "localvqe-v1.4-aec-2.7K-f32.gguf",
        "d79f824f6ee6f58b2a7108d1df90db51c42c15a7400a5addbd3be9972ba657a4",
        "localvqe-v1.4-aec-2.7k-f32-amanu-v4-delay-prime");

    /// <summary>The same filter followed by a small neural mask, which also removes heavily reverberant echo.</summary>
    public static readonly LocalVqeModel Thorough = new("thorough", "localvqe-v1.4-aec-200K-f32.gguf",
        "b6e43138588a83bfe903ab5e143b4020b91c1e1629f5a575ac5855ff0003c731",
        "localvqe-v1.4-aec-200k-f32-amanu-v4-delay-prime");

    public static readonly IReadOnlyList<LocalVqeModel> Models = [Sonora, Fast, Thorough, SonoraLinear, SonoraNearend, SonoraGentle, SonoraLong, SonoraLong80, SonoraLong80Nearend, SonoraDelayLinear, SonoraDelayNearend, SonoraDelayFloor, SonoraDelayNearendFloor];

    public static LocalVqeModel Default => Sonora;

    /// <summary>The model the echo_model setting names; an unknown name gets the default rather than no echo removal.</summary>
    public static LocalVqeModel For(string? key) =>
        Models.FirstOrDefault(model => string.Equals(model.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Default;

    public sealed record Paths(string Library, string Model);

    public static Paths Resolve(string? applicationDirectory = null, LocalVqeModel? model = null)
    {
        var root = Path.Combine(applicationDirectory ?? AppContext.BaseDirectory, RuntimeFolder);
        model ??= Default;
        // Sonora is one DLL with nothing to load into it, so both paths name that DLL.
        var paths = model.IsSonora
            ? new Paths(Path.Combine(root, model.FileName), Path.Combine(root, model.FileName))
            : new Paths(Path.Combine(root, LibraryName), Path.Combine(root, model.FileName));
        foreach (var path in new[] { paths.Library, paths.Model })
            if (!File.Exists(path)) throw new EchoCancellationException($"echo removal asset is missing: {path}");
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
/// The library is loaded once per path and never unloaded: its ggml/MinGW
/// worker threads outlive localvqe_free, and FreeLibrary under them left the
/// loader lock held, so no new thread could start and the queue froze.
/// </summary>
public sealed unsafe class LocalVqeBackend : IEchoCancellationBackend, IEchoDelayPrimer
{
    private static readonly Dictionary<string, nint> Loaded = new(StringComparer.OrdinalIgnoreCase);
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

    /// <param name="threads">Compute threads of this context; null keeps the library default.</param>
    public LocalVqeBackend(string libraryPath, string modelPath, int? threads = null)
    {
        var digest = LocalVqeAssets.Sha256(modelPath);
        if (!LocalVqeAssets.Models.Any(model => model.Sha256 == digest))
            throw new EchoCancellationException($"LocalVQE model failed its SHA-256 check: {modelPath}");
        try { library = LoadOnce(libraryPath); }
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
            lock (ThreadSetting)
            {
                // The library reads GGML_NTHREADS from the C runtime's environment when it builds a context.
                if (threads is { } count) PutEnvironment("GGML_NTHREADS", count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                try { fixed (byte* path = native) context = create(path); }
                finally { if (threads is not null) PutEnvironment("GGML_NTHREADS", ""); }
            }
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
    }

    private static nint LoadOnce(string libraryPath)
    {
        var key = Path.GetFullPath(libraryPath);
        lock (Loaded)
        {
            if (!Loaded.TryGetValue(key, out var handle))
                Loaded[key] = handle = NativeLibrary.Load(key);
            return handle;
        }
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
                if (LocalVqeAssets.Sha256(copy) != LocalVqeAssets.Sha256(full))
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

    private static readonly object ThreadSetting = new();

    private static void PutEnvironment(string name, string value) => _putenv_s(name, value);

    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int _putenv_s(string name, string value);

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
    /// <summary>
    /// Starts the playback history as if every sample before this stream had been
    /// seen: for a stream that begins in the middle of a recording.
    /// </summary>
    public void SeedReferenceHistory(bool seenActiveReference, int silentSamples)
    {
        if (hasPending || finished) throw new EchoCancellationException("the reference history must be set before the stream");
        hasSeenActiveReference = seenActiveReference;
        silentReferenceSamples = seenActiveReference ? Math.Clamp(silentSamples, 0, ReferenceHangoverSamples + 1) : 0;
    }

    /// <summary>Time spent inside the model call (frame processing only).</summary>
    public TimeSpan NativeTime { get; private set; }

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
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        backend.Process(microphone, reference, candidate);
        NativeTime += System.Diagnostics.Stopwatch.GetElapsedTime(started);
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

    /// <summary>Warm-up before each block that starts mid-file, in seconds of audio.</summary>
    public const int DefaultWarmupSeconds = 10;

    /// <summary>
    /// Echo removal on several blocks at once. Every canceller owns its own model
    /// state, so each block after the first starts <paramref name="warmupSamples"/>
    /// earlier and drops that output: the state has settled when the block begins.
    /// Block edges sit on hop edges; hop framing, delay prime and the
    /// playback-silence rule are the same as in <see cref="Run"/>. Returns the
    /// prime of the first canceller.
    /// </summary>
    public static DelayPrime RunParallel(
        Func<IMonoSampleSource> openMicrophone, Func<IMonoSampleSource> openReference, long frames,
        IReadOnlyList<EchoCanceller> cancellers, int warmupSamples, float[] cleaned, CancellationToken cancellationToken = default)
    {
        const int hop = EchoCanceller.FrameSize;
        if (cleaned.Length < frames) throw new ArgumentException("output buffer is shorter than the recording", nameof(cleaned));
        var totalHops = (frames + hop - 1) / hop;
        var perWorker = Math.Max(1, (totalHops + cancellers.Count - 1) / cancellers.Count);
        var warmupHops = warmupSamples / hop;
        var primes = new DelayPrime[cancellers.Count];
        Parallel.For(0, cancellers.Count, new ParallelOptions { MaxDegreeOfParallelism = cancellers.Count, CancellationToken = cancellationToken }, worker =>
        {
            var canceller = cancellers[worker];
            var hopStart = worker * perWorker;
            var hopEnd = Math.Min(totalHops, hopStart + perWorker);
            if (hopStart >= hopEnd) return;
            var first = Math.Max(0, hopStart - warmupHops);

            var microphone = openMicrophone();
            var reference = openReference();
            try
            {
                var primeMic = openMicrophone();
                var primeFar = openReference();
                try { primes[worker] = canceller.PrimeDelay(primeMic, primeFar, frames, cancellationToken); }
                finally { (primeMic as IDisposable)?.Dispose(); (primeFar as IDisposable)?.Dispose(); }

                // Playback history before the first hop this canceller sees.
                var skipped = (long)first * hop;
                var seen = false;
                var silent = 0L;
                var scratch = new float[WindowSamples];
                var micScratch = new float[WindowSamples];
                for (long done = 0; done < skipped;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(scratch.Length, skipped - done);
                    Fill(reference, scratch.AsSpan(0, count));
                    Fill(microphone, micScratch.AsSpan(0, count));
                    for (var index = 0; index < count; index++)
                    {
                        if (scratch[index] != 0) { seen = true; silent = 0; }
                        else if (seen) silent++;
                    }
                    done += count;
                }
                canceller.SeedReferenceHistory(seen, (int)Math.Min(silent, int.MaxValue));

                var local = new float[hop];
                var remote = new float[hop];
                var output = new float[hop];
                for (var k = first; k <= hopEnd; k++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (k == hopEnd && hopEnd == totalHops) break;
                    Array.Clear(local);
                    Array.Clear(remote);
                    var length = (int)Math.Min(hop, frames - (long)k * hop);
                    Fill(microphone, local.AsSpan(0, length));
                    Fill(reference, remote.AsSpan(0, length));
                    if (!canceller.Process(local, remote, output)) continue;
                    var previous = k - 1;
                    if (previous < hopStart) continue;
                    var previousLength = (int)Math.Min(hop, frames - (long)previous * hop);
                    output.AsSpan(0, previousLength).CopyTo(cleaned.AsSpan((int)((long)previous * hop), previousLength));
                }
                if (hopEnd == totalHops)
                {
                    if (!canceller.Finish(output)) throw new EchoCancellationException("LocalVQE flushed without input");
                    var last = totalHops - 1;
                    var lastLength = (int)Math.Min(hop, frames - last * hop);
                    output.AsSpan(0, lastLength).CopyTo(cleaned.AsSpan((int)(last * hop), lastLength));
                }
            }
            finally { (microphone as IDisposable)?.Dispose(); (reference as IDisposable)?.Dispose(); }
        });
        return primes[0];
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
