using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amanu.Core.Processing;
using static Amanu.Core.Localization.Localized;

namespace Amanu.App;

/// <summary>
/// The audio of one session as the engines see it. A track that is missing or
/// never delivered a sample is null — a silent side, not a failure of the whole
/// session, as long as some other track has audio.
/// </summary>
public sealed record SessionAudio(
    string Directory,
    string Title,
    string? Microphone,
    string? System,
    string? Single,
    int MicrophoneOffsetMs,
    int SystemOffsetMs)
{
    /// <summary>
    /// Where a paid service's answer is kept (<see cref="ProviderCache"/>). It is the session's own
    /// folder even after echo removal has pointed <see cref="Directory"/> at a temporary copy: that
    /// copy is deleted when the pass ends, and a cache kept in it would make a failed pass send all
    /// the audio again. A `with` that changes Directory leaves this as it was.
    /// </summary>
    public string CacheDirectory { get; init; } = Directory;

    public bool HasAudio => Microphone is not null || System is not null || Single is not null;
    public bool BothSides => Microphone is not null && System is not null;

    /// <summary>The one file there is, with the side it belongs to ("" when it is an import).</summary>
    public (string Path, string Side, int OffsetMs) Only =>
        Single is not null ? (Single, "", 0)
        : Microphone is not null ? (Microphone, SpeakerLabels.Me, MicrophoneOffsetMs)
        : System is not null ? (System, SpeakerLabels.Them, SystemOffsetMs)
        : throw new ProcessingFailure(FailureKind.Recording, T("The session has no audio.", "В записи нет звука."));
}

public interface ITranscriptionEngine
{
    string Name { get; }
    Task<TranscriptDocument> TranscribeAsync(SessionAudio audio, CancellationToken cancellationToken);
}

/// <summary>What was sent to a paid service, kept beside the session so a crash after the upload does not pay twice.</summary>
internal static class ProviderCache
{
    public static string? PathFor(string? directory, string provider, params string[] parts)
    {
        if (directory is null) return null;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001F', parts))))[..16];
        return System.IO.Path.Combine(directory, $".cache-{provider}-{hash}.json");
    }

    /// <summary>Every cache in a session, for a re-transcription to throw away: a corrected language must not return the old text.</summary>
    public static void Clear(string directory)
    {
        if (!System.IO.Directory.Exists(directory)) return;
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, ".cache-*.json")) File.Delete(file);
        File.Delete(System.IO.Path.Combine(directory, ".assemblyai-job.json"));
    }

    public static string KeyDigest(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
}

internal static class Http
{
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, Func<HttpRequestMessage> request, string service, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var message = request();
            response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw ProcessingFailure.FromHttp(service, null, exception.Message);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProcessingFailure(FailureKind.Transient, T($"{service} timed out", $"{service} не ответил вовремя"), exception);
        }
        if (response.IsSuccessStatusCode) return response;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        response.Dispose();
        throw ProcessingFailure.FromHttp(service, status, detail);
    }
}

/// <summary>transcribe.cpp on this computer, one call per track: mic is "me", the call is "them".</summary>
public sealed class LocalTranscriptionEngine(ModelManager models, string model, string? languagePin) : ITranscriptionEngine
{
    /// <summary>The macOS GigaAMEngine's chunk.</summary>
    private const int GigaAmChunkSeconds = 20;

    public string Name => model;

    /// <summary>
    /// CPU threads for each transcribe-cli run. AMANU_ASR_THREADS overrides it,
    /// so the measure script can try other counts without a new build.
    /// </summary>
    public static int Threads { get; } = ThreadCount(Environment.GetEnvironmentVariable("AMANU_ASR_THREADS"), Environment.ProcessorCount);

    public static int ThreadCount(string? setting, int processors) =>
        int.TryParse(setting, out var value) && value > 0
            ? Math.Min(value, processors)
            : Math.Max(1, processors);

    /// <summary>
    /// Set after a Vulkan run fails (no GPU, no driver, no Vulkan loader, no
    /// VC++ runtime for the GPU module). Then all later runs in this app use the CPU.
    /// </summary>
    private static volatile bool vulkanFailed;

    /// <summary>
    /// The transcribe-cli backend: "vulkan" when the runtime has the GPU module
    /// (ggml-vulkan.dll), else "cpu". AMANU_ASR_BACKEND=cpu or =vulkan overrides it.
    /// After one failed Vulkan run it is always "cpu".
    /// </summary>
    public static string Backend(string? setting, bool vulkanModulePresent, bool failed)
    {
        if (failed) return "cpu";
        if (string.Equals(setting?.Trim(), "cpu", StringComparison.OrdinalIgnoreCase)) return "cpu";
        if (string.Equals(setting?.Trim(), "vulkan", StringComparison.OrdinalIgnoreCase)) return "vulkan";
        return vulkanModulePresent ? "vulkan" : "cpu";
    }

    private static string BackendFor(string cli) => Backend(
        Environment.GetEnvironmentVariable("AMANU_ASR_BACKEND"),
        File.Exists(Path.Combine(Path.GetDirectoryName(cli) ?? "", "ggml-vulkan.dll")),
        vulkanFailed);

    /// <summary>
    /// Runs transcribe-cli with the chosen backend. A failed Vulkan run is run
    /// again on the CPU, and the CPU is used from then on.
    /// </summary>
    private static async Task<string> RunAsrAsync(string cli, List<string> arguments, TrackTiming timing, CancellationToken cancellationToken)
    {
        var backend = BackendFor(cli);
        if (!timing.Backend.EndsWith("(vulkan failed)", StringComparison.Ordinal)) timing.Backend = backend;
        void Record(string error) { lock (timing.Used) timing.Used.Add(UsedBackend(error) ?? "unknown"); }
        try
        {
            return await RunCliAsync(cli, ["--backend", backend, .. arguments], cancellationToken, Record).ConfigureAwait(false);
        }
        catch (ProcessingFailure) when (backend == "vulkan" && !cancellationToken.IsCancellationRequested)
        {
            vulkanFailed = true;
            timing.Backend = "cpu (vulkan failed)";
            return await RunCliAsync(cli, ["--backend", "cpu", .. arguments], cancellationToken, Record).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The backend that really ran, from transcribe-cli's stderr line
    /// "parakeet: using vulkan backend: Vulkan0" or "using cpu backend (strict)":
    /// "vulkan Vulkan0" or "cpu". Null when the CLI did not say.
    /// </summary>
    public static string? UsedBackend(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return null;
        var matches = System.Text.RegularExpressions.Regex.Matches(stderr, @"using (\w+) backend(?:: (\S+))?");
        if (matches.Count == 0) return null;
        var last = matches[^1];
        return last.Groups[2].Success ? $"{last.Groups[1].Value} {last.Groups[2].Value}" : last.Groups[1].Value;
    }

    /// <summary>The backends that ran a track's runs, for the log: "vulkan Vulkan0", or "unknown".</summary>
    public static string RanOn(IEnumerable<string> used) =>
        used.Any() ? string.Join(" + ", used.Order(StringComparer.Ordinal)) : "unknown";

    /// <summary>Told each track's time: ("asr.me", wall time, detail).</summary>
    public Action<string, TimeSpan, string>? Timing { get; set; }

    /// <summary>
    /// Told how much of recognition is done, 0 to 1, across all tracks. Only
    /// Parakeet reports it, one step per 60-second piece; other models run as
    /// one call and say nothing until they finish. A track that live speech
    /// preempts starts again, so the share can go back.
    /// </summary>
    public Action<double>? Progress { get; set; }

    private sealed class TrackTiming
    {
        /// <summary>Told this track's share done, 0 to 1, after each piece.</summary>
        public Action<double>? Progress;
        public TimeSpan Convert;
        public int Runs;
        public string Backend = "cpu";
        /// <summary>What transcribe-cli said it really ran on, one entry per kind.</summary>
        public readonly HashSet<string> Used = new(StringComparer.Ordinal);
        public double AudioSeconds;
    }

    /// <summary>Threads for each of <paramref name="tracks"/> runs at the same time: an even share, at least one.</summary>
    public static int ShareThreads(int threads, int tracks) => Math.Max(1, threads / Math.Max(1, tracks));

    /// <summary>
    /// The call track's segments, when they were made while the mic was being
    /// echo-cleaned (roadmap-speed step 3). Used instead of running that track
    /// again; the caller sets them only for the same audio and offset.
    /// </summary>
    public IReadOnlyList<TranscriptSegment>? PrecomputedSystem { get; set; }

    /// <summary>One track on its own, under the caller's CPU gate lease.</summary>
    public Task<IReadOnlyList<TranscriptSegment>> TranscribeTrackAsync(
        string source, string side, int offsetMs, int threads, CancellationToken cancellationToken) =>
        TranscribeFileCoreAsync(source, side, offsetMs, threads, new TrackTiming(), cancellationToken);

    public async Task<TranscriptDocument> TranscribeAsync(SessionAudio audio, CancellationToken cancellationToken)
    {
        (string Source, string Side, int OffsetMs)[] tracks = audio.BothSides
            ? [(audio.Microphone!, SpeakerLabels.Me, audio.MicrophoneOffsetMs), (audio.System!, SpeakerLabels.Them, audio.SystemOffsetMs)]
            : [audio.Only];
        var parts = await TranscribeTracksAsync(tracks, cancellationToken).ConfigureAwait(false);
        // Mic first, then the call, as before: equal start times keep the same order.
        var segments = parts.SelectMany(part => part).OrderBy(segment => segment.StartMs).ToArray();
        return new TranscriptDocument(model, model, DateTimeOffset.UtcNow, SpeakerLabels.Assign(segments));
    }

    /// <summary>
    /// Runs every track at the same time, with an even share of the threads.
    /// One CPU gate lease covers all tracks, so live preemption cancels all of them.
    /// A finished track keeps its result; only unfinished tracks run again.
    /// </summary>
    private async Task<IReadOnlyList<TranscriptSegment>[]> TranscribeTracksAsync(
        (string Source, string Side, int OffsetMs)[] tracks, CancellationToken cancellationToken)
    {
        var results = new IReadOnlyList<TranscriptSegment>?[tracks.Length];
        if (PrecomputedSystem is not null && tracks.Length == 2)
            results[1] = PrecomputedSystem;
        var wall = Stopwatch.StartNew();
        var preempted = 0;
        // Each track's share done; the average is the whole of recognition.
        var done = new double[tracks.Length];
        for (var index = 0; index < tracks.Length; index++)
            if (results[index] is not null) done[index] = 1;
        void Report(int index, double share)
        {
            double average;
            lock (done)
            {
                done[index] = share;
                average = done.Average();
            }
            Progress?.Invoke(average);
        }
        while (true)
        {
            using var cpu = await models.CpuTranscription.EnterBatchAsync(cancellationToken).ConfigureAwait(false);
            var pending = Enumerable.Range(0, tracks.Length).Where(index => results[index] is null).ToArray();
            // A track left alone still gets only its share: a single run on all threads was slower than half.
            var threads = ShareThreads(Threads, PrecomputedSystem is null ? pending.Length : tracks.Length);
            // A failed track stops the others; the failure is reported, not their cancellation.
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cpu.Token);
            var runs = pending.Select(async index =>
            {
                var (source, side, offsetMs) = tracks[index];
                var timing = new TrackTiming { Progress = share => Report(index, share) };
                try
                {
                    results[index] = await TranscribeFileCoreAsync(source, side, offsetMs, threads, timing, stop.Token).ConfigureAwait(false);
                    Report(index, 1);
                }
                catch
                {
                    try { stop.Cancel(); } catch (ObjectDisposedException) { }
                    throw;
                }
                Timing?.Invoke($"asr.{side}", wall.Elapsed, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"convert {timing.Convert.TotalSeconds:0.0} s, {timing.Runs} runs, {threads} threads, preempted {preempted}, backend {timing.Backend}, ran on {RanOn(timing.Used)}, audio sent {timing.AudioSeconds:0.0} s"));
            }).ToArray();
            var all = Task.WhenAll(runs);
            try
            {
                await all.ConfigureAwait(false);
                return results!;
            }
            catch when (!cancellationToken.IsCancellationRequested && cpu.Token.IsCancellationRequested)
            {
                // Live preempted this pass. Wait and retry without spending a
                // processing attempt or changing the saved recording.
                preempted++;
            }
            catch when (all.Exception?.InnerExceptions.FirstOrDefault(exception => exception is not OperationCanceledException) is { } failure)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                throw;
            }
        }
    }

    private async Task<IReadOnlyList<TranscriptSegment>> TranscribeFileCoreAsync(
        string source, string side, int offsetMs, int threads, TrackTiming timing, CancellationToken cancellationToken)
    {
        var threadArgument = threads.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var cli = models.CliPath ?? throw new ProcessingFailure(FailureKind.Environmental,
            T("The local transcription runtime is missing from this installation.", "В этой установке нет локального движка расшифровки."));
        var modelPath = models.ModelPath(model);
        if (!File.Exists(modelPath))
            throw new ProcessingFailure(FailureKind.Environmental, T($"{EngineResolver.DisplayName(model)} isn't downloaded.", $"{EngineResolver.DisplayName(model)} не скачана."));
        var temp = Path.Combine(Path.GetTempPath(), "Amanu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var wave = Path.Combine(temp, "input.wav");
            var convert = Stopwatch.StartNew();
            AudioPreprocessor.ConvertToMono16k(source, wave);
            timing.Convert = convert.Elapsed;
            cancellationToken.ThrowIfCancellationRequested();
            if (ModelCatalog.IsParakeet(model))
            {
                // Step 6: long noise-only stretches are left out of each chunk; chunk times map back below.
                IReadOnlyList<(int From, int To)>? keep = null;
                IReadOnlyList<int>? cuts = null;
                var skipSilence = !string.Equals(Environment.GetEnvironmentVariable("AMANU_SKIP_SILENCE"), "0", StringComparison.Ordinal);
                // Off by default: it did not keep the quiet speaker at 0.0 % WER (docs/roadmap-speed.md, "Pause cuts").
                var pauseCuts = string.Equals(Environment.GetEnvironmentVariable("AMANU_PAUSE_CUTS"), "1", StringComparison.Ordinal);
                if (skipSilence || pauseCuts)
                {
                    try
                    {
                        var blocks = AudioPreprocessor.MeasureBlocks(wave);
                        if (blocks is not null)
                        {
                            if (skipSilence)
                            {
                                var spans = SilenceSkip.KeepBlocks(blocks);
                                keep = spans.Count == 1 && spans[0].From == 0 && spans[0].To >= blocks.Length ? null : spans;
                            }
                            // Roadmap "pause cuts": cut at a pause between 40 s and 60 s, not inside a word.
                            if (pauseCuts) cuts = PauseCuts.Boundaries(blocks, 600, 400);
                        }
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException) { keep = null; cuts = null; }
                }
                var chunks = AudioPreprocessor.SplitWithContext(wave, temp, 60, 1, keep, cuts);
                timing.AudioSeconds = chunks.Sum(chunk => new FileInfo(chunk.Path).Length / 32000.0);
                var words = new List<TimedWord>();
                var fallback = new List<TranscriptSegment>();
                var finished = 0;
                foreach (var chunk in chunks)
                {
                    if (finished > 0) timing.Progress?.Invoke((double)finished / chunks.Count);
                    finished++;
                    cancellationToken.ThrowIfCancellationRequested();
                    var args = new List<string> { "-m", modelPath, "--threads", threadArgument, "-q", "--timestamps", "word" };
                    if (!string.IsNullOrWhiteSpace(languagePin)) args.AddRange(["--language", languagePin]);
                    args.Add(chunk.Path);
                    timing.Runs++;
                    var answer = await RunAsrAsync(cli, args, timing, cancellationToken).ConfigureAwait(false);
                    var parsed = LocalCliWords.Parse(answer);
                    words.AddRange(parsed.Select(word => word with
                    {
                        StartMs = (chunk.Map?.ToOriginal(word.StartMs) ?? word.StartMs) + chunk.OffsetMs,
                        EndMs = (chunk.Map?.ToOriginal(word.EndMs) ?? word.EndMs) + chunk.OffsetMs,
                    }).Where(word => (word.StartMs + word.EndMs) / 2 >= chunk.StartMs
                        && (word.StartMs + word.EndMs) / 2 < chunk.EndMs));
                    if (parsed.Count == 0 && LocalCliWords.FullText(answer) is { Length: > 0 } text)
                        fallback.Add(new TranscriptSegment(chunk.StartMs + offsetMs, chunk.EndMs + offsetMs, text, SpeakerLabels.Raw(side, "")));
                }
                return LocalCliWords.Segments(words.OrderBy(word => word.StartMs).ToArray()).Select(item => item with
                {
                    StartMs = item.StartMs + offsetMs,
                    EndMs = item.EndMs + offsetMs,
                    Speaker = SpeakerLabels.Raw(side, ""),
                }).Concat(fallback).OrderBy(segment => segment.StartMs).ToArray();
            }
            // GigaAM hears twenty seconds at a time; Whisper finds its own segments.
            IReadOnlyList<(string Path, long StartMs, long EndMs)> pieces = model == "gigaam"
                ? AudioPreprocessor.Split(wave, temp, GigaAmChunkSeconds)
                : [(wave, 0L, 0L)];
            var arguments = new List<string> { "-m", modelPath, "--threads", threadArgument };
            var batch = Path.Combine(temp, "batch.txt");
            await File.WriteAllLinesAsync(batch, pieces.Select(piece => piece.Path), cancellationToken).ConfigureAwait(false);
            arguments.AddRange(["--batch", batch, "--batch-jsonl", "--timestamps", "auto"]);
            if (!string.IsNullOrWhiteSpace(languagePin)) arguments.AddRange(["--language", languagePin]);
            timing.Runs++;
            var output = await RunAsrAsync(cli, arguments, timing, cancellationToken).ConfigureAwait(false);
            if (model == "gigaam")
            {
                // One segment per piece, as on the Mac: the piece's span, its text.
                var byFile = LocalCliResultParser.ParseAll(output)
                    .GroupBy(item => item.File, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last().Text, StringComparer.OrdinalIgnoreCase);
                var answered = pieces.Count(piece => byFile.ContainsKey(Path.GetFileName(piece.Path)));
                if (answered < pieces.Count)
                    throw new ProcessingFailure(FailureKind.Recording, T(
                        $"Local transcription answered for {answered} of {pieces.Count} pieces.",
                        $"Локальная расшифровка ответила на {answered} из {pieces.Count} кусков."));
                return pieces
                    .Select(piece => (piece, text: byFile.GetValueOrDefault(Path.GetFileName(piece.Path), "").Trim()))
                    .Where(item => item.text.Length > 0)
                    .Select(item => new TranscriptSegment(item.piece.StartMs + offsetMs, item.piece.EndMs + offsetMs, item.text, SpeakerLabels.Raw(side, "")))
                    .ToArray();
            }
            var result = LocalCliResultParser.Parse(output);
            if (result.Segments.Count > 0)
                return result.Segments.Select(item => item with
                {
                    StartMs = item.StartMs + offsetMs,
                    EndMs = item.EndMs + offsetMs,
                    Speaker = SpeakerLabels.Raw(side, item.Speaker ?? ""),
                }).ToArray();
            return string.IsNullOrWhiteSpace(result.Text)
                ? []
                : [new TranscriptSegment(offsetMs, offsetMs, result.Text, SpeakerLabels.Raw(side, ""))];
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    private static async Task<string> RunCliAsync(string cli, IEnumerable<string> arguments, CancellationToken cancellationToken, Action<string>? stderr = null)
    {
        var start = new ProcessStartInfo(cli)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new ProcessingFailure(FailureKind.Environmental,
            T("Could not launch local transcription.", "Не удалось запустить локальную расшифровку."));
        // Batch work only (live speech has its own worker). BelowNormal still
        // yields to the user's apps, but Idle starved ASR on a busy computer.
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (InvalidOperationException) { }
        using var registration = cancellationToken.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { await process.WaitForExitAsync().ConfigureAwait(false); throw; }
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new ProcessingFailure(process.ExitCode < 0 ? FailureKind.Environmental : FailureKind.Recording,
                T("Local transcription failed: ", "Локальная расшифровка не удалась: ") + $"({process.ExitCode}) " + error.Trim());
        stderr?.Invoke(error);
        return output;
    }
}

/// <summary>
/// AssemblyAI over aligned two-channel audio: mic on channel 1, the call on
/// channel 2, each diarized on its own. Detection is narrowed to the expected
/// languages rather than pinned, because a pin on the wrong language returns
/// fluent phonetic garbage instead of failing.
/// </summary>
public sealed class AssemblyAiTranscriptionEngine(
    HttpClient httpClient,
    string apiKey,
    IReadOnlyList<string> expectedLanguages,
    string? speechModel,
    bool cache = true,
    IReadOnlyList<string>? keyTerms = null) : ITranscriptionEngine
{
    private const string Service = "AssemblyAI";

    public string Name => "assemblyai";

    private string Model => speechModel ?? "universal";

    public async Task<TranscriptDocument> TranscribeAsync(SessionAudio audio, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(audio.Directory, ".assemblyai-input.wav");
        try
        {
            string source;
            string side;
            if (audio.BothSides)
            {
                AudioPreprocessor.CreateAlignedStereo16k(
                    audio.Microphone!, audio.System!, audio.MicrophoneOffsetMs, audio.SystemOffsetMs, temporary);
                source = temporary;
                side = "*";
            }
            else
            {
                var only = audio.Only;
                AudioPreprocessor.ConvertToMono16k(only.Path, temporary);
                source = temporary;
                side = only.Side;
            }
            var multichannel = side == "*";
            var shift = audio.BothSides ? Math.Min(audio.MicrophoneOffsetMs, audio.SystemOffsetMs) : audio.Only.OffsetMs;
            var cachePath = cache
                ? ProviderCache.PathFor(audio.CacheDirectory, "assemblyai", "code-switching-v1", Model, string.Join('+', expectedLanguages),
                    multichannel ? "multichannel" : "mono", side, new FileInfo(source).Length.ToString(), CustomWords.Digest(keyTerms))
                : null;

            JsonElement result;
            if (cachePath is not null && File.Exists(cachePath))
                result = JsonDocument.Parse(await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false)).RootElement.Clone();
            else
            {
                var id = await ResumableJobAsync(audio.CacheDirectory, cachePath)
                         ?? await SubmitAsync(source, multichannel, audio.CacheDirectory, cachePath, cancellationToken).ConfigureAwait(false);
                try
                {
                    result = await PollAsync(id, cancellationToken).ConfigureAwait(false);
                }
                catch (ProcessingFailure failure) when (failure.Kind == FailureKind.Recording)
                {
                    // A job that errored or is gone is not resumed: the next attempt submits afresh.
                    File.Delete(Path.Combine(audio.CacheDirectory, ".assemblyai-job.json"));
                    throw;
                }
                if (cachePath is not null)
                    await AtomicFiles.WriteTextAsync(cachePath, result.GetRawText(), cancellationToken).ConfigureAwait(false);
                File.Delete(Path.Combine(audio.CacheDirectory, ".assemblyai-job.json"));
            }
            var segments = Segments(result, side);
            if (shift != 0)
                segments = segments.Select(segment => segment with { StartMs = segment.StartMs + shift, EndMs = segment.EndMs + shift }).ToArray();
            return new TranscriptDocument("assemblyai", Model, DateTimeOffset.UtcNow, SpeakerLabels.Assign(segments));
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>
    /// A job submitted before a crash, polled rather than uploaded and paid for
    /// again — but only with the key that submitted it and for the same request:
    /// another key cannot read it, and a different request is a different job.
    /// </summary>
    private Task<string?> ResumableJobAsync(string directory, string? cachePath)
    {
        var path = Path.Combine(directory, ".assemblyai-job.json");
        if (cachePath is null || !File.Exists(path)) return Task.FromResult<string?>(null);
        try
        {
            using var job = JsonDocument.Parse(File.ReadAllText(path));
            var root = job.RootElement;
            return Task.FromResult(
                root.GetProperty("key").GetString() == ProviderCache.KeyDigest(apiKey) && root.GetProperty("cache").GetString() == Path.GetFileName(cachePath)
                    ? root.GetProperty("id").GetString()
                    : null);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private async Task<string> SubmitAsync(string source, bool multichannel, string directory, string? cachePath, CancellationToken cancellationToken)
    {
        using var upload = await Http.SendAsync(httpClient, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.assemblyai.com/v2/upload");
            request.Headers.TryAddWithoutValidation("authorization", apiKey);
            request.Content = new StreamContent(File.OpenRead(source));
            return request;
        }, Service, cancellationToken).ConfigureAwait(false);
        var uploadUrl = (await upload.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false))
            .GetProperty("upload_url").GetString();

        var body = RequestBody(uploadUrl!, multichannel, expectedLanguages, speechModel, keyTerms);

        using var created = await Http.SendAsync(httpClient, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.assemblyai.com/v2/transcript");
            request.Headers.TryAddWithoutValidation("authorization", apiKey);
            request.Content = JsonContent.Create(body);
            return request;
        }, Service, cancellationToken).ConfigureAwait(false);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false)).GetProperty("id").GetString()!;
        if (cachePath is not null)
            await AtomicFiles.WriteJsonAsync(Path.Combine(directory, ".assemblyai-job.json"),
                new { id, key = ProviderCache.KeyDigest(apiKey), cache = Path.GetFileName(cachePath) }, cancellationToken).ConfigureAwait(false);
        return id;
    }

    internal static Dictionary<string, object?> RequestBody(string audioUrl, bool multichannel,
        IReadOnlyList<string> expectedLanguages, string? speechModel, IReadOnlyList<string>? keyTerms = null)
    {
        // Universal-2 requires code switching explicitly even on automatic
        // language selection. Keep one request and the existing channel layout.
        var detection = new Dictionary<string, object?> { ["code_switching"] = true };
        if (expectedLanguages.Count > 0)
        {
            detection["expected_languages"] = expectedLanguages;
            detection["fallback_language"] = expectedLanguages[0];
        }
        var body = new Dictionary<string, object?>
        {
            ["audio_url"] = audioUrl,
            ["speaker_labels"] = true,
            ["punctuate"] = true,
            ["format_text"] = true,
            ["language_detection"] = true,
            ["language_detection_options"] = detection,
        };
        if (multichannel) body["multichannel"] = true;
        if (speechModel is not null) body["speech_model"] = speechModel;
        // The user's word list; up to 200 phrases of six words fit every model.
        if (keyTerms is { Count: > 0 }) body["keyterms_prompt"] = keyTerms.Take(200).ToArray();
        return body;
    }

    /// <summary>
    /// A poll is a free GET against a job already paid for, so a failed one is sat
    /// out rather than given up on. A job the service no longer has is dropped, so
    /// the next attempt submits afresh.
    /// </summary>
    private async Task<JsonElement> PollAsync(string id, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            JsonElement result;
            try
            {
                using var response = await Http.SendAsync(httpClient, () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.assemblyai.com/v2/transcript/{id}");
                    request.Headers.TryAddWithoutValidation("authorization", apiKey);
                    return request;
                }, Service, cancellationToken).ConfigureAwait(false);
                result = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
                failures = 0;
            }
            // Sat out: the network dropping or the service busy. Not a refused key,
            // which no amount of waiting fixes.
            catch (ProcessingFailure failure) when ((failure.Kind == FailureKind.Transient || failure.Unreachable) && ++failures < 60)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * failures)), cancellationToken).ConfigureAwait(false);
                continue;
            }
            var status = result.GetProperty("status").GetString();
            if (status == "completed") return result;
            if (status == "error")
                throw new ProcessingFailure(FailureKind.Recording, "AssemblyAI: " + result.GetProperty("error").GetString());
        }
    }

    private static IReadOnlyList<TranscriptSegment> Segments(JsonElement root, string side)
    {
        if (!root.TryGetProperty("utterances", out var utterances) || utterances.ValueKind != JsonValueKind.Array)
        {
            var text = ReadString(root, "text");
            return string.IsNullOrWhiteSpace(text) ? [] : [new TranscriptSegment(0, 0, text, SpeakerLabels.Raw(side == "*" ? "" : side, ""))];
        }
        return utterances.EnumerateArray().Select(item =>
        {
            var speaker = ReadString(item, "speaker") ?? "";
            var channel = ReadString(item, "channel") ?? (speaker.Length > 0 && char.IsDigit(speaker[0]) ? speaker[..1] : null);
            var voice = speaker.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            var itemSide = side != "*" ? side : channel == "1" ? SpeakerLabels.Me : channel == "2" ? SpeakerLabels.Them : "";
            return new TranscriptSegment(ReadLong(item, "start"), ReadLong(item, "end"), ReadString(item, "text") ?? "",
                SpeakerLabels.Raw(itemSide, voice));
        }).Where(item => !string.IsNullOrWhiteSpace(item.Text)).ToArray();
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()
        : element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;
}

/// <summary>
/// OpenAI's diarizing model over one mixed-down file, cut into ten-minute pieces.
/// Its speakers are mapped back onto me and them by comparing each voice's
/// energy in the two tracks, since the mix itself cannot say who was where.
/// </summary>
public sealed class OpenAiTranscriptionEngine(
    HttpClient httpClient,
    string apiKey,
    string model,
    string? languagePin,
    bool cache = true) : ITranscriptionEngine
{
    private const string Service = "OpenAI";

    public string Name => "openai";

    public async Task<TranscriptDocument> TranscribeAsync(SessionAudio audio, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetTempPath(), "Amanu", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var mixed = Path.Combine(temp, "mixed.wav");
            string? stereo = null;
            var offset = 0;
            string side = "";
            if (audio.BothSides)
            {
                stereo = Path.Combine(temp, "stereo.wav");
                AudioPreprocessor.CreateAlignedStereo16k(
                    audio.Microphone!, audio.System!, audio.MicrophoneOffsetMs, audio.SystemOffsetMs, stereo);
                AudioPreprocessor.ConvertToMono16k(stereo, mixed);
                offset = Math.Min(audio.MicrophoneOffsetMs, audio.SystemOffsetMs);
            }
            else
            {
                var only = audio.Only;
                AudioPreprocessor.ConvertToMono16k(only.Path, mixed);
                side = only.Side;
                offset = only.OffsetMs;
            }

            var parts = AudioPreprocessor.SplitWav(mixed, Path.Combine(temp, "parts"));
            var all = new List<TranscriptSegment>();
            for (var index = 0; index < parts.Count; index++)
            {
                var part = parts[index];
                var cachePath = cache
                    ? ProviderCache.PathFor(audio.CacheDirectory, "openai", model, languagePin ?? "detect", $"{index}/{parts.Count}",
                        new FileInfo(part.Path).Length.ToString())
                    : null;
                string json;
                if (cachePath is not null && File.Exists(cachePath)) json = await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
                else
                {
                    json = await TranscribePartAsync(part.Path, cancellationToken).ConfigureAwait(false);
                    if (cachePath is not null) await AtomicFiles.WriteTextAsync(cachePath, json, cancellationToken).ConfigureAwait(false);
                }
                // Voices are named per request, so "A" in one piece is not "A" in the next.
                all.AddRange(Parse(json, part.OffsetSeconds).Select(segment => segment with { Speaker = $"{index}:{segment.Speaker}" }));
            }

            var sides = stereo is null
                ? all.Select(segment => segment.Speaker!).Distinct().ToDictionary(label => label, _ => side)
                : AudioPreprocessor.SideByEnergy(stereo, all);
            var labelled = all.Select(segment => segment with
            {
                StartMs = segment.StartMs + offset,
                EndMs = segment.EndMs + offset,
                Speaker = SpeakerLabels.Raw(sides.GetValueOrDefault(segment.Speaker!, ""), segment.Speaker),
            }).ToArray();
            return new TranscriptDocument("openai", model, DateTimeOffset.UtcNow, SpeakerLabels.Assign(labelled));
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    private async Task<string> TranscribePartAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await Http.SendAsync(httpClient, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var content = new MultipartFormDataContent
            {
                { new StringContent(model), "model" },
                { new StringContent("diarized_json"), "response_format" },
                { new StringContent("auto"), "chunking_strategy" },
            };
            if (languagePin is not null) content.Add(new StringContent(languagePin), "language");
            var file = new StreamContent(File.OpenRead(path));
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(file, "file", Path.GetFileName(path));
            request.Content = content;
            return request;
        }, Service, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<TranscriptSegment> Parse(string json, double offsetSeconds)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("segments", out var segments)) return [];
        return segments.EnumerateArray().Select(item => new TranscriptSegment(
            (long)((ReadDouble(item, "start") + offsetSeconds) * 1000),
            (long)((ReadDouble(item, "end") + offsetSeconds) * 1000),
            item.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "",
            item.TryGetProperty("speaker", out var speaker) ? speaker.GetString() ?? "" : ""))
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Text)).ToArray();
    }

    private static double ReadDouble(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : 0;
}

/// <summary>ElevenLabs Scribe, each track on its own: the track says the side, diarization the voice.</summary>
public sealed class ElevenLabsTranscriptionEngine(HttpClient httpClient, string apiKey, bool cache = true, IReadOnlyList<string>? keyTerms = null) : ITranscriptionEngine
{
    private const string Service = "ElevenLabs";
    private const string Model = "scribe_v2";

    public string Name => "elevenlabs";

    public async Task<TranscriptDocument> TranscribeAsync(SessionAudio audio, CancellationToken cancellationToken)
    {
        var tracks = audio.BothSides
            ? new[] { (audio.Microphone!, SpeakerLabels.Me, audio.MicrophoneOffsetMs), (audio.System!, SpeakerLabels.Them, audio.SystemOffsetMs) }
            : [audio.Only];
        var all = new List<TranscriptSegment>();
        foreach (var (path, side, offset) in tracks)
        {
            var temp = Path.Combine(audio.Directory, $".elevenlabs-{side}.wav");
            try
            {
                AudioPreprocessor.ConvertToMono16k(path, temp);
                var cachePath = cache ? ProviderCache.PathFor(audio.CacheDirectory, "elevenlabs", Model, side, new FileInfo(temp).Length.ToString(), CustomWords.Digest(keyTerms)) : null;
                string json;
                if (cachePath is not null && File.Exists(cachePath)) json = await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
                else
                {
                    using var response = await Http.SendAsync(httpClient, () =>
                    {
                        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.elevenlabs.io/v1/speech-to-text");
                        request.Headers.TryAddWithoutValidation("xi-api-key", apiKey);
                        var content = new MultipartFormDataContent
                        {
                            { new StringContent(Model), "model_id" },
                            { new StringContent("word"), "timestamps_granularity" },
                            { new StringContent("false"), "tag_audio_events" },
                            { new StringContent("true"), "diarize" },
                        };
                        // Up to 100 terms: more would bill a 20 s minimum per request (and +20 % in all).
                        foreach (var term in keyTerms?.Take(100) ?? [])
                            content.Add(new StringContent(term), "keyterms");
                        var file = new StreamContent(File.OpenRead(temp));
                        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                        content.Add(file, "file", "audio.wav");
                        request.Content = content;
                        return request;
                    }, Service, cancellationToken).ConfigureAwait(false);
                    json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (cachePath is not null) await AtomicFiles.WriteTextAsync(cachePath, json, cancellationToken).ConfigureAwait(false);
                }
                all.AddRange(Turns(json, side, offset));
            }
            finally
            {
                File.Delete(temp);
            }
        }
        return new TranscriptDocument("elevenlabs", Model, DateTimeOffset.UtcNow,
            SpeakerLabels.Assign(all.OrderBy(segment => segment.StartMs).ToArray()));
    }

    /// <summary>Words joined into turns: one voice, no gap longer than a second and a half.</summary>
    private static IEnumerable<TranscriptSegment> Turns(string json, string side, int offsetMs)
    {
        using var document = JsonDocument.Parse(json);
        var turns = new List<(double Start, double End, StringBuilder Text, string Voice)>();
        string? lastVoice = null;
        if (document.RootElement.TryGetProperty("words", out var words))
        {
            foreach (var word in words.EnumerateArray())
            {
                var type = word.TryGetProperty("type", out var t) ? t.GetString() : "word";
                var text = word.TryGetProperty("text", out var w) ? w.GetString() ?? "" : "";
                var voice = word.TryGetProperty("speaker_id", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : lastVoice ?? "";
                if (type == "spacing")
                {
                    if (turns.Count > 0 && turns[^1].Voice == voice) turns[^1].Text.Append(text);
                    continue;
                }
                if (type != "word") continue;
                var start = word.GetProperty("start").GetDouble();
                var end = word.GetProperty("end").GetDouble();
                lastVoice = voice;
                if (turns.Count > 0 && turns[^1].Voice == voice && start - turns[^1].End <= 1.5)
                {
                    turns[^1].Text.Append(text);
                    turns[^1] = turns[^1] with { End = Math.Max(turns[^1].End, end) };
                }
                else turns.Add((start, end, new StringBuilder(text), voice));
            }
        }
        foreach (var turn in turns)
        {
            var text = turn.Text.ToString().Trim();
            if (text.Length > 0)
                yield return new TranscriptSegment((long)(turn.Start * 1000) + offsetMs, (long)(turn.End * 1000) + offsetMs, text,
                    SpeakerLabels.Raw(side, turn.Voice));
        }
    }
}

/// <summary>
/// Gemini, each track on its own, five minutes at a time: the track says the side, the model's
/// speaker letters say the voice. It is a language model, not a recognizer, so a piece it is
/// handed is short enough for it to keep timestamps straight, and it is off unless asked for.
/// </summary>
public sealed class GeminiTranscriptionEngine(
    HttpClient httpClient, IReadOnlyList<string> apiKeys, IReadOnlyList<string> expectedLanguages, bool cache = true,
    IReadOnlyList<string>? keyTerms = null, IReadOnlyList<TimeSpan>? backoff = null, IReadOnlyList<string>? models = null) : ITranscriptionEngine
{
    private const string Service = "Gemini";

    private readonly GeminiKeyRing keys = new(apiKeys);

    /// <summary>The models in the order they are asked (the Gemini models setting).</summary>
    private readonly IReadOnlyList<string> order = GeminiTranscript.ModelOrder(models);
    private const int PieceSeconds = 300;

    /// <summary>What a busy service is given before the next try: three tries, then the fallback model once.</summary>
    private static readonly TimeSpan[] DefaultBackoff = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45)];

    public string Name => "gemini";

    public async Task<TranscriptDocument> TranscribeAsync(SessionAudio audio, CancellationToken cancellationToken)
    {
        var tracks = audio.BothSides
            ? new[] { (audio.Microphone!, SpeakerLabels.Me, audio.MicrophoneOffsetMs), (audio.System!, SpeakerLabels.Them, audio.SystemOffsetMs) }
            : [audio.Only];
        var prompt = GeminiTranscript.Prompt(expectedLanguages, keyTerms);
        var all = new List<TranscriptSegment>();
        // Once a model has used up its tries, the rest of the session starts with the next one:
        // asking a model that has just given up, for every remaining piece, would add minutes for nothing.
        var firstUsable = 0;
        // The latest model in the order that answered any piece, and the name it gave.
        var weakest = -1;
        string? weakestAnswer = null;
        foreach (var (path, side, offset) in tracks)
        {
            var work = Path.Combine(audio.Directory, $".gemini-{side}");
            try
            {
                Directory.CreateDirectory(work);
                var wave = Path.Combine(work, "track.wav");
                AudioPreprocessor.ConvertToMono16k(path, wave);
                var pieces = AudioPreprocessor.SplitWithContext(wave, work, PieceSeconds, 1, null, null);
                for (var index = 0; index < pieces.Count; index++)
                {
                    var piece = pieces[index];
                    cancellationToken.ThrowIfCancellationRequested();
                    var length = new FileInfo(piece.Path).Length.ToString();
                    var number = index.ToString();
                    string? CachePath(string model) => cache
                        ? ProviderCache.PathFor(audio.CacheDirectory, "gemini", model, side, number, length, CustomWords.Digest(keyTerms))
                        : null;
                    string? body = null;
                    var asked = 0;
                    for (var m = 0; m < order.Count; m++)
                    {
                        if (CachePath(order[m]) is { } cached && File.Exists(cached))
                        {
                            body = await File.ReadAllTextAsync(cached, cancellationToken).ConfigureAwait(false);
                            asked = m;
                            break;
                        }
                    }
                    if (body is null)
                    {
                        var wavBytes = await File.ReadAllBytesAsync(piece.Path, cancellationToken).ConfigureAwait(false);
                        (body, asked) = await AskAsync(GeminiTranscript.RequestBody(prompt, wavBytes), firstUsable, cancellationToken).ConfigureAwait(false);
                        firstUsable = asked;
                        if (CachePath(order[asked]) is { } cachePath) await AtomicFiles.WriteTextAsync(cachePath, body, cancellationToken).ConfigureAwait(false);
                    }
                    var (text, version) = GeminiTranscript.ReadResponse(body);
                    if (asked > weakest)
                    {
                        weakest = asked;
                        weakestAnswer = string.IsNullOrWhiteSpace(version) ? order[asked] : version;
                    }
                    all.AddRange(GeminiTranscript.Segments(text, side, piece.OffsetMs, piece.StartMs, piece.EndMs)
                        .Select(segment => segment with { StartMs = segment.StartMs + offset, EndMs = segment.EndMs + offset }));
                }
            }
            finally
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            }
        }
        // One model name has to describe the whole transcript. The latest model in the order that answered
        // any piece is the one named: it is the one asked last, and the reader should know it was used.
        return new TranscriptDocument("gemini", weakestAnswer ?? order[0], DateTimeOffset.UtcNow,
            SpeakerLabels.Assign(all.OrderBy(segment => segment.StartMs).ToArray()));
    }

    /// <summary>
    /// The body of a successful answer and the place in the order of the model that gave it. The first
    /// model is tried again after each back-off delay; every later one is asked once, since it is only
    /// reached when the one before it has given up.
    /// </summary>
    private async Task<(string Body, int Model)> AskAsync(string requestBody, int start, CancellationToken cancellationToken)
    {
        var delays = backoff ?? DefaultBackoff;
        ProcessingFailure? busy = null;
        for (var m = start; m < order.Count; m++)
        {
            var model = order[m];
            for (var attempt = 0; ; attempt++)
            {
                var (body, failure) = await keys.SendAsync(key => SendAsync(model, key, requestBody, cancellationToken)).ConfigureAwait(false);
                if (body is not null) return (body, m);
                busy = failure;
                if (m > 0 || attempt >= delays.Count) break;
                await Task.Delay(delays[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
        throw busy!;
    }

    /// <summary>One request: the body, or the failure and status when 429, 500 or 503 says the service is busy; any other failure is thrown.</summary>
    private async Task<(string? Body, ProcessingFailure? Busy, int Status)> SendAsync(string model, string apiKey, string requestBody, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
            // The key goes in a header: a URL ends up in logs and in exception messages.
            message.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
            message.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw ProcessingFailure.FromHttp(Service, null, exception.Message);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProcessingFailure(FailureKind.Transient, T($"{Service} timed out", $"{Service} не ответил вовремя"), exception);
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return (text, null, 200);
            var status = (int)response.StatusCode;
            var failure = ProcessingFailure.FromHttp(Service, status, text);
            if (status is 429 or 500 or 503) return (null, failure, status);
            throw failure;
        }
    }
}
