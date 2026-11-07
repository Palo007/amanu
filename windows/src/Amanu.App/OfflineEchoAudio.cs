using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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

    public sealed record Result(string Directory, string Microphone, string System, int OffsetMs, long Frames, bool ModelRan)
    {
        /// <summary>Null when the model did not run (no playback in the whole recording).</summary>
        public DelayPrime? DelayPrime { get; init; }

        public string Timings { get; init; } = "";

        /// <summary>The derived audio goes; the provider caches beside it stay for a retry.</summary>
        public void RemoveAudio() => OfflineEchoAudio.RemoveAudio(Directory);

        public void Delete()
        {
            try { if (IODirectory.Exists(Directory)) IODirectory.Delete(Directory, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    public static Result Prepare(SessionAudio audio, Func<int?, IEchoCancellationBackend> backendFactory, CancellationToken cancellationToken)
    {
        if (!audio.BothSides) throw new InvalidOperationException("Echo cancellation needs both the mic and the system track.");
        var work = WorkDirectory(audio);
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
            var workers = active ? Workers(frames) : 0;
            var cancellers = new List<EchoCanceller>();
            try
            {
                if (active)
                    for (var index = 0; index < workers; index++)
                        cancellers.Add(new EchoCanceller(backendFactory(workers > 1 ? 1 : ModelThreads())));
                float[]? cleaned = null;
                if (active)
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
    /// The session's hidden folder for this processor and these exact sources.
    /// A changed track, offset or processor revision is a different folder.
    /// </summary>
    public static string WorkDirectory(SessionAudio audio)
    {
        var sources = string.Join('\n', new[] { (audio.Microphone!, audio.MicrophoneOffsetMs), (audio.System!, audio.SystemOffsetMs) }.Select(source =>
        {
            var file = new FileInfo(source.Item1);
            return $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{source.Item2}";
        }));
        var identity = $"{LocalVqeAssets.ProcessorVersion}\n{LocalVqeAssets.ModelSha256}\n{sources}";
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(audio.Directory, $"{FolderPrefix}{digest[..20]}");
    }

    /// <summary>Every echo folder of a session, for a re-transcription to throw away with the other caches.</summary>
    public static void Clear(string sessionDirectory)
    {
        if (!IODirectory.Exists(sessionDirectory)) return;
        foreach (var folder in IODirectory.EnumerateDirectories(sessionDirectory, FolderPrefix + "*"))
        {
            try { IODirectory.Delete(folder, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void RemoveAudio(string work)
    {
        foreach (var name in new[] { "mic.wav", "system.wav", ".decoded-mic.wav", ".decoded-system.wav" })
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
