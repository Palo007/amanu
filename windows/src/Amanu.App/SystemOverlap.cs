using System.Diagnostics;
using System.IO;
using Amanu.Core.Processing;

namespace Amanu.App;

/// <summary>
/// Roadmap-speed step 3: echo removal changes only the mic, so the call track is
/// recognized from the original audio while the mic is being cleaned. It runs under
/// the echo pass's CPU gate lease, so live preemption cancels it with the echo work;
/// a finished result survives a preemption. Any failure leaves <see cref="Result"/>
/// empty, and recognition then runs that track as before.
/// </summary>
internal sealed class SystemOverlap(LocalTranscriptionEngine engine, SessionAudio audio, int threads, Action<string, TimeSpan, string>? log)
{
    /// <summary>
    /// Off unless AMANU_ASR_OVERLAP is "1" or "on": measured on the GPU path it gave no gain
    /// (the two tracks already ran together, and the mic is the longer one).
    /// </summary>
    public static bool Enabled(string? setting) =>
        string.Equals(setting?.Trim(), "1", StringComparison.Ordinal) || string.Equals(setting?.Trim(), "on", StringComparison.OrdinalIgnoreCase);

    /// <summary>Threads for the call track while echo removal runs. AMANU_ASR_OVERLAP_THREADS overrides the default of half.</summary>
    public static int OverlapThreads(string? setting, int total) =>
        int.TryParse(setting, out var value) && value > 0 ? Math.Min(value, Math.Max(1, total)) : Math.Max(1, total / 2);

    public IReadOnlyList<TranscriptSegment>? Result { get; private set; }

    /// <summary>The offset the segments carry: the earlier track's start, as in the echo-cleaned copy.</summary>
    public int OffsetMs => Math.Min(audio.MicrophoneOffsetMs, audio.SystemOffsetMs);

    /// <summary>Runs once; throws only <see cref="OperationCanceledException"/> (the lease was lost).</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Result is not null) return;
        var temp = Path.Combine(Path.GetTempPath(), "Amanu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var timer = Stopwatch.StartNew();
            var source = Path.Combine(temp, "system.wav");
            await Task.Run(() => OfflineEchoAudio.PrepareSystemOnly(audio, source, cancellationToken), cancellationToken).ConfigureAwait(false);
            var segments = await engine.TranscribeTrackAsync(source, SpeakerLabels.Them, OffsetMs, threads, cancellationToken).ConfigureAwait(false);
            Result = segments;
            log?.Invoke("asr.them.overlap", timer.Elapsed, $"{threads} threads");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Result = null;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
