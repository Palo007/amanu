using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amanu.Core.Processing;

/// <summary>One summary of one transcript: the current summary.md, or an archived one.</summary>
public sealed record SummaryVersion(string Directory, string Backend, string? Model, DateTimeOffset CreatedAt, bool IsCurrent)
{
    public string BackendName => SummaryVersions.BackendName(Backend);
}

/// <summary>
/// A summary asked for by hand. <paramref name="Transcript"/> is the name of the
/// archived transcript folder under transcripts/, or null for the current transcript.
/// </summary>
public sealed record SummaryRequest(
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("transcript")] string? Transcript = null);

/// <summary>
/// Summaries are kept as transcripts are (<see cref="TranscriptVersions"/>): a
/// new summary.md archives the one it replaces under summaries/, with
/// summary.json saying which engine and model wrote it, when, and from which
/// transcript. A transcript archived by "Transcribe again" takes its summaries
/// with it, so each transcript version keeps its own.
/// </summary>
public static class SummaryVersions
{
    /// <summary>In the recording folder: a JSON list of <see cref="SummaryRequest"/>, worked off in order.</summary>
    public const string RequestFile = "summarize.requested";
    public const string InfoFile = "summary.json";
    public const string Folder = "summaries";

    private static readonly object RequestLock = new();

    private sealed record Info(
        [property: JsonPropertyName("backend")] string? Backend,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
        [property: JsonPropertyName("transcript")] string? Transcript);

    public static string BackendName(string backend) => backend switch
    {
        "claude-cli" => "Claude Code",
        "anthropic-api" => "Anthropic API",
        "codex-cli" => "Codex",
        "openai-api" => "OpenAI API",
        "ollama" => "Ollama",
        "" or "?" => "?",
        _ => backend,
    };

    /// <summary>The name an archived copy of this folder's transcript.json has under transcripts/.</summary>
    public static string? TranscriptId(string directory)
    {
        var path = Path.Combine(directory, "transcript.json");
        return File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : null;
    }

    /// <summary>
    /// Copies the current summary under summaries/ before a new one replaces it.
    /// A stale summary belongs to the transcript before, and is already kept
    /// in that transcript's archive.
    /// </summary>
    public static async Task ArchiveCurrentAsync(string directory, CancellationToken cancellationToken = default)
    {
        var summary = Path.Combine(directory, "summary.md");
        if (!File.Exists(summary) || File.Exists(Path.Combine(directory, "summary.stale"))) return;
        var bytes = await File.ReadAllBytesAsync(summary, cancellationToken).ConfigureAwait(false);
        var root = Path.Combine(directory, Folder);
        var destination = Path.Combine(root, Convert.ToHexStringLower(SHA256.HashData(bytes))[..16]);
        if (System.IO.Directory.Exists(destination)) return;
        var staging = Path.Combine(root, "." + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(staging);
        try
        {
            File.Copy(summary, Path.Combine(staging, "summary.md"));
            var info = Path.Combine(directory, InfoFile);
            if (File.Exists(info)) File.Copy(info, Path.Combine(staging, InfoFile));
            else
                // A summary from before summary.json existed: keep at least which transcript it is of.
                await WriteAsync(Path.Combine(staging, InfoFile),
                    new Info(null, null, File.GetLastWriteTimeUtc(summary), TranscriptId(directory)), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            System.IO.Directory.Move(staging, destination);
        }
        finally
        {
            if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>Written beside a new summary.md.</summary>
    public static Task WriteInfoAsync(string directory, string backend, string? model, DateTimeOffset createdAt, CancellationToken cancellationToken = default) =>
        WriteAsync(Path.Combine(directory, InfoFile), new Info(backend, model, createdAt, TranscriptId(directory)), cancellationToken);

    /// <summary>The summaries of the transcript in this folder, newest first.</summary>
    public static async Task<IReadOnlyList<SummaryVersion>> ReadAsync(string directory, CancellationToken cancellationToken = default)
    {
        var transcript = TranscriptId(directory);
        var versions = new List<SummaryVersion>();
        var root = Path.Combine(directory, Folder);
        if (System.IO.Directory.Exists(root))
            foreach (var archive in System.IO.Directory.EnumerateDirectories(root).Where(path => !Path.GetFileName(path).StartsWith('.')))
                await AddAsync(archive, false).ConfigureAwait(false);
        if (!File.Exists(Path.Combine(directory, "summary.stale"))) await AddAsync(directory, true).ConfigureAwait(false);
        return versions.OrderByDescending(version => version.CreatedAt).ThenByDescending(version => version.IsCurrent).ToList();

        async Task AddAsync(string path, bool current)
        {
            var summary = Path.Combine(path, "summary.md");
            if (!File.Exists(summary)) return;
            var info = await ReadInfoAsync(Path.Combine(path, InfoFile), cancellationToken).ConfigureAwait(false);
            // Summaries left in summaries/ from a transcript that was replaced belong to that transcript's archive.
            if (!current && info?.Transcript is { } of && transcript is not null && of != transcript) return;
            var version = new SummaryVersion(path, info?.Backend ?? "?", info?.Model,
                info?.CreatedAt ?? File.GetLastWriteTimeUtc(summary), current);
            // A crash after archiving but before the new summary.md was written must not show it twice.
            if (current) versions.RemoveAll(other => !other.IsCurrent && SameText(other.Directory, path));
            versions.Add(version);
        }

        static bool SameText(string a, string b) =>
            File.ReadAllBytes(Path.Combine(a, "summary.md")).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(b, "summary.md")));
    }

    /// <summary>The folder a request's summary is written in.</summary>
    public static string Target(string recordingDirectory, SummaryRequest request) =>
        request.Transcript is { Length: > 0 } archive && !archive.Contains('/') && !archive.Contains('\\') && !archive.Contains("..")
            ? Path.Combine(recordingDirectory, "transcripts", archive)
            : recordingDirectory;

    /// <summary>Adds a request; one asked for while another waits runs after it.</summary>
    public static void Request(string recordingDirectory, SummaryRequest request)
    {
        lock (RequestLock)
        {
            var requests = ReadRequests(recordingDirectory).ToList();
            requests.Add(request);
            WriteRequests(recordingDirectory, requests);
        }
    }

    public static IReadOnlyList<SummaryRequest> ReadRequests(string recordingDirectory)
    {
        var path = Path.Combine(recordingDirectory, RequestFile);
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<SummaryRequest>>(File.ReadAllText(path))?
                .Where(request => !string.IsNullOrWhiteSpace(request.Backend)).ToList() ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Takes away the first request equal to this one, when it is done or can never be done.</summary>
    public static void Remove(string recordingDirectory, SummaryRequest request)
    {
        lock (RequestLock)
        {
            var requests = ReadRequests(recordingDirectory).ToList();
            requests.Remove(request);
            WriteRequests(recordingDirectory, requests);
        }
    }

    private static void WriteRequests(string recordingDirectory, IReadOnlyList<SummaryRequest> requests)
    {
        var path = Path.Combine(recordingDirectory, RequestFile);
        if (requests.Count == 0)
        {
            File.Delete(path);
            return;
        }
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(requests));
        File.Move(temporary, path, overwrite: true);
    }

    private static async Task<Info?> ReadInfoAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Info>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)); }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static async Task WriteAsync(string path, Info info, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(info), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }
}
