using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using Amanu.Core.Processing;
using NAudio.Wave;
using SherpaOnnx;

namespace Amanu.App;

/// <summary>
/// Splits the call track by voice with sherpa-onnx (pyannote segmentation and a
/// WeSpeaker voice model), on the CPU. It runs in a child process of Amanu.exe,
/// as speech recognition runs in transcribe-cli: onnxruntime makes its own
/// threads, and only a process priority class reaches them, so BelowNormal is
/// set on the child rather than hoped for in this process.
/// </summary>
internal static class CallDiarizer
{
    public const string Argument = "--diarize";

    // The values amanu-gemini's diarize.py was tuned with.
    private const float MinDurationOn = 0.3f;
    private const float MinDurationOff = 0.5f;

    /// <summary>The voice stretches of the call track, in transcript time (the track's offset added).</summary>
    public static async Task<IReadOnlyList<SpeakerTurn>> RunAsync(
        ModelManager models, string track, int offsetMs, double threshold, CancellationToken cancellationToken)
    {
        await models.EnsureDiarizationReadyAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            // The same gate as recognition: live transcription takes the CPU
            // back, and the run starts over once it is done.
            using var cpu = await models.CpuTranscription.EnterBatchAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var lines = await RunWorkerAsync(models, track, threshold, cpu.Token).ConfigureAwait(false);
                return Parse(lines, offsetMs);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
    }

    private static async Task<IReadOnlyList<string>> RunWorkerAsync(ModelManager models, string track, double threshold, CancellationToken token)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Amanu.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     Argument, track,
                     models.DiarizationPath(DiarizationModels.Segmentation), models.DiarizationPath(DiarizationModels.Embedding),
                     threshold.ToString(CultureInfo.InvariantCulture), Threads().ToString(CultureInfo.InvariantCulture),
                 })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the diarization worker.");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { }
        using var kill = token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { }
        });
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Diarization failed ({process.ExitCode}): {(await error.ConfigureAwait(false)).Trim()}");
        return (await output.ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Half the cores: the same share recognition leaves for the rest of the machine.</summary>
    private static int Threads() => Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>The worker's lines, "start_ms end_ms speaker", as turns in transcript time.</summary>
    internal static IReadOnlyList<SpeakerTurn> Parse(IEnumerable<string> lines, int offsetMs) =>
        lines.Select(line => line.Split(' '))
            .Where(parts => parts.Length == 3)
            .Select(parts => new SpeakerTurn(
                long.Parse(parts[0], CultureInfo.InvariantCulture) + offsetMs,
                long.Parse(parts[1], CultureInfo.InvariantCulture) + offsetMs,
                int.Parse(parts[2], CultureInfo.InvariantCulture)))
            .ToArray();

    /// <summary>
    /// The child: <c>--diarize track segmentation embedding threshold threads</c>.
    /// Writes one "start_ms end_ms speaker" line per stretch; errors go to stderr.
    /// </summary>
    public static int Main(string[] arguments)
    {
        try
        {
            var threads = int.Parse(arguments[5], CultureInfo.InvariantCulture);
            var config = new OfflineSpeakerDiarizationConfig();
            config.Segmentation.Pyannote.Model = arguments[2];
            config.Segmentation.NumThreads = threads;
            config.Embedding.Model = arguments[3];
            config.Embedding.NumThreads = threads;
            config.Clustering.NumClusters = -1;
            config.Clustering.Threshold = float.Parse(arguments[4], CultureInfo.InvariantCulture);
            config.MinDurationOn = MinDurationOn;
            config.MinDurationOff = MinDurationOff;
            using var diarization = new OfflineSpeakerDiarization(config);
            var samples = ReadMono(arguments[1], diarization.SampleRate);
            var output = Console.Out;
            foreach (var segment in diarization.Process(samples))
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{(long)Math.Round(segment.Start * 1000)} {(long)Math.Round(segment.End * 1000)} {segment.Speaker}"));
            output.Flush();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    /// <summary>The whole track as mono floats at the model's rate (16 kHz).</summary>
    private static float[] ReadMono(string path, int sampleRate)
    {
        if (sampleRate != 16_000) throw new InvalidOperationException($"The diarization model wants {sampleRate} Hz; Amanu gives 16000.");
        using var reader = new AudioFileReader(path);
        var source = AudioPreprocessor.Mono16k(reader);
        var samples = new List<float>(capacity: (int)Math.Min(int.MaxValue, reader.TotalTime.TotalSeconds * 16_000 + 16_000));
        var buffer = new float[16_000];
        int read;
        while ((read = source.Read(buffer.AsSpan())) > 0)
            samples.AddRange(buffer.AsSpan(0, read));
        return [.. samples];
    }
}
