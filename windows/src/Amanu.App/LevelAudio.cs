using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Amanu.Core.Processing;
using NAudio.Wave;
using IODirectory = System.IO.Directory;

namespace Amanu.App;

/// <summary>One track's levels as processing.json keeps them, in dBFS.</summary>
public sealed record LevelTrackRecord(
    string Side,
    double? FloorDb,
    double? SpeechDb,
    double? SpeechDbAfter = null,
    double? MaxGainDb = null,
    double? MinGainDb = null,
    int? LimitedBlocks = null,
    string? Skipped = null);

/// <summary>What speech level normalization did before recognition (roadmap-speed step 4).</summary>
public sealed record LevelNormalizationRecord(
    string Processor,
    string Mode,
    IReadOnlyList<LevelTrackRecord> Tracks,
    double? Seconds = null,
    string? Skipped = null);

/// <summary>
/// Copies of the tracks recognition hears, each brought toward −20 dBFS by
/// <see cref="SpeechLevels"/>, in a hidden folder of the session. Runs after echo
/// removal, on its output when there is one; each side gets its own gain. A track
/// already in range is heard as it is. The recording and its archive are never touched.
/// </summary>
public static class LevelAudio
{
    public const string FolderPrefix = ".levels-";

    public sealed record Result(string Directory, SessionAudio Audio, LevelNormalizationRecord Record, bool Changed)
    {
        /// <summary>The derived audio goes; the provider caches beside it stay for a retry.</summary>
        public void RemoveAudio()
        {
            if (!IODirectory.Exists(Directory)) return;
            foreach (var file in IODirectory.EnumerateFiles(Directory, "*.wav"))
            {
                try { File.Delete(file); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }

        public void Delete()
        {
            try { if (IODirectory.Exists(Directory)) IODirectory.Delete(Directory, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The audio to hear. <see cref="Result.Changed"/> is false when no track
    /// needed a gain; then <see cref="Result.Audio"/> is <paramref name="heard"/> itself.
    /// </summary>
    public static Result Prepare(string sessionDirectory, SessionAudio heard, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var work = WorkDirectory(sessionDirectory, heard);
        var info = IODirectory.CreateDirectory(work);
        info.Attributes |= FileAttributes.Hidden;
        var result = new Result(work, heard, new LevelNormalizationRecord(SpeechLevels.Version, "auto", []), false);
        result.RemoveAudio();
        var completed = false;
        try
        {
            var tracks = new List<LevelTrackRecord>();
            var audio = heard;
            foreach (var (side, path) in new[] { ("me", heard.Microphone), ("them", heard.System), ("single", heard.Single) })
            {
                if (path is null) continue;
                var (record, output) = Level(work, side, path, cancellationToken);
                tracks.Add(record);
                if (output is null) continue;
                audio = side switch
                {
                    "me" => audio with { Microphone = output },
                    "them" => audio with { System = output },
                    _ => audio with { Single = output },
                };
            }
            var changed = !ReferenceEquals(audio, heard);
            completed = true;
            return result with
            {
                Audio = changed ? audio with { Directory = work } : heard,
                Changed = changed,
                Record = result.Record with { Tracks = tracks, Seconds = Math.Round(timer.Elapsed.TotalSeconds, 1) },
            };
        }
        finally
        {
            if (!completed) result.RemoveAudio();
        }
    }

    private static (LevelTrackRecord Record, string? Output) Level(string work, string side, string source, CancellationToken cancellationToken)
    {
        // Decode once to 16 kHz mono float while measuring; the second pass reads that copy.
        var decoded = Path.Combine(work, $".decoded-{side}.wav");
        var meter = new SpeechLevels.Meter();
        using (var reader = new AudioFileReader(source))
        using (var writer = new WaveFileWriter(decoded, WaveFormat.CreateIeeeFloatWaveFormat(SpeechLevels.SampleRate, 1)))
        {
            var samples = AudioPreprocessor.Mono16k(reader);
            var buffer = new float[SpeechLevels.BlockSamples * 10];
            int read;
            while ((read = samples.Read(buffer.AsSpan())) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                meter.Add(buffer.AsSpan(0, read));
                writer.Write(MemoryMarshal.AsBytes(buffer.AsSpan(0, read)));
            }
        }
        var plan = SpeechLevels.Decide(meter.Finish());
        if (plan.GainDb is null)
        {
            File.Delete(decoded);
            return (new LevelTrackRecord(side, Round(plan.FloorDb), Round(plan.SpeechDb), Skipped: plan.Skipped), null);
        }
        var output = Path.Combine(work, $"{side}.wav");
        SpeechLevels.Outcome outcome;
        using (var reader = new WaveFileReader(decoded))
        using (var writer = new WaveFileWriter(output, WaveFormat.CreateIeeeFloatWaveFormat(SpeechLevels.SampleRate, 1)))
        {
            var samples = reader.ToSampleProvider();
            outcome = SpeechLevels.Apply(new ProviderSource(samples), plan,
                block => writer.Write(MemoryMarshal.AsBytes(block)), cancellationToken);
        }
        File.Delete(decoded);
        return (new LevelTrackRecord(side, Round(plan.FloorDb), Round(plan.SpeechDb), Round(outcome.SpeechDbAfter),
            Round(plan.MaxGainDb), Round(plan.MinGainDb), outcome.LimitedBlocks), output);
    }

    private static double? Round(double value) => double.IsFinite(value) ? Math.Round(value, 1) : null;

    /// <summary>
    /// The session's hidden folder for this method and these sources. An echo-cleaned
    /// source is named by its echo folder, which already carries its own sources,
    /// so a retry finds the same folder and its provider caches.
    /// </summary>
    public static string WorkDirectory(string sessionDirectory, SessionAudio heard)
    {
        var sources = string.Join('\n', new[] { heard.Microphone, heard.System, heard.Single }.Select(path =>
        {
            if (path is null) return "-";
            var file = new FileInfo(path);
            var folder = file.Directory?.Name ?? "";
            return folder.StartsWith(OfflineEchoAudio.FolderPrefix, StringComparison.Ordinal)
                ? $"{folder}/{file.Name}"
                : $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        }));
        var identity = $"{SpeechLevels.Version}\n{heard.MicrophoneOffsetMs}|{heard.SystemOffsetMs}\n{sources}";
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(sessionDirectory, $"{FolderPrefix}{digest[..20]}");
    }

    /// <summary>Every level folder of a session, for a re-transcription to throw away with the other caches.</summary>
    public static void Clear(string sessionDirectory)
    {
        if (!IODirectory.Exists(sessionDirectory)) return;
        foreach (var folder in IODirectory.EnumerateDirectories(sessionDirectory, FolderPrefix + "*"))
        {
            try { IODirectory.Delete(folder, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed class ProviderSource(ISampleProvider samples) : IMonoSampleSource
    {
        public int Read(Span<float> destination) => samples.Read(destination);
    }
}
