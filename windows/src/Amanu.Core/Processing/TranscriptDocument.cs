using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amanu.Core.Processing;

public sealed record TranscriptSegment(
    [property: JsonPropertyName("start_ms")] long StartMs,
    [property: JsonPropertyName("end_ms")] long EndMs,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("speaker")] string? Speaker = null);

public sealed record TranscriptDocument(
    [property: JsonPropertyName("engine")] string Engine,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("segments")] IReadOnlyList<TranscriptSegment> Segments,
    // Null in transcripts made before this was recorded, and in tests that do not care.
    [property: JsonPropertyName("echo"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TranscriptEcho? Echo = null);

/// <summary>
/// Which echo removal the engine heard the mic through, so transcripts of one
/// recording made with different echo_model settings can be told apart and compared.
/// </summary>
/// <param name="Model">The echo_model key that ran or was meant to run, or "off" when the setting was off.</param>
/// <param name="Ran">Whether the mic the engine heard was actually changed by it.</param>
/// <param name="Note">Why it did not run, or how it ran differently from its name.</param>
/// <param name="Processor">The audio path's revision (<see cref="LocalVqeModel.ProcessorVersion"/>), since a model's name outlives changes to its code.</param>
/// <param name="TranscriptFilter">Whether the text-level transcript_echo_filter also ran.</param>
public sealed record TranscriptEcho(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("ran")] bool Ran,
    [property: JsonPropertyName("note"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note = null,
    [property: JsonPropertyName("processor"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Processor = null,
    [property: JsonPropertyName("transcript_filter")] bool TranscriptFilter = false)
{
    public const string Off = "off";

    /// <summary>The few words a tab has room for.</summary>
    public string ShortName => Ran ? Model : Model == Off ? "echo off" : "no echo removal";

    /// <summary>The line under the engine in transcript.md.</summary>
    public string Describe()
    {
        var text = Model == Off ? Off
            : Ran ? Model + (Note is null ? "" : $" ({Note})")
            : $"{Model}, not run" + (Note is null ? "" : $" ({Note})");
        return TranscriptFilter ? text + "; transcript echo filter on" : text;
    }
}

public static class TranscriptWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task WriteAsync(
        string directory,
        string title,
        TranscriptDocument transcript,
        IReadOnlyDictionary<string, string>? speakerNames = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var markdown = RenderMarkdown(title, transcript, speakerNames);
        await WriteAtomicallyAsync(Path.Combine(directory, "transcript.md"), markdown, cancellationToken)
            .ConfigureAwait(false);
        await WriteAtomicallyAsync(
                Path.Combine(directory, "transcript.json"),
                JsonSerializer.Serialize(transcript, JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static string RenderMarkdown(
        string title,
        TranscriptDocument transcript,
        IReadOnlyDictionary<string, string>? speakerNames = null)
    {
        var result = new StringBuilder()
            .Append("# ").AppendLine(title)
            .AppendLine()
            .Append("engine: ").Append(transcript.Engine).Append(" (").Append(transcript.Model).AppendLine(")");
        if (transcript.Echo is { } echo) result.Append("echo removal: ").AppendLine(echo.Describe());
        if (speakerNames is { Count: > 0 })
        {
            result.Append("speakers: ").AppendLine(string.Join(", ", speakerNames.Select(pair => $"{pair.Key} → {pair.Value}")));
        }
        result.AppendLine();
        foreach (var segment in transcript.Segments)
        {
            var time = TimeSpan.FromMilliseconds(Math.Max(0, segment.StartMs));
            var timestamp = time.TotalHours >= 1
                ? time.ToString("h\\:mm\\:ss", CultureInfo.InvariantCulture)
                : time.ToString("m\\:ss", CultureInfo.InvariantCulture);
            var speaker = segment.Speaker is null
                ? "speaker"
                : speakerNames?.GetValueOrDefault(segment.Speaker) ?? segment.Speaker;
            result.Append("**[").Append(timestamp).Append("] ").Append(speaker).Append(":** ")
                .AppendLine(segment.Text.Trim()).AppendLine();
        }
        return result.ToString();
    }

    private static async Task WriteAtomicallyAsync(string path, string contents, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, contents, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
