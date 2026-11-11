using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amanu.Core.Processing;
using NAudio.Wave;
using IODirectory = System.IO.Directory;

namespace Amanu.App;

/// <summary>
/// Two temporary, aligned 16 kHz mono tracks for recognition — an echo-cleaned
/// copy of the mic and the system audio unchanged — in a hidden folder of the
/// session whose name carries the processor revision and the sources, so a
/// provider answer cached for the raw tracks is never reused for cleaned ones.
/// The recording, and the archive made from it, are never touched.
/// </summary>
public static class OfflineEchoAudio
{
    public const string FolderPrefix = ".echo-";

    /// <summary>
    /// Where the speech gate stood open in the cleaned mic, for the models that hand recognition
    /// the speech regions. It sits beside mic.wav, so it follows the track into the kept folder.
    /// </summary>
    public const string GateFile = "mic.gate.json";

    public sealed record Result(string Directory, string Microphone, string System, int OffsetMs, long Frames, bool ModelRan)
    {
        /// <summary>Null when the model did not run (no playback in the whole recording).</summary>
        public DelayPrime? DelayPrime { get; init; }

        public string Timings { get; init; } = "";

        /// <summary>What the echo solver found, for the log; null when the model has no solver or there was no playback.</summary>
        public string? Solver { get; init; }

        /// <summary>The mic was cleaned by the echo solver, not by the model's stream.</summary>
        public bool Solved { get; init; }

        /// <summary>
        /// Audio kept in <see cref="KeptFolder"/> for later passes. Nothing that
        /// cleans up after one pass removes it; only a re-transcription with
        /// keep_echo_cleaned_audio off does, through <see cref="Clear"/>.
        /// </summary>
        public bool Kept { get; init; }

        /// <summary>The derived audio goes; the provider caches beside it stay for a retry.</summary>
        public void RemoveAudio()
        {
            if (!Kept) OfflineEchoAudio.RemoveAudio(Directory);
        }

        public void Delete()
        {
            if (Kept) return;
            try { if (IODirectory.Exists(Directory)) IODirectory.Delete(Directory, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <param name="model">The model the backend runs; it names the work folder, so two models never share one. Null is the default model.</param>
    public static Result Prepare(SessionAudio audio, Func<int?, IEchoCancellationBackend> backendFactory, CancellationToken cancellationToken, LocalVqeModel? model = null)
    {
        if (!audio.BothSides) throw new InvalidOperationException("Echo cancellation needs both the mic and the system track.");
        var work = WorkDirectory(audio, model);
        var info = IODirectory.CreateDirectory(work);
        info.Attributes |= FileAttributes.Hidden;
        RemoveAudio(work);
        var completed = false;
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var marks = new List<string>();
            var native = TimeSpan.Zero;
            void Mark(string name) { marks.Add($"{name} {clock.Elapsed.TotalSeconds:0.0}s"); clock.Restart(); }
            var micSource = Path.Combine(work, ".decoded-mic.wav");
            var systemSource = Path.Combine(work, ".decoded-system.wav");
            Decode(audio.Microphone!, micSource, cancellationToken);
            Decode(audio.System!, systemSource, cancellationToken);
            Mark("decode");

            // One timeline from the earlier track's start, as the archive and the
            // engines already do; both outputs then carry that start as their offset.
            var start = Math.Min(audio.MicrophoneOffsetMs, audio.SystemOffsetMs);
            var micLead = (long)(audio.MicrophoneOffsetMs - start) * EchoCanceller.SampleRate / 1000;
            var systemLead = (long)(audio.SystemOffsetMs - start) * EchoCanceller.SampleRate / 1000;
            long frames;
            using (var mic = new WaveFileReader(micSource))
            using (var system = new WaveFileReader(systemSource))
                frames = Math.Max(micLead + mic.SampleCount, systemLead + system.SampleCount);

            bool active;
            using (var reference = new WaveSource(systemSource))
                active = OfflineEcho.HasReferenceActivity(new DelayedSampleSource(reference, systemLead), cancellationToken);

            Mark("scan");
            var result = new Result(work, Path.Combine(work, "mic.wav"), Path.Combine(work, "system.wav"), start, frames, active);
            float[]? cleaned = null;
            if (active && model?.Solver is { } solver)
            {
                var solved = OfflineEchoSolver.Cancel(
                    ReadAll(micSource, micLead, frames, cancellationToken), ReadAll(systemSource, systemLead, frames, cancellationToken), cancellationToken);
                // A recording that joins many devices has no one echo path to solve: cleaned
                // stays null, and the model's stream (plain Sonora) runs below.
                if (!solved.ManyPaths)
                    cleaned = solved.Sections.Count > 0 && solver.SuppressorFloor is { } floor
                        ? OfflineEchoSolver.Suppress(solved.Cleaned, solved.Echo, floor)
                        : solved.Cleaned;
                result = result with { Solver = Describe(solved), Solved = cleaned is not null };
                Mark("solve");
            }
            var streaming = active && cleaned is null;
            var workers = streaming ? Workers(frames) : 0;
            var cancellers = new List<EchoCanceller>();
            try
            {
                if (streaming)
                    for (var index = 0; index < workers; index++)
                        cancellers.Add(new EchoCanceller(backendFactory(workers > 1 ? 1 : ModelThreads())));
                if (streaming)
                {
                    cleaned = new float[frames];
                    // Each block: own model state, warmed up on the audio before it; the prime scans the whole aligned signal.
                    result = result with
                    {
                        DelayPrime = OfflineEcho.RunParallel(
                            () => new LeadSource(micSource, micLead), () => new LeadSource(systemSource, systemLead), frames,
                            cancellers, WarmupSeconds() * EchoCanceller.SampleRate, cleaned, cancellationToken),
                    };
                    foreach (var canceller in cancellers) native += canceller.NativeTime;
                }
                Mark("model");
                if (cleaned is not null && model?.Solver is { SpeechGate: true })
                {
                    if (SpeechGate.ModelPath() is { } gateModel)
                    {
                        var open = SpeechGate.Apply(cleaned, gateModel, out var openRuns, cancellationToken: cancellationToken);
                        // The gate's decisions go beside the mic so that recognition cuts the track at them.
                        if (model.Solver.CutRegions)
                            SpeechRegions.Gate.From(openRuns, frames).Save(Path.Combine(work, GateFile));
                        Mark(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"speech gate open {open:P0},"));
                    }
                    else marks.Add($"speech gate left out: no {SpeechGate.ModelName}");
                }
                using (var mic = new WaveSource(micSource))
                using (var reference = new WaveSource(systemSource))
                using (var micOut = new WaveFileWriter(result.Microphone, WaveFormat.CreateIeeeFloatWaveFormat(EchoCanceller.SampleRate, 1)))
                using (var systemOut = new WaveFileWriter(result.System, WaveFormat.CreateIeeeFloatWaveFormat(EchoCanceller.SampleRate, 1)))
                {
                    var position = 0L;
                    OfflineEcho.Run(new DelayedSampleSource(mic, micLead), new DelayedSampleSource(reference, systemLead), frames, null,
                        samples =>
                        {
                            // The mic copy is exact when the model did not run; else the cleaned block.
                            if (cleaned is null) micOut.Write(MemoryMarshal.AsBytes(samples));
                            else micOut.Write(MemoryMarshal.AsBytes(cleaned.AsSpan((int)position, samples.Length)));
                            position += samples.Length;
                        },
                        samples => systemOut.Write(MemoryMarshal.AsBytes(samples)),
                        cancellationToken);
                }
            }
            finally { foreach (var canceller in cancellers) canceller.Dispose(); }
            Mark("write");
            marks.Add($"workers {Math.Max(1, workers)}, native total {native.TotalSeconds:0.0}s");
            result = result with { Timings = string.Join(", ", marks) };
            foreach (var path in new[] { result.Microphone, result.System })
            {
                using var check = new WaveFileReader(path);
                if (check.SampleCount != frames)
                    throw new EchoCancellationException($"echo-cancelled audio came out {check.SampleCount} samples long, not {frames}");
            }
            File.Delete(micSource);
            File.Delete(systemSource);
            completed = true;
            return result;
        }
        finally
        {
            if (!completed) RemoveAudio(work);
        }
    }

    /// <summary>
    /// The system track as <see cref="Prepare"/> will write it to system.wav, made
    /// without the mic: the same decode, with the same lead of silence. Echo removal
    /// does not change this track, so recognition can start on it while the mic is
    /// cleaned. Only the tail (silence up to the longer track) is left out.
    /// </summary>
    public static void PrepareSystemOnly(SessionAudio audio, string destination, CancellationToken cancellationToken)
    {
        if (!audio.BothSides) throw new InvalidOperationException("Needs both the mic and the system track.");
        var start = Math.Min(audio.MicrophoneOffsetMs, audio.SystemOffsetMs);
        var lead = (long)(audio.SystemOffsetMs - start) * EchoCanceller.SampleRate / 1000;
        using var reader = new AudioFileReader(audio.System!);
        var samples = AudioPreprocessor.Mono16k(reader);
        using var writer = new WaveFileWriter(destination, WaveFormat.CreateIeeeFloatWaveFormat(EchoCanceller.SampleRate, 1));
        var buffer = new float[OfflineEcho.WindowSamples];
        for (var remaining = lead; remaining > 0;)
        {
            var count = (int)Math.Min(remaining, buffer.Length);
            writer.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<float>(new float[count])));
            remaining -= count;
        }
        int read;
        while ((read = samples.Read(buffer.AsSpan())) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Write(MemoryMarshal.AsBytes(buffer.AsSpan(0, read)));
        }
    }

    /// <summary>
    /// The session's hidden folder for this processor and these exact sources.
    /// A changed track, offset or processor revision is a different folder.
    /// </summary>
    public static string WorkDirectory(SessionAudio audio, LocalVqeModel? model = null)
    {
        model ??= LocalVqeAssets.Default;
        var sources = string.Join('\n', new[] { (audio.Microphone!, audio.MicrophoneOffsetMs), (audio.System!, audio.SystemOffsetMs) }.Select(source =>
        {
            var file = new FileInfo(source.Item1);
            return $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{source.Item2}";
        }));
        var identity = $"{model.ProcessorVersion}\n{model.Sha256}\n{sources}";
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(audio.Directory, $"{FolderPrefix}{digest[..20]}");
    }

    /// <summary>
    /// Every echo folder of a session, for a re-transcription to throw away with
    /// the other caches. With <paramref name="keepCleaned"/>, the kept audio stays
    /// and only the provider answers beside it go: a corrected language must not
    /// return the old text, but the cleaned mic does not depend on it.
    /// </summary>
    public static void Clear(string sessionDirectory, bool keepCleaned = false)
    {
        if (!IODirectory.Exists(sessionDirectory)) return;
        foreach (var folder in IODirectory.EnumerateDirectories(sessionDirectory, FolderPrefix + "*"))
        {
            if (keepCleaned && Path.GetFileName(folder) == KeptFolder)
            {
                ProviderCache.Clear(folder);
                continue;
            }
            try { IODirectory.Delete(folder, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The session's one folder of echo-cleaned audio kept between passes. It is
    /// named for the session, not for its sources: after the first pass the raw
    /// tracks become audio.m4a, and the halves split back out of it are new files
    /// each time, so a name made from the sources would never be found again.
    /// </summary>
    public const string KeptFolder = FolderPrefix + "kept";

    private const string KeptManifest = "kept.json";

    private sealed record KeptManifestData(int OffsetMs, long Frames, bool ModelRan, EchoCancellationRecord Echo);

    /// <summary>
    /// Moves a pass's cleaned tracks into <see cref="KeptFolder"/> as 16-bit PCM,
    /// half the size of the float copy and well past what recognition can tell
    /// apart, and returns them as the audio to hear. The pass's own folder goes.
    /// </summary>
    public static Result Keep(Result prepared, string sessionDirectory, EchoCancellationRecord echo, CancellationToken cancellationToken)
    {
        var kept = Path.Combine(sessionDirectory, KeptFolder);
        var partial = kept + ".partial";
        if (IODirectory.Exists(partial)) IODirectory.Delete(partial, recursive: true);
        var info = IODirectory.CreateDirectory(partial);
        info.Attributes |= FileAttributes.Hidden;
        try
        {
            ToPcm16(prepared.Microphone, Path.Combine(partial, "mic.wav"), cancellationToken);
            ToPcm16(prepared.System, Path.Combine(partial, "system.wav"), cancellationToken);
            var gate = Path.Combine(prepared.Directory, GateFile);
            if (File.Exists(gate)) File.Copy(gate, Path.Combine(partial, GateFile));
            File.WriteAllText(Path.Combine(partial, KeptManifest),
                JsonSerializer.Serialize(new KeptManifestData(prepared.OffsetMs, prepared.Frames, prepared.ModelRan, echo)));
            if (IODirectory.Exists(kept)) IODirectory.Delete(kept, recursive: true);
            IODirectory.Move(partial, kept);
        }
        catch
        {
            try { IODirectory.Delete(partial, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            throw;
        }
        prepared.Delete();
        return prepared with { Directory = kept, Microphone = Path.Combine(kept, "mic.wav"), System = Path.Combine(kept, "system.wav"), Kept = true };
    }

    /// <summary>
    /// The audio an earlier pass kept, with what processing.json said about it
    /// then, or null when there is none or it was made by another LocalVQE
    /// revision or model — those would clean the mic differently.
    /// </summary>
    public static (Result Audio, EchoCancellationRecord Echo)? TryReuse(string sessionDirectory, LocalVqeModel? model = null)
    {
        model ??= LocalVqeAssets.Default;
        var kept = Path.Combine(sessionDirectory, KeptFolder);
        try
        {
            var manifest = Path.Combine(kept, KeptManifest);
            if (!File.Exists(manifest)) return null;
            var data = JsonSerializer.Deserialize<KeptManifestData>(File.ReadAllText(manifest));
            if (data is null || data.Echo.Processor != model.ProcessorVersion) return null;
            if (data.ModelRan && data.Echo.ModelSha256 != model.Sha256) return null;
            var result = new Result(kept, Path.Combine(kept, "mic.wav"), Path.Combine(kept, "system.wav"), data.OffsetMs, data.Frames, data.ModelRan) { Kept = true };
            foreach (var path in new[] { result.Microphone, result.System })
            {
                if (!File.Exists(path)) return null;
                using var check = new WaveFileReader(path);
                if (check.SampleCount != data.Frames) return null;
            }
            return (result, data.Echo);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidDataException)
        {
            return null;
        }
    }

    private static void ToPcm16(string source, string destination, CancellationToken cancellationToken)
    {
        using var reader = new WaveFileReader(source);
        var samples = reader.ToSampleProvider();
        using var writer = new WaveFileWriter(destination, new WaveFormat(EchoCanceller.SampleRate, 16, 1));
        var buffer = new float[OfflineEcho.WindowSamples];
        var pcm = new byte[buffer.Length * 2];
        int read;
        while ((read = samples.Read(buffer.AsSpan())) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0; index < read; index++)
            {
                var value = (short)Math.Round(Math.Clamp(buffer[index], -1f, 1f) * short.MaxValue);
                pcm[index * 2] = (byte)value;
                pcm[index * 2 + 1] = (byte)(value >> 8);
            }
            writer.Write(pcm, 0, read * 2);
        }
    }

    private static void RemoveAudio(string work)
    {
        foreach (var name in new[] { "mic.wav", "system.wav", GateFile, ".decoded-mic.wav", ".decoded-system.wav" })
        {
            try { File.Delete(Path.Combine(work, name)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Any track Amanu reads, as 16 kHz mono float — a stereo call track mixed, as everywhere else.</summary>
    private static void Decode(string source, string destination, CancellationToken cancellationToken)
    {
        using var reader = new AudioFileReader(source);
        var samples = AudioPreprocessor.Mono16k(reader);
        using var writer = new WaveFileWriter(destination, WaveFormat.CreateIeeeFloatWaveFormat(EchoCanceller.SampleRate, 1));
        var buffer = new float[OfflineEcho.WindowSamples];
        int read;
        while ((read = samples.Read(buffer.AsSpan())) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Write(MemoryMarshal.AsBytes(buffer.AsSpan(0, read)));
        }
    }

    private static int WarmupSeconds() =>
        int.TryParse(Environment.GetEnvironmentVariable("AMANU_ECHO_WARMUP_S"), out var forced) && forced >= 0 ? forced : OfflineEcho.DefaultWarmupSeconds;

    /// <summary>
    /// Compute threads for one model stream; the library default unless AMANU_ECHO_THREADS says otherwise.
    /// The per-hop graph is tiny, so threads gain little (1: 141 s, 2: 120, 4: 109-117, 8: 114, 16: 172 on
    /// 16 logical cores; default 115-127). Output is bit-identical for every count.
    /// </summary>
    private static int? ModelThreads()
    {
        var forced = Environment.GetEnvironmentVariable("AMANU_ECHO_THREADS");
        if (int.TryParse(forced, out var count)) return count > 0 ? count : null;
        return null;
    }

    /// <summary>
    /// Blocks processed at once. 1 (default) keeps the stream exact. More blocks are faster
    /// (8 blocks: 3x) but the model state has a memory of minutes, so a block starting
    /// mid-file gives slightly different audio than one stream, whatever the warm-up.
    /// Opt in with AMANU_ECHO_WORKERS.
    /// </summary>
    public static int Workers(long frames)
    {
        _ = frames;
        return int.TryParse(Environment.GetEnvironmentVariable("AMANU_ECHO_WORKERS"), out var forced) && forced > 0 ? forced : 1;
    }

    /// <summary>A track on the shared timeline: a wave file read after <paramref name="lead"/> samples of silence.</summary>
    /// <summary>One track on the recording's timeline, whole, for the echo solver.</summary>
    private static float[] ReadAll(string path, long lead, long frames, CancellationToken cancellationToken)
    {
        var all = new float[frames];
        using var source = new LeadSource(path, lead);
        var position = 0;
        while (position < all.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(all.AsSpan(position, Math.Min(1 << 16, all.Length - position)));
            if (read == 0) break;
            position += read;
        }
        return all;
    }

    private static string Describe(OfflineEchoSolver.Result solved)
    {
        if (solved.ManyPaths) return "many echo paths";
        if (solved.Sections.Count == 0) return "no echo found, mic kept exact";
        var parts = solved.Sections.Select(section => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"delay {section.Lag:0} samples, drift {section.Slope / OfflineEchoSolver.SampleRate * 1e6:0.0} ppm"));
        return $"{solved.Sections.Count} echo path{(solved.Sections.Count == 1 ? "" : "s")}: {string.Join("; ", parts)}";
    }

    private sealed class LeadSource : IMonoSampleSource, IDisposable
    {
        private readonly WaveSource wave;
        private readonly DelayedSampleSource delayed;

        public LeadSource(string path, long lead) { wave = new WaveSource(path); delayed = new DelayedSampleSource(wave, lead); }

        public int Read(Span<float> buffer) => delayed.Read(buffer);

        public void Dispose() => wave.Dispose();
    }

    private sealed class WaveSource(string path) : IMonoSampleSource, IDisposable
    {
        private readonly WaveFileReader reader = new(path);
        private ISampleProvider? samples;

        public int Read(Span<float> buffer) => (samples ??= reader.ToSampleProvider()).Read(buffer);

        public void Dispose() => reader.Dispose();
    }
}
