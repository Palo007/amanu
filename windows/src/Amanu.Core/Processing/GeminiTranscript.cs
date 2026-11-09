using System.Globalization;
using System.Text;
using System.Text.Json;
using static Amanu.Core.Localization.Localized;

namespace Amanu.Core.Processing;

/// <summary>
/// The parts of the Gemini engine that need no network: what it is asked, and
/// how the answer becomes segments. They live here so they are tested without
/// a key.
/// </summary>
public static class GeminiTranscript
{
    public const string Model = "gemini-3.8-flash";

    /// <summary>Asked once, after the first model has been busy four times in a row.</summary>
    public const string FallbackModel = "gemini-3.7-flash";

    /// <summary>
    /// Verbatim text only: a model that is allowed to tidy up will translate, summarize,
    /// or write a line for the silence between two sentences, and none of those is a transcript.
    /// </summary>
    public static string Prompt(IReadOnlyList<string> expectedLanguages, IReadOnlyList<string>? words)
    {
        var languages = expectedLanguages
            .Where(MeetingLanguages.Names.ContainsKey)
            .Select(code => $"{MeetingLanguages.Names[code]} ({code})")
            .ToArray();
        var text = new StringBuilder();
        text.AppendLine("Transcribe this audio verbatim.");
        text.AppendLine(languages.Length == 0
            ? "Write each phrase in the language it is spoken in."
            : $"The speech is in: {string.Join(", ", languages)}. Write each phrase in the language it is spoken in.");
        text.AppendLine("Do not translate, do not summarize, do not correct what was said, and write nothing for silence, music or noise.");
        text.AppendLine("Return only a JSON array of objects, in time order, each shaped like");
        text.AppendLine("{\"start\": \"mm:ss\", \"speaker\": \"A\", \"text\": \"...\"}");
        text.AppendLine("where start is counted from the start of this audio, and speaker is a letter (A, B, C, ...) that tells the different voices in this audio apart and stays the same for the same voice.");
        var terms = words?.Where(word => !string.IsNullOrWhiteSpace(word)).ToArray() ?? [];
        if (terms.Length > 0)
        {
            text.AppendLine("These words and names may be spoken; spell them exactly like this when you hear them:");
            text.AppendLine(string.Join(", ", terms));
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>The generateContent body for one piece of audio. The key goes in a header, never in here.</summary>
    public static string RequestBody(string prompt, byte[] wave) => JsonSerializer.Serialize(new
    {
        contents = new[]
        {
            new
            {
                parts = new object[]
                {
                    new { text = prompt },
                    new { inline_data = new { mime_type = "audio/wav", data = Convert.ToBase64String(wave) } },
                },
            },
        },
        generationConfig = new { temperature = 0, responseMimeType = "application/json" },
    });

    /// <summary>The model's text and the model that really answered, or null for the second when the response does not say.</summary>
    public static (string Text, string? ModelVersion) ReadResponse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var version = root.TryGetProperty("modelVersion", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var text = new StringBuilder();
            if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0
                && candidates[0].TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                    if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) text.Append(t.GetString());
            }
            return (text.ToString(), version);
        }
        catch (JsonException)
        {
            throw Malformed();
        }
    }

    /// <summary>
    /// Segments of one piece, in the track's own time. <paramref name="offsetMs"/> is where the piece
    /// file starts in the track and <paramref name="startMs"/>/<paramref name="endMs"/> the part of it
    /// that is this piece's own: a phrase that starts in the context on either side belongs to the
    /// neighbouring piece, as with a local chunk. Voice letters are only meaningful inside one piece,
    /// so two pieces can disagree about who "A" is; the call side then shows more voices than there
    /// were, which costs a label and no text.
    /// </summary>
    public static IReadOnlyList<TranscriptSegment> Segments(string text, string side, long offsetMs, long startMs, long endMs)
    {
        var json = text.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var open = json.IndexOf('\n');
            var close = json.LastIndexOf("```", StringComparison.Ordinal);
            if (open >= 0 && close > open) json = json[(open + 1)..close].Trim();
        }
        var items = new List<(long Start, string Voice, string Text)>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var array = document.RootElement;
            if (array.ValueKind == JsonValueKind.Object)
                array = array.EnumerateObject().FirstOrDefault(property => property.Value.ValueKind == JsonValueKind.Array).Value;
            if (array.ValueKind != JsonValueKind.Array) throw Malformed();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var words = item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()?.Trim() ?? "" : "";
                if (words.Length == 0) continue;
                if (!item.TryGetProperty("start", out var s) || !TryParseStart(s, out var seconds)) continue;
                var voice = item.TryGetProperty("speaker", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()?.Trim() ?? "" : "";
                items.Add(((long)Math.Round(seconds * 1000) + offsetMs, voice, words));
            }
        }
        catch (JsonException)
        {
            throw Malformed();
        }
        var kept = items.Where(item => item.Start >= startMs && item.Start < endMs).OrderBy(item => item.Start).ToArray();
        var result = new List<TranscriptSegment>(kept.Length);
        for (var i = 0; i < kept.Length; i++)
        {
            var end = i + 1 < kept.Length ? kept[i + 1].Start : endMs;
            result.Add(new TranscriptSegment(kept[i].Start, Math.Max(kept[i].Start, end), kept[i].Text, SpeakerLabels.Raw(side, kept[i].Voice)));
        }
        return result;
    }

    /// <summary>"mm:ss", "h:mm:ss" (the seconds may carry a fraction) or a number of seconds.</summary>
    public static bool TryParseStart(JsonElement value, out double seconds)
    {
        seconds = 0;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetDouble(out seconds) && seconds >= 0;
        if (value.ValueKind != JsonValueKind.String) return false;
        var parts = (value.GetString() ?? "").Trim().Split(':');
        if (parts.Length > 3) return false;
        var total = 0.0;
        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number < 0) return false;
            if (!last && number != Math.Floor(number)) return false;
            total = total * 60 + number;
        }
        seconds = total;
        return true;
    }

    private static ProcessingFailure Malformed() => new(FailureKind.Transient,
        T("Gemini's answer could not be read as a transcript.", "Ответ Gemini не удалось прочитать как расшифровку."));
}
