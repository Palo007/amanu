using System.Diagnostics;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Channels;
using Amanu.Core.Analytics;
using Amanu.Core.Configuration;
using Amanu.Core.Processing;
using static Amanu.Core.Localization.Localized;

namespace Amanu.App;

/// <param name="Progress">
/// How much of the whole pass is done, 0 to 1, for the progress bar; null
/// while the current step cannot say how far it is (a cloud engine, a model
/// that runs as one call), and the bar then only shows that work goes on.
/// </param>
public sealed record ProcessingStatus(string? SessionDirectory, string Stage, string Message, bool IsBusy, double? Progress = null);

/// <summary>
/// Where each step of a pass sits on the progress bar. Echo removal took about
/// as long as recognition on a measured call, so it gets a large share when it
/// runs; recognition fills most of the rest. Names and summary are one model
/// call each and get a sliver.
/// </summary>
internal static class PassProgress
{
    public const double Start = 0;
    public const double Echo = 0.03;
    public const double Levels = 0.28;
    public const double SpeakerNames = 0.90;
    public const double Summary = 0.95;

    /// <summary>Recognition runs from here to <see cref="SpeakerNames"/>.</summary>
    public static double RecognitionStart(bool echo, bool levels) => echo ? 0.30 : levels ? 0.08 : 0.05;

    public static double Recognition(double start, double share) =>
        start + (SpeakerNames - start) * Math.Clamp(share, 0, 1);
}

/// <summary>What one session's processing has cost so far, kept in its processing.json.</summary>
public sealed record ProcessingLedger(
    int TranscribeAttempts = 0,
    int NamesAttempts = 0,
    int SummaryAttempts = 0,
    bool HookRan = false,
    bool NamesAsked = false,
    string? LastError = null,
    EchoCancellationRecord? AudioEchoCancellation = null,
    LevelNormalizationRecord? AudioLevelNormalization = null,
    // Custom words put in place of close-sounding words (roadmap "Custom words"); the raw text is in transcript.raw.json.
    IReadOnlyList<CustomWordCorrection>? CustomWordCorrections = null,
    // Voices diarization heard on the call track of a local transcript (roadmap step 4).
    int? CallSpeakers = null);

/// <summary>Which audio recognition heard: LocalVQE's cleaned mic, the exact mic (no playback), or — skipped — the raw tracks.</summary>
public sealed record EchoCancellationRecord(
    string Processor,
    string? ModelSha256 = null,
    int? SampleRate = null,
    long? Frames = null,
    string? Mode = null,
    string? Skipped = null,
    // The whole-signal echo delay in samples, or why LocalVQE fell back to estimating it online.
    string? DelayPrime = null);

/// <summary>
/// Turns recordings into transcripts, names and summaries, one session at a time.
/// </summary>
/// <remarks>
/// What a failure costs depends on whose fault it was. A missing key, a model not
/// downloaded or no network is this computer's, and waits for the computer to
/// change without using up an attempt; only a failure of the recording itself
/// counts towards the three a transcription gets. Names and summaries give up
/// after five tries that reached a model, so a transcript is not re-sent to
/// every backend at every launch for ever. The on_stop command runs once, after
/// names and summary are done or definitively skipped.
/// </remarks>
public sealed class ProcessingCoordinator : IAsyncDisposable
{
    public const int TranscribeAttemptLimit = 3;
    public const int LanguageModelAttemptLimit = 5;

    private readonly Func<AppSettings> settings;
    private readonly Func<bool> configReadable;
    private readonly SecretStore secrets;
    private readonly ModelManager models;
    private readonly HttpClient httpClient;
    private readonly AnalyticsService analytics;
    private readonly LanguageModels languageModels;
    private readonly Func<string, AppSettings, ITranscriptionEngine>? localEngineFactory;
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<string> queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock queueLock = new();
    private readonly HashSet<string> again = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> preparing = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProcessingStatus> statuses = new(StringComparer.OrdinalIgnoreCase);
    private string? current;
    private Task? worker;

    public ProcessingCoordinator(
        Func<AppSettings> settings,
        Func<bool> configReadable,
        SecretStore secrets,
        ModelManager models,
        HttpClient httpClient,
        AnalyticsService analytics,
        Func<string, AppSettings, ITranscriptionEngine>? localEngineFactory = null)
    {
        this.settings = settings;
        this.configReadable = configReadable;
        this.secrets = secrets;
        this.models = models;
        this.httpClient = httpClient;
        this.analytics = analytics;
        this.localEngineFactory = localEngineFactory;
        languageModels = new LanguageModels(httpClient, secrets, settings);
    }

    public event EventHandler<ProcessingStatus>? StatusChanged;
    public event EventHandler? SessionsChanged;
    /// <summary>A new summary.md was written without errors; the argument is the session folder.</summary>
    public event EventHandler<string>? SummaryWritten;

    public LanguageModels LanguageModels => languageModels;

    public ProcessingStatus? StatusFor(string directory)
    {
        directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        lock (queueLock)
        {
            if (preparing.Contains(directory))
                return new(directory, "preparing", T("Preparing processing…", "Подготавливаю обработку…"), true);
            return statuses.GetValueOrDefault(directory) is { } status
                ? status with { IsBusy = queued.Contains(directory) } : null;
        }
    }

    /// <summary>Reserve this session while an action changes its files; other sessions can still queue.</summary>
    internal bool TryUseIdleSession(string directory, Action action)
    {
        directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        lock (queueLock)
        {
            if (queued.Contains(directory) || !preparing.Add(directory)) return false;
        }
        try { action(); return true; }
        finally { lock (queueLock) preparing.Remove(directory); }
    }

    public bool IsBusy
    {
        get { lock (queueLock) return queued.Count > 0; }
    }

    public void Start()
    {
        worker ??= Task.Run(() => WorkerAsync(lifetime.Token));
        Rescan();
    }

    /// <summary>
    /// Queues every session that has work left. Called at launch, when settings
    /// change — a new key, a model downloaded — when the network comes back, and
    /// on a slow clock, which is what lets an environmental failure recover.
    /// </summary>
    public void Rescan()
    {
        var root = settings().RecordingsDirectory;
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (NeedsWork(directory)) Enqueue(directory);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A recordings folder on a share that just went away; the next rescan tries again.
        }
    }

    private bool NeedsWork(string directory)
    {
        if (Path.GetFileName(directory).StartsWith('.')) return false;
        if (!File.Exists(Path.Combine(directory, "meta.json")) || File.Exists(Path.Combine(directory, ".recording.json"))) return false;
        if (Has(directory, "transcribe.failed")) return false;
        if (Has(directory, "transcribe.off")) return !ReadLedger(directory).HookRan;
        if (!Has(directory, "transcript.json") || Has(directory, TranscriptVersions.RequestFile)) return true;
        if (Has(directory, SummaryVersions.RequestFile)) return true;
        var current = settings();
        // A session finished while naming or summaries were off stays that way:
        // switching them on is not a request to send every old meeting to a model.
        // Finish on the session is.
        var namesPending = current.SpeakerNames.Enabled && !Has(directory, "speakers.json") && !Has(directory, "speakers.failed") && !Has(directory, "speakers.off");
        var summaryPending = current.Summary.Enabled && (!Has(directory, "summary.md") || Has(directory, "summary.stale"))
                             && !Has(directory, "summary.failed") && !Has(directory, "summary.off");
        return namesPending || summaryPending || !ReadLedger(directory).HookRan;
    }

    public void Enqueue(string sessionDirectory)
    {
        var directory = Path.GetFullPath(sessionDirectory).TrimEnd(Path.DirectorySeparatorChar);
        lock (queueLock)
        {
            if (preparing.Contains(directory)) return;
            if (!queued.Add(directory))
            {
                // Asked for while it is being worked on: go round once more after,
                // so a change made mid-pass is not lost to the pass that missed it.
                if (string.Equals(current, directory, StringComparison.OrdinalIgnoreCase)) again.Add(directory);
                return;
            }
        }
        Publish(directory, "queued", T("Queued for processing", "В очереди на обработку"), true);
        queue.Writer.TryWrite(directory);
    }

    public Task EnsureLocalModelAsync(string model, CancellationToken cancellationToken = default) =>
        models.EnsureReadyAsync(model, cancellationToken);

    public async Task<string> ImportAsync(string source, CancellationToken cancellationToken = default)
    {
        var root = settings().RecordingsDirectory;
        var started = DateTimeOffset.Now;
        var title = Path.GetFileNameWithoutExtension(source);
        var baseName = $"{started:yyyy.MM.dd-HHmm} {title}";
        var directory = Path.Combine(root, baseName);
        for (var suffix = 2; Directory.Exists(directory); suffix++) directory = Path.Combine(root, $"{baseName}-{suffix}");
        // Copied under a hidden name and renamed once whole, so a rescan never
        // picks up half a file.
        var staging = Path.Combine(root, "." + Path.GetFileName(directory));
        Directory.CreateDirectory(staging);
        var destination = Path.Combine(staging, "source" + Path.GetExtension(source).ToLowerInvariant());
        await using (var input = File.OpenRead(source))
        await using (var output = File.Create(destination))
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        var duration = AudioPreprocessor.DurationSeconds(destination);
        await AtomicFiles.WriteJsonAsync(Path.Combine(staging, "meta.json"), new
        {
            started,
            ended = started.AddSeconds(duration),
            duration_seconds = duration,
            title,
            trigger = "import",
            stop_reason = "imported",
            platform = "windows",
            files = new { source = Path.GetFileName(destination) },
        }, cancellationToken).ConfigureAwait(false);
        Directory.Move(staging, directory);
        Enqueue(directory);
        SessionsChanged?.Invoke(this, EventArgs.Empty);
        return directory;
    }

    /// <summary>
    /// "Finish": clears what gave up or is waiting and tries again, keeping the
    /// transcript and every name somebody typed.
    /// </summary>
    public void Finish(string directory)
    {
        directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        if (!TryUseIdleSession(directory, () =>
        {
            foreach (var marker in new[] { "transcribe.failed", "transcribe.deferred", "speakers.failed", "speakers.deferred", "speakers.off",
                     "summary.failed", "summary.deferred", "summary.off" })
                File.Delete(Path.Combine(directory, marker));
            WriteLedger(directory, ReadLedger(directory) with { TranscribeAttempts = 0, NamesAttempts = 0, SummaryAttempts = 0, LastError = null });
        })) return;
        Enqueue(directory);
    }

    /// <summary>
    /// Transcribes a session again, optionally with a particular engine, which is
    /// remembered by the session so a queue draining in the background never
    /// substitutes its own. Provider caches go, or a corrected language would
    /// return the old text. The existing transcript and names remain available
    /// until the replacement succeeds; completed versions are archived separately.
    /// </summary>
    public async Task RetranscribeAsync(string directory, string? engine, CancellationToken cancellationToken = default)
    {
        directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        lock (queueLock)
        {
            if (queued.Contains(directory) || !preparing.Add(directory))
                throw new InvalidOperationException(T(
                    "This recording is being processed right now; try again when it finishes.",
                    "Эта запись сейчас обрабатывается — попробуйте, когда закончится."));
        }
        try
        {
            var audio = await LoadAudioAsync(directory, cancellationToken).ConfigureAwait(false);
            if (!audio.HasAudio)
                throw new InvalidOperationException(T(
                    "This session's audio was not kept, so there is nothing to transcribe again.",
                    "Звук этой записи не сохранён, расшифровывать заново нечего."));
            ProviderCache.Clear(directory);
            OfflineEchoAudio.Clear(directory);
            LevelAudio.Clear(directory);
            foreach (var file in new[] { "transcribe.failed", "transcribe.deferred", "transcribe.off",
                         "speakers.failed", "speakers.deferred", "summary.failed", "summary.deferred" })
                File.Delete(Path.Combine(directory, file));
            engine ??= settings().Transcription.Engine;
            var choice = Path.Combine(directory, "transcribe.engine");
            await AtomicFiles.WriteTextAsync(choice, engine, cancellationToken).ConfigureAwait(false);
            // on_stop has had its run for this session; a new transcript does not earn another.
            WriteLedger(directory, new ProcessingLedger(HookRan: ReadLedger(directory).HookRan));
            await AtomicFiles.WriteTextAsync(Path.Combine(directory, TranscriptVersions.RequestFile), engine, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (queueLock) preparing.Remove(directory);
        }
        Enqueue(directory);
    }

    public async Task SetSpeakerNameAsync(string directory, string label, string name, CancellationToken cancellationToken = default, string? title = null)
    {
        var transcript = await ReadTranscriptAsync(directory, cancellationToken).ConfigureAwait(false);
        var file = await SpeakerFile.ReadAsync(directory, cancellationToken).ConfigureAwait(false);
        var names = new Dictionary<string, string>(file.Names);
        var sources = new Dictionary<string, string>(file.Sources);
        if (string.IsNullOrWhiteSpace(name))
        {
            names.Remove(label);
            sources.Remove(label);
        }
        else
        {
            names[label] = name.Trim();
            sources[label] = "manual";
        }
        await SpeakerFile.WriteAsync(directory, names, sources, cancellationToken).ConfigureAwait(false);
        await TranscriptWriter.WriteAsync(directory, title ?? await ReadTitleAsync(directory, cancellationToken), transcript, names, cancellationToken)
            .ConfigureAwait(false);
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var directory in queue.Reader.ReadAllAsync(cancellationToken))
            {
                lock (queueLock) current = directory;
                try
                {
                    await ProcessAsync(directory, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    // Nothing one session does may stop the queue for the others:
                    // it is written down, and the session waits for the next pass.
                    try
                    {
                        await File.AppendAllTextAsync(Path.Combine(directory, "transcribe.log"),
                            $"{DateTimeOffset.UtcNow:O} {exception}{Environment.NewLine}", CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception logging) when (logging is IOException or UnauthorizedAccessException) { }
                    Publish(directory, "deferred", exception.Message, false);
                    ScheduleRetry(directory, TimeSpan.FromMinutes(10));
                }
                finally
                {
                    bool repeat;
                    lock (queueLock)
                    {
                        queued.Remove(directory);
                        current = null;
                        repeat = again.Remove(directory);
                    }
                    SessionsChanged?.Invoke(this, EventArgs.Empty);
                    if (repeat) Enqueue(directory);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessAsync(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory) || !File.Exists(Path.Combine(directory, "meta.json"))) return;
        if (!configReadable())
        {
            Publish(directory, "deferred", T("Waiting for config.json to be fixed", "Жду, пока исправят config.json"), false);
            return;
        }

        var current = settings();
        var title = await ReadTitleAsync(directory, cancellationToken).ConfigureAwait(false);
        var ledger = ReadLedger(directory);

        if (!Has(directory, "transcript.json") || Has(directory, TranscriptVersions.RequestFile))
        {
            if (Has(directory, "transcribe.failed")) return;
            if (!current.Transcription.Enabled && !Has(directory, TranscriptVersions.RequestFile))
            {
                await AtomicFiles.WriteTextAsync(Path.Combine(directory, "transcribe.off"), "disabled", cancellationToken).ConfigureAwait(false);
                await RunHookOnceAsync(directory, cancellationToken).ConfigureAwait(false);
                Publish(directory, "complete", T("Recording saved (transcription is off)", "Запись сохранена (расшифровка выключена)"), false);
                return;
            }
            var transcribed = await TranscribeAsync(directory, title, ledger, cancellationToken).ConfigureAwait(false);
            if (!transcribed) return;
            ledger = ReadLedger(directory);
        }

        var transcript = await ReadTranscriptAsync(directory, cancellationToken).ConfigureAwait(false);
        var namesSettled = await NameSpeakersAsync(directory, title, transcript, cancellationToken).ConfigureAwait(false);
        var summarySettled = await SummarizeAsync(directory, cancellationToken).ConfigureAwait(false);
        var requestsSettled = await SummarizeOnRequestAsync(directory, cancellationToken).ConfigureAwait(false);
        if (!namesSettled || !summarySettled || !requestsSettled) return;

        await RunHookOnceAsync(directory, cancellationToken).ConfigureAwait(false);
        Publish(directory, "complete", T($"Finished {title}", $"Готово: {title}"), false);
    }

    /// <returns>Whether there is now a transcript.</returns>
    private async Task<bool> TranscribeAsync(string directory, string title, ProcessingLedger ledger, CancellationToken cancellationToken)
    {
        var current = settings();
        Publish(directory, "transcribing", T("Reading the audio…", "Читаю звук…"), true, PassProgress.Start);
        var total = Stopwatch.StartNew();
        var decode = Stopwatch.StartNew();
        var audio = await LoadAudioAsync(directory, cancellationToken).ConfigureAwait(false);
        LogTiming(directory, "decode", decode.Elapsed);
        if (!audio.HasAudio)
        {
            await GiveUpAsync(directory, T("No track in this recording has any audio.", "Ни на одной дорожке записи нет звука."), cancellationToken).ConfigureAwait(false);
            return false;
        }

        var engine = ReadText(directory, "transcribe.engine") ?? current.Transcription.Engine;
        var plan = EngineResolver.Plan(engine, current.Transcription.Cloud, current.Transcription.LocalEngine,
            provider => !string.IsNullOrWhiteSpace(secrets.Get(provider)), models.IsReady);
        if (!plan.Usable)
        {
            await DeferAsync(directory, "transcribe.deferred", plan.Missing!, cancellationToken).ConfigureAwait(false);
            return false;
        }

        // Recognition hears an echo-cleaned copy of the mic; the archive and
        // keep_audio below still settle the original tracks in `audio`.
        var overlap = OverlapFor(plan, audio, current, directory);
        var echoing = current.OfflineEchoCancellation && audio.BothSides;
        var leveling = string.Equals(current.Transcription.LevelNormalization, "auto", StringComparison.OrdinalIgnoreCase);
        if (echoing) Publish(directory, "transcribing", T("Removing echo…", "Убираю эхо…"), true, PassProgress.Echo);
        var (echoed, cleaned) = await HearAsync(directory, audio, current, overlap, cancellationToken).ConfigureAwait(false);
        LevelAudio.Result? leveled = null;

        try
        {
            if (leveling) Publish(directory, "transcribing", T("Evening out speech levels…", "Выравниваю громкость…"), true, PassProgress.Levels);
            (var heard, leveled) = await LevelAsync(directory, echoed, current, cancellationToken).ConfigureAwait(false);
            var recognition = Stopwatch.StartNew();
            var start = PassProgress.RecognitionStart(echoing, leveling);
            // No number until the engine reports one: a cloud engine or a one-call
            // model never does, and its bar only shows that work goes on.
            Publish(directory, "transcribing", T("Transcribing…", "Расшифровываю…"), true);
            var transcript = await RunPlanAsync(plan, heard, directory, current, cancellationToken, cleaned is null ? null : overlap?.Result,
                share => PublishProgress(directory, "transcribing",
                    T($"Transcribing… {share:P0}", $"Расшифровываю… {share:P0}"), PassProgress.Recognition(start, share))).ConfigureAwait(false);
            LogTiming(directory, "asr", recognition.Elapsed);
            transcript = ApplyCustomWords(directory, transcript, current);
            cleaned?.Delete();
            cleaned = null;
            leveled?.Delete();
            leveled = null;
            if (current.TranscriptEchoFilter && audio.BothSides)
                transcript = transcript with { Segments = TranscriptEchoFilter.Filter(transcript.Segments) };
            transcript = await DiarizeCallAsync(directory, audio, transcript, current, cancellationToken).ConfigureAwait(false);
            if (transcript.Segments.All(segment => string.IsNullOrWhiteSpace(segment.Text)))
            {
                await GiveUpAsync(directory, T("No speech was heard in this recording.", "В записи не слышно речи."), cancellationToken).ConfigureAwait(false);
                return false;
            }
            var speakers = await SpeakerFile.ReadAsync(directory, cancellationToken).ConfigureAwait(false);
            var replacing = Has(directory, TranscriptVersions.RequestFile);
            var names = replacing
                ? speakers.Names.Where(pair => speakers.Sources.GetValueOrDefault(pair.Key) == "manual").ToDictionary()
                : speakers.Names;
            await TranscriptVersions.ArchiveCurrentAsync(directory, cancellationToken).ConfigureAwait(false);
            if (replacing && Has(directory, "summary.md"))
                await AtomicFiles.WriteTextAsync(Path.Combine(directory, "summary.stale"), "retranscribed", cancellationToken).ConfigureAwait(false);
            await TranscriptWriter.WriteAsync(directory, title, transcript, names, cancellationToken).ConfigureAwait(false);
            LogTiming(directory, "total", total.Elapsed, $"{CountWords(transcript)} words");
            if (replacing)
            {
                if (names.Count == 0) File.Delete(Path.Combine(directory, "speakers.json"));
                else await SpeakerFile.WriteAsync(directory, names, names.ToDictionary(pair => pair.Key, _ => "manual"), cancellationToken).ConfigureAwait(false);
                foreach (var marker in new[] { TranscriptVersions.RequestFile, "speakers.off", "summary.off" })
                    File.Delete(Path.Combine(directory, marker));
            }
            File.Delete(Path.Combine(directory, "transcribe.deferred"));
            SessionsChanged?.Invoke(this, EventArgs.Empty);
            WriteLedger(directory, ReadLedger(directory) with { LastError = null });
            _ = analytics.RecordAsync("transcript_finished", new Dictionary<string, object?>
            {
                ["engine"] = transcript.Engine,
                ["model"] = KnownModel(transcript.Engine, transcript.Model),
            });
            try
            {
                await SettleAudioAsync(audio, current.KeepAudio, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The transcript is safe; the raw tracks stay rather than be lost to a failed archive.
                await File.AppendAllTextAsync(Path.Combine(directory, "transcribe.log"),
                    $"{DateTimeOffset.UtcNow:O} archive: {exception.Message}{Environment.NewLine}", cancellationToken).ConfigureAwait(false);
            }
            return true;
        }
        catch (ProcessingFailure failure)
        {
            await File.AppendAllTextAsync(Path.Combine(directory, "transcribe.log"),
                $"{DateTimeOffset.UtcNow:O} {failure.Kind}: {failure.Message}{Environment.NewLine}", cancellationToken).ConfigureAwait(false);
            if (failure.Kind != FailureKind.Recording)
            {
                await DeferAsync(directory, "transcribe.deferred", failure.Message, cancellationToken).ConfigureAwait(false);
                _ = analytics.RecordAsync("transcript_failed", new Dictionary<string, object?>
                {
                    ["engine"] = engine, ["reason"] = Reason(failure), ["outcome"] = "deferred",
                });
                if (failure.Kind == FailureKind.Transient) ScheduleRetry(directory, TimeSpan.FromMinutes(2));
                return false;
            }
            var attempts = ledger.TranscribeAttempts + 1;
            WriteLedger(directory, ReadLedger(directory) with { TranscribeAttempts = attempts, LastError = failure.Message });
            _ = analytics.RecordAsync("transcript_failed", new Dictionary<string, object?>
            {
                ["engine"] = engine, ["reason"] = Reason(failure), ["outcome"] = attempts >= TranscribeAttemptLimit ? "gave_up" : "deferred",
            });
            if (attempts >= TranscribeAttemptLimit)
            {
                await GiveUpAsync(directory, failure.Message, cancellationToken).ConfigureAwait(false);
                return false;
            }
            await DeferAsync(directory, "transcribe.deferred", failure.Message, cancellationToken).ConfigureAwait(false);
            ScheduleRetry(directory, TimeSpan.FromSeconds(30));
            return false;
        }
        finally
        {
            // A retry recomputes the cleaned audio; only provider caches stay for it.
            cleaned?.RemoveAudio();
            leveled?.RemoveAudio();
        }
    }

    /// <summary>
    /// The tracks recognition hears after speech level normalization (roadmap-speed
    /// step 4), when it is on and some track needs it; `heard` itself otherwise.
    /// Like echo removal, it improves a transcript and is not what makes one: a
    /// failure leaves the tracks as they were, and processing.json says why.
    /// </summary>
    private async Task<(SessionAudio Heard, LevelAudio.Result? Leveled)> LevelAsync(
        string directory, SessionAudio heard, AppSettings current, CancellationToken cancellationToken)
    {
        if (!string.Equals(current.Transcription.LevelNormalization, "auto", StringComparison.OrdinalIgnoreCase))
        {
            WriteLedger(directory, ReadLedger(directory) with { AudioLevelNormalization = null });
            return (heard, null);
        }
        var log = Path.Combine(directory, "transcribe.log");
        var timer = Stopwatch.StartNew();
        LevelAudio.Result result;
        try
        {
            result = await Task.Run(() => LevelAudio.Prepare(directory, heard, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await File.AppendAllTextAsync(log, $"{DateTimeOffset.UtcNow:O} level normalization skipped — {exception.Message}; transcribing the tracks as they were{Environment.NewLine}", cancellationToken).ConfigureAwait(false);
            WriteLedger(directory, ReadLedger(directory) with
            {
                AudioLevelNormalization = new LevelNormalizationRecord(SpeechLevels.Version, "auto", [], Skipped: exception.Message),
            });
            return (heard, null);
        }
        WriteLedger(directory, ReadLedger(directory) with { AudioLevelNormalization = result.Record });
        var tracks = string.Join("; ", result.Record.Tracks.Select(track => track.Skipped is { } why
            ? FormattableString.Invariant($"{track.Side} speech {track.SpeechDb:0.0} dBFS left as is ({why})")
            : FormattableString.Invariant($"{track.Side} speech {track.SpeechDb:0.0} -> {track.SpeechDbAfter:0.0} dBFS, gain {track.MinGainDb:+0.0;-0.0}..{track.MaxGainDb:+0.0;-0.0} dB")));
        await File.AppendAllTextAsync(log, $"{DateTimeOffset.UtcNow:O} level normalization: {tracks}; original tracks kept{Environment.NewLine}", cancellationToken).ConfigureAwait(false);
        LogTiming(directory, "levels", timer.Elapsed, result.Changed ? null : "no change");
        if (!result.Changed)
        {
            result.Delete();
            return (heard, null);
        }
        return (result.Audio, result);
    }

    /// <summary>
    /// The tracks recognition hears: the echo-cleaned mic copy when there is
    /// one, the tracks as recorded otherwise. `audio` itself is never changed.
    /// </summary>
    private async Task<(SessionAudio Heard, OfflineEchoAudio.Result? Cleaned)> HearAsync(
        string directory, SessionAudio audio, AppSettings current, SystemOverlap? overlap, CancellationToken cancellationToken)
    {
        if (!current.OfflineEchoCancellation || !audio.BothSides)
        {
            WriteLedger(directory, ReadLedger(directory) with { AudioEchoCancellation = null });
            return (audio, null);
        }
        var echo = Stopwatch.StartNew();
        var cleaned = await CancelEchoAsync(directory, audio, overlap, cancellationToken).ConfigureAwait(false);
        LogTiming(directory, "echo", echo.Elapsed, cleaned is null ? "skipped" : null);
        if (cleaned is null) return (audio, null);
        return (audio with
        {
            Directory = cleaned.Directory,
            Microphone = cleaned.Microphone,
            System = cleaned.System,
            MicrophoneOffsetMs = cleaned.OffsetMs,
            SystemOffsetMs = cleaned.OffsetMs,
        }, cleaned);
    }

    /// <summary>
    /// Measurement only (roadmap-speed step 0): transcribes a copy of one
    /// recording in `output` on this computer's model, with the same stages
    /// and timing lines as a real pass. The recording itself is only read:
    /// no archive, no names, no summary, no analytics. The cloud is used only
    /// when `engine` names a cloud engine outright, so a cloud engine can be
    /// measured on the same audio path; nothing else ever reaches it.
    /// </summary>
    public async Task<TranscriptDocument> MeasureAsync(string recording, string output, string? engine, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(output);
        foreach (var path in Directory.EnumerateFiles(recording))
        {
            var name = Path.GetFileName(path);
            if (name is "meta.json" or "transcribe.engine" || (!name.StartsWith('.') && IsMedia(path)))
                File.Copy(path, Path.Combine(output, name), overwrite: true);
        }
        var current = settings();
        var total = Stopwatch.StartNew();
        var decode = Stopwatch.StartNew();
        var audio = await LoadAudioAsync(output, cancellationToken).ConfigureAwait(false);
        LogTiming(output, "decode", decode.Elapsed);
        if (!audio.HasAudio) throw new InvalidOperationException("No track in this recording has any audio.");
        engine ??= ReadText(output, "transcribe.engine") ?? current.Transcription.Engine;
        var namedCloud = TranscriptionSettings.CloudEngines.Contains(engine);
        var plan = EngineResolver.Plan(engine, current.Transcription.Cloud, current.Transcription.LocalEngine,
            name => namedCloud && secrets.Get(name) is not null, models.IsReady);
        if (namedCloud && plan.Cloud is null) throw new InvalidOperationException($"No key for '{engine}': {plan.Missing}");
        if (!namedCloud && plan.Local is null) throw new InvalidOperationException($"No local model for '{engine}': {plan.Missing}");
        var measurePlan = namedCloud ? plan : plan with { Cloud = null };
        var overlap = OverlapFor(measurePlan, audio, current, output);
        var (echoed, cleaned) = await HearAsync(output, audio, current, overlap, cancellationToken).ConfigureAwait(false);
        LevelAudio.Result? leveled = null;
        try
        {
            (var heard, leveled) = await LevelAsync(output, echoed, current, cancellationToken).ConfigureAwait(false);
            var recognition = Stopwatch.StartNew();
            var transcript = await RunPlanAsync(measurePlan, heard, output, current, cancellationToken, cleaned is null ? null : overlap?.Result).ConfigureAwait(false);
            LogTiming(output, "asr", recognition.Elapsed);
            transcript = ApplyCustomWords(output, transcript, current);
            if (current.TranscriptEchoFilter && audio.BothSides)
                transcript = transcript with { Segments = TranscriptEchoFilter.Filter(transcript.Segments) };
            await TranscriptWriter.WriteAsync(output, audio.Title, transcript, null, cancellationToken).ConfigureAwait(false);
            LogTiming(output, "total", total.Elapsed, $"{CountWords(transcript)} words");
            return transcript;
        }
        finally
        {
            cleaned?.Delete();
            leveled?.Delete();
        }
    }

    private static int CountWords(TranscriptDocument transcript) =>
        transcript.Segments.Sum(segment => segment.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);

    /// <summary>
    /// One "timing" line in transcribe.log, with a dot for decimals whatever the
    /// culture, so Measure-Transcription.ps1 can read it back.
    /// </summary>
    internal static void LogTiming(string directory, string stage, TimeSpan elapsed, string? detail = null)
    {
        try
        {
            File.AppendAllText(Path.Combine(directory, "transcribe.log"), string.Create(CultureInfo.InvariantCulture,
                $"{DateTimeOffset.UtcNow:O} timing {stage} {elapsed.TotalSeconds:0.0} s{(detail is null ? "" : $" ({detail})")}{Environment.NewLine}"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// An echo-cleaned copy of the mic, or null when that could not be made.
    /// Echo cancellation improves a transcript; it is not what makes one: a
    /// missing DLL or model, a failed checksum, a native error or a short copy
    /// leaves the tracks to be transcribed as recorded, and transcribe.log and
    /// processing.json say what was skipped and why. Shares the CPU with local
    /// recognition, so live transcription preempts it like a batch pass.
    /// </summary>
    private async Task<OfflineEchoAudio.Result?> CancelEchoAsync(string directory, SessionAudio audio, SystemOverlap? overlap, CancellationToken cancellationToken)
    {
        var log = Path.Combine(directory, "transcribe.log");
        await File.AppendAllTextAsync(log, $"{DateTimeOffset.UtcNow:O} removing acoustic echo from a microphone copy before transcription{Environment.NewLine}", cancellationToken).ConfigureAwait(false);
        var timer = Stopwatch.StartNew();
        OfflineEchoAudio.Result prepared;
        try
        {
            while (true)
            {
                using var cpu = await models.CpuTranscription.EnterBatchAsync(cancellationToken).ConfigureAwait(false);
                // The call track is not changed by echo removal: hear it now, under this lease.
                var side = overlap?.RunAsync(cpu.Token);
                try
                {
                    prepared = await Task.Run(() => OfflineEchoAudio.Prepare(audio, threads =>
                    {
                        var assets = LocalVqeAssets.Resolve();
                        return new LocalVqeBackend(assets.Library, assets.Model, threads);
                    }, cpu.Token), cpu.Token).ConfigureAwait(false);
                    break;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Live preempted this pass; the model starts again from t=0 once it is done.
                }
                finally
                {
                    // Never leave the lease with a recognizer still running; a failed
                    // overlap only means that track runs later as usual.
                    if (side is not null)
                    {
                        try { await side.ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await File.AppendAllTextAsync(log, $"{DateTimeOffset.UtcNow:O} echo cancellation skipped — {exception.Message}; transcribing the tracks as recorded{Environment.NewLine}", cancellationToken).ConfigureAwait(false);
            WriteLedger(directory, ReadLedger(directory) with
            {
                AudioEchoCancellation = new EchoCancellationRecord(LocalVqeAssets.ProcessorVersion, Skipped: exception.Message),
            });
            return null;
        }
        await File.AppendAllTextAsync(log, $"{DateTimeOffset.UtcNow:O} echo stages: {prepared.Timings}{Environment.NewLine}", cancellationToken).ConfigureAwait(false);
        WriteLedger(directory, ReadLedger(directory) with
        {
            AudioEchoCancellation = new EchoCancellationRecord(LocalVqeAssets.ProcessorVersion,
                prepared.ModelRan ? LocalVqeAssets.ModelSha256 : null, EchoCanceller.SampleRate, prepared.Frames,
                Mode: prepared.ModelRan ? "localvqe" : "silent_reference_passthrough",
                DelayPrime: prepared.DelayPrime?.Describe()),
        });
        if (prepared.DelayPrime is { Applied: false } prime)
            await File.AppendAllTextAsync(log, $"{DateTimeOffset.UtcNow:O} echo delay prime unavailable ({prime.Status}); LocalVQE estimated the delay online{Environment.NewLine}", cancellationToken).ConfigureAwait(false);
        var how = !prepared.ModelRan ? "no playback, mic kept exact"
            : prepared.DelayPrime is { ShiftSamples: { } shift } ? $"LocalVQE, echo delay primed at {shift} samples"
            : "LocalVQE";
        await File.AppendAllTextAsync(log, $"{DateTimeOffset.UtcNow:O} audio echo cancellation complete in {timer.Elapsed.TotalSeconds:0.0} s " +
            $"({how}); original tracks kept{Environment.NewLine}", cancellationToken).ConfigureAwait(false);
        return prepared;
    }

    /// <summary>
    /// Call-track recognition to run during echo removal (roadmap-speed step 3), or
    /// null when it cannot: a cloud plan, no echo removal, level normalization (it
    /// would change the track), a replaced engine, or AMANU_ASR_OVERLAP not set to 1 (off by default).
    /// </summary>
    private SystemOverlap? OverlapFor(EnginePlan plan, SessionAudio audio, AppSettings current, string directory)
    {
        if (plan.Cloud is not null || plan.Local is null || !audio.BothSides || !current.OfflineEchoCancellation) return null;
        if (string.Equals(current.Transcription.LevelNormalization, "auto", StringComparison.OrdinalIgnoreCase)) return null;
        if (!SystemOverlap.Enabled(Environment.GetEnvironmentVariable("AMANU_ASR_OVERLAP"))) return null;
        if (CreateLocal(plan.Local, current) is not LocalTranscriptionEngine engine) return null;
        var threads = SystemOverlap.OverlapThreads(Environment.GetEnvironmentVariable("AMANU_ASR_OVERLAP_THREADS"), LocalTranscriptionEngine.Threads);
        return new SystemOverlap(engine, audio, threads, (stage, elapsed, detail) => LogTiming(directory, stage, elapsed, detail));
    }

    /// <summary>
    /// The cloud first when the plan has one, and this computer's model only when
    /// the cloud could not be reached or was busy — for this session alone.
    /// </summary>
    private async Task<TranscriptDocument> RunPlanAsync(EnginePlan plan, SessionAudio audio, string directory, AppSettings current, CancellationToken cancellationToken,
        IReadOnlyList<TranscriptSegment>? system = null, Action<double>? progress = null)
    {
        if (plan.Cloud is { } cloud)
        {
            try
            {
                var done = await CreateCloud(cloud, current, cache: true).TranscribeAsync(audio, cancellationToken).ConfigureAwait(false);
                // Gemini may have been answered by its fallback model, and the transcript's quality depends on which; the log says so.
                if (cloud == "gemini")
                    await File.AppendAllTextAsync(Path.Combine(directory, "transcribe.log"),
                        $"{DateTimeOffset.UtcNow:O} gemini model: {done.Model}{Environment.NewLine}", cancellationToken).ConfigureAwait(false);
                return done;
            }
            catch (ProcessingFailure failure) when (plan.Local is not null && failure.Kind != FailureKind.Recording)
            {
                _ = analytics.RecordAsync("transcript_fallback", new Dictionary<string, object?>
                {
                    ["from_engine"] = cloud, ["to_engine"] = plan.Local, ["reason"] = Reason(failure),
                });
                await File.AppendAllTextAsync(Path.Combine(directory, "transcribe.log"),
                    $"{DateTimeOffset.UtcNow:O} {cloud} → {plan.Local}: {failure.Message}{Environment.NewLine}", cancellationToken).ConfigureAwait(false);
            }
        }
        var local = CreateLocal(plan.Local!, current);
        if (local is LocalTranscriptionEngine timed)
        {
            timed.Timing = (stage, elapsed, detail) => LogTiming(directory, stage, elapsed, detail);
            timed.PrecomputedSystem = system;
            timed.Progress = progress;
        }
        return await local.TranscribeAsync(audio, cancellationToken).ConfigureAwait(false);
    }

    public ITranscriptionEngine CreateCloud(string provider, AppSettings current, bool cache)
    {
        var key = secrets.Get(provider) ?? throw new ProcessingFailure(FailureKind.Environmental,
            T($"{EngineResolver.DisplayName(provider)} needs a key.", $"Для {EngineResolver.DisplayName(provider)} нужен ключ."));
        var expected = MeetingLanguages.Expected(current.Transcription.Language);
        return provider switch
        {
            "openai" => new OpenAiTranscriptionEngine(httpClient, key, current.Transcription.OpenAi.Model, MeetingLanguages.Pin(expected), cache),
            "elevenlabs" => new ElevenLabsTranscriptionEngine(httpClient, key, cache, LoadCustomWords(current)),
            "gemini" => new GeminiTranscriptionEngine(httpClient, key, expected, cache, LoadCustomWords(current)),
            _ => new AssemblyAiTranscriptionEngine(httpClient, key, expected, current.Transcription.AssemblyAi.SpeechModel, cache, LoadCustomWords(current)),
        };
    }

    /// <summary>
    /// A local engine hears the call track as one voice; this splits it into
    /// "them A", "them B". Cloud engines label voices themselves and are left
    /// alone. A failure here costs only the split: the transcript goes on with
    /// "them", and the reason is in transcribe.log.
    /// </summary>
    private async Task<TranscriptDocument> DiarizeCallAsync(
        string directory, SessionAudio audio, TranscriptDocument transcript, AppSettings current, CancellationToken cancellationToken)
    {
        if (!string.Equals(current.Transcription.Diarization, "auto", StringComparison.OrdinalIgnoreCase)) return transcript;
        if (!TranscriptionSettings.LocalEngines.Contains(transcript.Engine, StringComparer.OrdinalIgnoreCase)) return transcript;
        if (audio.System is null || !transcript.Segments.Any(segment => segment.Speaker == SpeakerLabels.Them)) return transcript;
        var threshold = current.Transcription.DiarizationThreshold ?? TranscriptionSettings.DefaultDiarizationThreshold;
        var clock = Stopwatch.StartNew();
        try
        {
            Publish(directory, "transcribing", T("Telling call voices apart…", "Различаю голоса в звонке…"), true);
            var turns = await CallDiarizer.RunAsync(models, audio.System, audio.SystemOffsetMs, threshold, cancellationToken).ConfigureAwait(false);
            var result = CallDiarization.Apply(transcript.Segments, turns);
            LogTiming(directory, "diarization", clock.Elapsed,
                string.Create(CultureInfo.InvariantCulture, $"{result.Speakers} speakers, threshold {threshold}"));
            WriteLedger(directory, ReadLedger(directory) with { CallSpeakers = result.Speakers });
            return transcript with { Segments = result.Segments };
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogTiming(directory, "diarization", clock.Elapsed, $"failed: {exception.Message}");
            return transcript;
        }
    }

    private static string DataDirectory()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataDirectory = Path.Combine(localData, "Amanu Data");
#if DEBUG
        if (Environment.GetEnvironmentVariable("AMANU_TEST_DATA") is { Length: > 0 } testDirectory)
            dataDirectory = Path.GetFullPath(testDirectory);
#endif
        return dataDirectory;
    }

    internal static string PeoplePath(AppSettings current) => PeopleList.PathFor(current.SpeakerNames.PeopleFile, DataDirectory());

    /// <summary>The user's people list (empty when there is none): read at each use, so an edit needs no restart.</summary>
    internal static IReadOnlyList<Person> LoadPeople(AppSettings current) => PeopleList.Load(PeoplePath(current));

    /// <summary>
    /// The user's word list and the full names from the people list (empty when
    /// there is neither): read at each use, so an edit needs no restart. The
    /// names come after the words, so a full list keeps the user's own entries.
    /// </summary>
    internal static IReadOnlyList<string> LoadCustomWords(AppSettings current)
    {
        var words = CustomWords.Load(CustomWords.DefaultPath(DataDirectory()));
        var names = PeopleList.FullNames(LoadPeople(current));
        return names.Count == 0 ? words : CustomWords.Parse(words.Concat(names));
    }

    /// <summary>
    /// Engines that take the list (AssemblyAI, ElevenLabs, Gemini) were given it. For the
    /// rest (local models, OpenAI's diarizing model) close-sounding words are
    /// replaced by list entries. The raw text is kept and every change is
    /// logged in processing.json. An empty list changes nothing and writes nothing.
    /// </summary>
    private static TranscriptDocument ApplyCustomWords(string directory, TranscriptDocument transcript, AppSettings current)
    {
        if (transcript.Engine is "assemblyai" or "elevenlabs" or "gemini") return transcript;
        var words = LoadCustomWords(current);
        if (words.Count == 0) return transcript;
        var (segments, corrections) = CustomWords.Correct(transcript.Segments, words);
        if (corrections.Count == 0) return transcript;
        File.WriteAllText(Path.Combine(directory, "transcript.raw.json"), JsonSerializer.Serialize(transcript));
        WriteLedger(directory, ReadLedger(directory) with { CustomWordCorrections = corrections });
        return transcript with { Segments = segments };
    }

    public ITranscriptionEngine CreateLocal(string model, AppSettings current) =>
        localEngineFactory?.Invoke(model, current)
        ?? new LocalTranscriptionEngine(models, model, MeetingLanguages.Pin(MeetingLanguages.Expected(current.Transcription.Language)));

    /// <returns>Whether naming is done, off, or given up on — anything but waiting.</returns>
    private async Task<bool> NameSpeakersAsync(string directory, string title, TranscriptDocument transcript, CancellationToken cancellationToken)
    {
        var current = settings();
        if (!current.SpeakerNames.Enabled)
        {
            await AtomicFiles.WriteTextAsync(Path.Combine(directory, "speakers.off"), "disabled", cancellationToken).ConfigureAwait(false);
            return true;
        }
        if (Has(directory, "speakers.failed") || Has(directory, "speakers.off")) return true;
        if (Has(directory, "speakers.json") && !Has(directory, "speakers.deferred"))
        {
            // Asked once and answered: labels the model could not name stay
            // unnamed rather than send the transcript again at every pass.
            if (ReadLedger(directory).NamesAsked) return true;
            var labels = transcript.Segments.Select(segment => segment.Speaker).OfType<string>().Distinct();
            var known = (await SpeakerFile.ReadAsync(directory, cancellationToken).ConfigureAwait(false)).Names;
            if (labels.All(known.ContainsKey) || MeetingEgress.Route(EgressPurpose.SpeakerNames, current) is null) return true;
        }
        Publish(directory, "speakers", T("Working out who spoke…", "Определяю участников…"), true, PassProgress.SpeakerNames);
        try
        {
            var result = await new SpeakerNamingService(languageModels, settings).ResolveAsync(directory, transcript, cancellationToken).ConfigureAwait(false);
            await TranscriptWriter.WriteAsync(directory, title, transcript, result.Names, cancellationToken).ConfigureAwait(false);
            File.Delete(Path.Combine(directory, "speakers.deferred"));
            if (result.AskedModel) WriteLedger(directory, ReadLedger(directory) with { NamesAsked = true });
            if (result.AskedModel)
                _ = analytics.RecordAsync("speaker_names_finished", new Dictionary<string, object?> { ["backend"] = current.SpeakerNames.Backend });
            return true;
        }
        catch (ProcessingFailure failure)
        {
            return await LanguageModelFailedAsync(directory, "speakers", failure,
                ledger => ledger.NamesAttempts, (ledger, attempts) => ledger with { NamesAttempts = attempts },
                "speaker_names_failed", current.SpeakerNames.Backend, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <returns>Whether the summary is done, off, or given up on.</returns>
    private async Task<bool> SummarizeAsync(string directory, CancellationToken cancellationToken)
    {
        var current = settings();
        if (MeetingEgress.Route(EgressPurpose.Summary, current) is null)
        {
            if (!Has(directory, "summary.md"))
                await AtomicFiles.WriteTextAsync(Path.Combine(directory, "summary.off"), "disabled", cancellationToken).ConfigureAwait(false);
            return true;
        }
        if (Has(directory, "summary.failed") || Has(directory, "summary.off")) return true;
        if (Has(directory, "summary.md") && !Has(directory, "summary.stale")) return true;
        Publish(directory, "summary", T("Writing the summary…", "Пишу саммари…"), true, PassProgress.Summary);
        try
        {
            var markdown = await File.ReadAllTextAsync(Path.Combine(directory, "transcript.md"), cancellationToken).ConfigureAwait(false);
            var answer = await new SummaryService(languageModels, settings).GenerateAsync(directory, markdown, cancellationToken).ConfigureAwait(false);
            File.Delete(Path.Combine(directory, "summary.deferred"));
            _ = analytics.RecordAsync("summary_finished", new Dictionary<string, object?>
            {
                ["backend"] = answer.Backend, ["model"] = answer.Model,
            });
            SummaryWritten?.Invoke(this, directory);
            return true;
        }
        catch (ProcessingFailure failure)
        {
            return await LanguageModelFailedAsync(directory, "summary", failure,
                ledger => ledger.SummaryAttempts, (ledger, attempts) => ledger with { SummaryAttempts = attempts },
                "summary_failed", current.Summary.Backend, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// "Summarize again": a summary of one transcript version with one engine,
    /// written to summarize.requested first so that a busy queue or a restart
    /// does not lose it.
    /// </summary>
    public void RequestSummary(string directory, string backend, string? transcript)
    {
        directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        SummaryVersions.Request(directory, new SummaryRequest(backend, transcript));
        Enqueue(directory);
    }

    /// <returns>Whether every request is done or dropped; false while one waits for a model.</returns>
    private async Task<bool> SummarizeOnRequestAsync(string directory, CancellationToken cancellationToken)
    {
        while (SummaryVersions.ReadRequests(directory) is [var request, ..])
        {
            var target = SummaryVersions.Target(directory, request);
            var transcriptPath = Path.Combine(target, "transcript.md");
            if (!File.Exists(transcriptPath))
            {
                SummaryVersions.Remove(directory, request);
                continue;
            }
            Publish(directory, "summary", T($"Writing the summary with {SummaryVersions.BackendName(request.Backend)}…",
                $"Пишу саммари через {SummaryVersions.BackendName(request.Backend)}…"), true, PassProgress.Summary);
            try
            {
                var markdown = await File.ReadAllTextAsync(transcriptPath, cancellationToken).ConfigureAwait(false);
                var (chosenModels, chosenSettings) = languageModels.WithSummaryBackend(request.Backend);
                var answer = await new SummaryService(chosenModels, chosenSettings).GenerateAsync(target, markdown, cancellationToken).ConfigureAwait(false);
                _ = analytics.RecordAsync("summary_finished", new Dictionary<string, object?>
                {
                    ["backend"] = answer.Backend, ["model"] = answer.Model,
                });
            }
            catch (ProcessingFailure failure) when (failure.Kind != FailureKind.Recording)
            {
                // Kept for the next pass: the engine may be back, or signed in, by then.
                Publish(directory, "deferred", T($"The summary waits: {failure.Message}", $"Саммари ждёт: {failure.Message}"), false);
                return false;
            }
            catch (ProcessingFailure failure)
            {
                Publish(directory, "failed", T($"The summary failed: {failure.Message}", $"Саммари не получилось: {failure.Message}"), false);
                // Dropped, so it is not tried again at every launch; the next request still runs.
                SummaryVersions.Remove(directory, request);
                SessionsChanged?.Invoke(this, EventArgs.Empty);
                continue;
            }
            SummaryVersions.Remove(directory, request);
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
        return true;
    }

    /// <summary>
    /// Waits while nothing could be reached; counts every try that reached a model
    /// and gave up after five, so a stale failure is not retried at every launch.
    /// </summary>
    private async Task<bool> LanguageModelFailedAsync(
        string directory, string prefix, ProcessingFailure failure,
        Func<ProcessingLedger, int> attemptsOf, Func<ProcessingLedger, int, ProcessingLedger> withAttempts,
        string analyticsEvent, string backend, CancellationToken cancellationToken)
    {
        var ledger = ReadLedger(directory);
        if (failure.Kind == FailureKind.Environmental)
        {
            await DeferAsync(directory, $"{prefix}.deferred", failure.Message, cancellationToken).ConfigureAwait(false);
            _ = analytics.RecordAsync(analyticsEvent, new Dictionary<string, object?> { ["backend"] = backend, ["reason"] = Reason(failure), ["outcome"] = "deferred" });
            return false;
        }
        var attempts = attemptsOf(ledger) + 1;
        WriteLedger(directory, withAttempts(ledger, attempts) with { LastError = failure.Message });
        var gaveUp = attempts >= LanguageModelAttemptLimit;
        _ = analytics.RecordAsync(analyticsEvent, new Dictionary<string, object?>
        {
            ["backend"] = backend, ["reason"] = Reason(failure), ["outcome"] = gaveUp ? "gave_up" : "deferred",
        });
        if (gaveUp)
        {
            File.Delete(Path.Combine(directory, $"{prefix}.deferred"));
            await AtomicFiles.WriteTextAsync(Path.Combine(directory, $"{prefix}.failed"), failure.Message, cancellationToken).ConfigureAwait(false);
            Publish(directory, "failed", failure.Message, false);
            return true;
        }
        await DeferAsync(directory, $"{prefix}.deferred", failure.Message, cancellationToken).ConfigureAwait(false);
        ScheduleRetry(directory, TimeSpan.FromMinutes(failure.Kind == FailureKind.Transient ? 5 : 1));
        return false;
    }

    private async Task DeferAsync(string directory, string marker, string message, CancellationToken cancellationToken)
    {
        await AtomicFiles.WriteTextAsync(Path.Combine(directory, marker), message, cancellationToken).ConfigureAwait(false);
        WriteLedger(directory, ReadLedger(directory) with { LastError = message });
        Publish(directory, "deferred", message, false);
    }

    /// <summary>
    /// The transcription is over for good. The audio is kept whatever keep_audio
    /// says — a recording that never got a transcript is all there is of that
    /// meeting — but compressed, so a failed session does not hold gigabytes.
    /// </summary>
    private async Task GiveUpAsync(string directory, string message, CancellationToken cancellationToken)
    {
        await AtomicFiles.WriteTextAsync(Path.Combine(directory, "transcribe.failed"), message, cancellationToken).ConfigureAwait(false);
        File.Delete(Path.Combine(directory, "transcribe.deferred"));
        WriteLedger(directory, ReadLedger(directory) with { LastError = message });
        var audio = await LoadAudioAsync(directory, cancellationToken).ConfigureAwait(false);
        if (audio.HasAudio)
        {
            try { await SettleAudioAsync(audio, keep: true, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The raw tracks stay; a failed compression costs space, not the meeting.
            }
        }
        Publish(directory, "failed", message, false);
    }

    private void ScheduleRetry(string directory, TimeSpan delay) => _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(delay, lifetime.Token).ConfigureAwait(false);
            Enqueue(directory);
        }
        catch (OperationCanceledException) { }
    });

    public static async Task<SessionAudio> LoadAudioAsync(string directory, CancellationToken cancellationToken)
    {
        using var meta = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "meta.json"), cancellationToken).ConfigureAwait(false));
        var root = meta.RootElement;
        var title = root.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(titleValue.GetString())
            ? titleValue.GetString()!
            : Path.GetFileName(directory);
        var mic = Path.Combine(directory, "mic.wav");
        var system = Path.Combine(directory, "system.wav");
        var single = Directory.EnumerateFiles(directory).FirstOrDefault(path =>
            Path.GetFileNameWithoutExtension(path).Equals("source", StringComparison.OrdinalIgnoreCase) && IsMedia(path));
        var archive = Path.Combine(directory, "audio.m4a");
        if (!AudioPreprocessor.HasSamples(mic) && !AudioPreprocessor.HasSamples(system) && single is null && File.Exists(archive))
        {
            // Kept audio is one stereo file, mic left and the call right, already
            // aligned: split back into its two sides, it transcribes like a fresh
            // recording. The halves are hidden and go when the audio is settled.
            mic = Path.Combine(directory, ".archive-mic.wav");
            system = Path.Combine(directory, ".archive-system.wav");
            if (!File.Exists(mic) || !File.Exists(system)) AudioPreprocessor.SplitStereo(archive, mic, system);
            return new SessionAudio(directory, title, AudioPreprocessor.HasSamples(mic) ? mic : null,
                AudioPreprocessor.HasSamples(system) ? system : null, null, 0, 0);
        }
        var micOffset = 0;
        var systemOffset = 0;
        if (root.TryGetProperty("start_offset_ms", out var offsets))
        {
            if (offsets.TryGetProperty("mic", out var value)) value.TryGetInt32(out micOffset);
            if (offsets.TryGetProperty("system", out value)) value.TryGetInt32(out systemOffset);
        }
        return new SessionAudio(directory, title,
            AudioPreprocessor.HasSamples(mic) ? mic : null,
            AudioPreprocessor.HasSamples(system) ? system : null,
            single, micOffset, systemOffset);
    }

    private static bool IsMedia(string path) => new[] { ".wav", ".m4a", ".mp3", ".flac", ".ogg", ".mp4", ".aac", ".mov", ".mkv", ".webm", ".wma" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// After a transcript: one stereo M4A — mic left, the call right — when the
    /// audio is kept, and the raw tracks gone either way.
    /// </summary>
    private static async Task SettleAudioAsync(SessionAudio audio, bool keep, CancellationToken cancellationToken)
    {
        var archive = Path.Combine(audio.Directory, "audio.m4a");
        if (keep && !File.Exists(archive) && (audio.Microphone is not null || audio.System is not null))
        {
            var temp = Path.Combine(audio.Directory, ".archive.wav");
            var partial = Path.Combine(audio.Directory, ".audio.m4a");
            try
            {
                await Task.Run(() =>
                {
                    AudioPreprocessor.CreateAlignedStereo16k(audio.Microphone, audio.System, audio.MicrophoneOffsetMs, audio.SystemOffsetMs, temp);
                    AudioPreprocessor.EncodeAac(temp, partial);
                }, cancellationToken).ConfigureAwait(false);
                File.Move(partial, archive);
            }
            finally
            {
                File.Delete(temp);
                File.Delete(partial);
            }
        }
        foreach (var name in new[] { "mic.wav", "system.wav", ".archive-mic.wav", ".archive-system.wav" }) File.Delete(Path.Combine(audio.Directory, name));
        if (!keep && audio.Single is not null) File.Delete(audio.Single);
        var live = Path.Combine(audio.Directory, ".live");
        if (Directory.Exists(live)) Directory.Delete(live, recursive: true);
    }

    private async Task RunHookOnceAsync(string directory, CancellationToken cancellationToken)
    {
        var ledger = ReadLedger(directory);
        if (ledger.HookRan || !configReadable()) return;
        WriteLedger(directory, ledger with { HookRan = true });
        if (settings().OnStop is not { } hook || string.IsNullOrWhiteSpace(hook.Executable)) return;
        try
        {
            var info = new ProcessStartInfo(hook.Executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
            foreach (var argument in hook.Arguments) info.ArgumentList.Add(argument.Replace("{session}", directory, StringComparison.Ordinal));
            // A hook may open a viewer such as Notepad. Launch it once, then
            // let the queue continue while the viewer remains open.
            using var process = Process.Start(info);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await File.AppendAllTextAsync(Path.Combine(directory, "transcribe.log"),
                $"{DateTimeOffset.UtcNow:O} on_stop: {exception.Message}{Environment.NewLine}", cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool Has(string directory, string name) => File.Exists(Path.Combine(directory, name));

    private static string? ReadText(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } text ? text : null;
    }

    private static ProcessingLedger ReadLedger(string directory)
    {
        var path = Path.Combine(directory, "processing.json");
        if (!File.Exists(path)) return new ProcessingLedger();
        try
        {
            return JsonSerializer.Deserialize<ProcessingLedger>(File.ReadAllText(path), LedgerOptions) ?? new ProcessingLedger();
        }
        catch (JsonException) { return new ProcessingLedger(); }
    }

    private static void WriteLedger(string directory, ProcessingLedger ledger)
    {
        var path = Path.Combine(directory, "processing.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(ledger, LedgerOptions));
        File.Move(temporary, path, overwrite: true);
    }

    public static ProcessingLedger Ledger(string directory) => ReadLedger(directory);

    private static readonly JsonSerializerOptions LedgerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static async Task<TranscriptDocument> ReadTranscriptAsync(string directory, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<TranscriptDocument>(await File.ReadAllTextAsync(Path.Combine(directory, "transcript.json"), cancellationToken).ConfigureAwait(false))
        ?? throw new InvalidDataException("transcript.json is empty");

    public static async Task<string> ReadTitleAsync(string directory, CancellationToken cancellationToken)
    {
        using var meta = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "meta.json"), cancellationToken).ConfigureAwait(false));
        return meta.RootElement.TryGetProperty("title", out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : Path.GetFileName(directory);
    }

    private void Publish(string directory, string stage, string message, bool busy, double? progress = null)
    {
        var status = new ProcessingStatus(directory, stage, message, busy, progress);
        statuses[directory] = status;
        StatusChanged?.Invoke(this, status);
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A step forward inside one stage. Only the status changes, so the
    /// Recordings list is not read again from disk for every piece of audio.
    /// </summary>
    private void PublishProgress(string directory, string stage, string message, double progress)
    {
        var status = new ProcessingStatus(directory, stage, message, true, progress);
        statuses[directory] = status;
        StatusChanged?.Invoke(this, status);
    }

    private static string KnownModel(string engine, string model) => (engine, model) switch
    {
        ("assemblyai", _) => "universal-3-pro",
        ("openai", "gpt-4o-transcribe-diarize") => "gpt-4o-transcribe-diarize",
        ("elevenlabs", _) => "scribe_v2",
        ("gemini", var used) when used.StartsWith("gemini-3.7", StringComparison.Ordinal) => "gemini-3.7-flash",
        ("gemini", _) => "gemini-3.8-flash",
        ("parakeet", _) => "parakeet-v3",
        ("parakeet-ultra", _) => "parakeet-ultra",
        ("parakeet-sk", _) => "parakeet-v3-sk",
        ("gigaam", _) => "gigaam-v3",
        ("whisper", _) => "whisper-large-v3-turbo",
        _ => "custom",
    };

    private static string Reason(ProcessingFailure failure) => failure.Kind switch
    {
        FailureKind.Environmental when failure.Unreachable => "no_network",
        FailureKind.Environmental when failure.Message.Contains("key", StringComparison.OrdinalIgnoreCase)
                                       || failure.Message.Contains("ключ", StringComparison.OrdinalIgnoreCase) => "no_key",
        FailureKind.Environmental => "no_model",
        FailureKind.Transient => "usage_limit",
        _ => "unknown",
    };

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        queue.Writer.TryComplete();
        if (worker is not null)
            try { await worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        lifetime.Dispose();
    }
}
