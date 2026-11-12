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

    /// <summary>The audio echo removal's row in transcript.md; the text filter has a row of its own.</summary>
    public string Describe() =>
        Model == Off ? Off
            : Ran ? Model + (Note is null ? "" : $" ({Note})")
            : $"{Model}, not run" + (Note is null ? "" : $" ({Note})");
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
        // A table with an empty header, which the preview leaves out, so that the
        // provenance reads as label and value rather than as one run-on line.
        var result = new StringBuilder()
            .Append("# ").AppendLine(title)
            .AppendLine()
            .AppendLine("| | |")
            .AppendLine("|---|---|");
        void Row(string label, string value) => result.Append("| ").Append(label).Append(" | ").Append(Cell(value)).AppendLine(" |");
        Row("Transcribed", FormatTime(transcript.CreatedAt.ToLocalTime()));
        Row("Engine", $"{transcript.Engine} · {transcript.Model}");
        if (transcript.Echo is { } echo)
        {
            Row("Echo removal (audio)", echo.Describe());
            Row("Echo filter (text)", echo.TranscriptFilter ? "on" : "off");
        }
        if (speakerNames is { Count: > 0 })
            Row("Speakers", string.Join(", ", speakerNames.Select(pair => $"{pair.Key} → {pair.Value}")));
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

    /// <summary>
    /// The time in the offset it is given, with that offset written out, so the
    /// time still reads right after a move to another time zone.
    /// </summary>
    public static string FormatTime(DateTimeOffset time)
    {
        var offset = time.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            + $" (UTC{sign}{offset.Duration().ToString("hh\\:mm", CultureInfo.InvariantCulture)})";
    }

    // A note can hold an exception's message, and a name anything a person typed.
    private static string Cell(string value) => value.ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal);

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
