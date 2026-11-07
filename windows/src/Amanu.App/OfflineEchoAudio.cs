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

        /// <summary>The derived audio goes; the provider caches beside it stay for a retry.</summary>
        public void RemoveAudio() => OfflineEchoAudio.RemoveAudio(Directory);

        public void Delete()
        {
            try { if (IODirectory.Exists(Directory)) IODirectory.Delete(Directory, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    public static Result Prepare(SessionAudio audio, Func<IEchoCancellationBackend> backendFactory, CancellationToken cancellationToken)
    {
        if (!audio.BothSides) throw new InvalidOperationException("Echo cancellation needs both the mic and the system track.");
        var work = WorkDirectory(audio);
        var info = IODirectory.CreateDirectory(work);
        info.Attributes |= FileAttributes.Hidden;
        RemoveAudio(work);
        var completed = false;
        try
        {
            var micSource = Path.Combine(work, ".decoded-mic.wav");
            var systemSource = Path.Combine(work, ".decoded-system.wav");
            Decode(audio.Microphone!, micSource, cancellationToken);
            Decode(audio.System!, systemSource, cancellationToken);

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

            var result = new Result(work, Path.Combine(work, "mic.wav"), Path.Combine(work, "system.wav"), start, frames, active);
            using var canceller = active ? new EchoCanceller(backendFactory()) : null;
            if (canceller is not null)
            {
                // The prime scans exactly the aligned signal the stream will see, from t=0.
                using var mic = new WaveSource(micSource);
                using var reference = new WaveSource(systemSource);
                result = result with
                {
                    DelayPrime = canceller.PrimeDelay(new DelayedSampleSource(mic, micLead), new DelayedSampleSource(reference, systemLead), frames, cancellationToken),
                };
            }
            using (var mic = new WaveSource(micSource))
            using (var reference = new WaveSource(systemSource))
            using (var micOut = new WaveFileWriter(result.Microphone, WaveFormat.CreateIeeeFloatWaveFormat(EchoCanceller.SampleRate, 1)))
            using (var systemOut = new WaveFileWriter(result.System, WaveFormat.CreateIeeeFloatWaveFormat(EchoCanceller.SampleRate, 1)))
            {
                OfflineEcho.Run(new DelayedSampleSource(mic, micLead), new DelayedSampleSource(reference, systemLead), frames, canceller,
                    samples => micOut.Write(MemoryMarshal.AsBytes(samples)),
                    samples => systemOut.Write(MemoryMarshal.AsBytes(samples)),
                    cancellationToken);
            }
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

    private sealed class WaveSource(string path) : IMonoSampleSource, IDisposable
    {
        private readonly WaveFileReader reader = new(path);
        private ISampleProvider? samples;

        public int Read(Span<float> buffer) => (samples ??= reader.ToSampleProvider()).Read(buffer);

        public void Dispose() => reader.Dispose();
    }
}
