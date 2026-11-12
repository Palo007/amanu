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
    [property: JsonPropertyName("echo"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TranscriptEcho? Echo = null,
    // In the order they ran; null in transcripts made before this was recorded.
    [property: JsonPropertyName("stages"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TranscriptStage>? Stages = null)
{
    /// <summary>This transcript with `stage` timed at `elapsed`, in place of an earlier time for the same stage.</summary>
    public TranscriptDocument WithStage(string stage, TimeSpan elapsed) => this with
    {
        Stages = [.. (Stages ?? []).Where(existing => existing.Stage != stage), new TranscriptStage(stage, Math.Round(elapsed.TotalSeconds, 1))],
    };
}

/// <summary>
/// How long one stage of making the transcript took, so two transcripts of one
/// recording can be compared on cost as well as on words.
/// </summary>
/// <param name="Stage">decode, echo, levels, asr, diarization, total or speaker_names, as in the timing lines of transcribe.log.</param>
public sealed record TranscriptStage(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("seconds")] double Seconds)
{
    public const string Decode = "decode";
    public const string Echo = "echo";
    public const string Levels = "levels";
    public const string Recognition = "asr";
    public const string Diarization = "diarization";
    public const string Total = "total";
    public const string SpeakerNames = "speaker_names";

    /// <summary>Tenths of a second under a minute, where most stages are; minutes and seconds above it.</summary>
    public string Duration()
    {
        if (Seconds < 60) return Seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        var time = TimeSpan.FromSeconds(Math.Round(Seconds));
        return time.TotalHours >= 1
            ? time.ToString("h\\:mm\\:ss", CultureInfo.InvariantCulture)
            : time.ToString("m\\:ss", CultureInfo.InvariantCulture);
    }
}

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
        // A table of label and value, so that the provenance does not read as one
        // run-on line. A transcript that was timed gets a third column with the time
        // of the stage each row names, beside the setting it was timed under; its
        // header names that column. Without times the header is empty, and the
        // preview leaves it out.
        var stages = transcript.Stages is { Count: > 0 } ? transcript.Stages : null;
        var result = new StringBuilder()
            .Append("# ").AppendLine(title)
            .AppendLine();
        result.AppendLine(stages is null ? "| | |" : "| | | Time taken |")
            .AppendLine(stages is null ? "|---|---|" : "|---|---|--:|");
        string? Took(string stage) => stages?.FirstOrDefault(timed => timed.Stage == stage)?.Duration();
        void Row(string label, string value, string? time = null)
        {
            result.Append("| ").Append(label).Append(" | ").Append(Cell(value));
            if (stages is not null) result.Append(" | ").Append(time);
            result.AppendLine(" |");
        }
        // Reading the audio has no row of its own; it is in the total, and in transcript.json.
        Row("Transcribed", FormatTime(transcript.CreatedAt.ToLocalTime()), Took(TranscriptStage.Total) is { } total ? $"{total} in all" : null);
        Row("Engine", $"{transcript.Engine} · {transcript.Model}", Took(TranscriptStage.Recognition));
        if (transcript.Echo is { } echo)
        {
            Row("Echo removal (audio)", echo.Describe(), Took(TranscriptStage.Echo));
            Row("Echo filter (text)", echo.TranscriptFilter ? "on" : "off");
        }
        // Speech levels and call voices have no setting row of their own, so they get
        // one only when they ran and there is a time to put beside them.
        if (Took(TranscriptStage.Levels) is { } levels)
            Row("Speech levels", "evened out", levels);
        if (Took(TranscriptStage.Diarization) is { } voices)
        {
            var count = transcript.Segments.Select(segment => segment.Speaker)
                .Where(speaker => speaker is not null && (speaker == "them" || speaker.StartsWith("them ", StringComparison.Ordinal)))
                .Distinct().Count();
            Row("Call voices", count == 1 ? "1 voice" : $"{count} voices", voices);
        }
        // Naming is a later pass than the total, and only a pass that asked a model is timed.
        var naming = Took(TranscriptStage.SpeakerNames);
        if (speakerNames is { Count: > 0 })
            Row("Speakers", string.Join(", ", speakerNames.Select(pair => $"{pair.Key} → {pair.Value}")), naming);
        else if (naming is not null)
            Row("Speakers", "none named", naming);
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
