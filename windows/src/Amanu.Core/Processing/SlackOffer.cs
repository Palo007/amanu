using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amanu.Core.Configuration;

namespace Amanu.Core.Processing;

public enum SlackOfferDecision
{
    /// <summary>Show the card now.</summary>
    Offer,
    /// <summary>Everything holds, but a recording is running: show it when that recording stops.</summary>
    WaitForRecording,
    No,
}

/// <summary>What the card says about the meeting, read from meta.json.</summary>
public sealed record SlackMeeting(DateTimeOffset StartedAt, int DurationSeconds, string? ProcessFamily);

/// <summary>The few lines of the summary the card shows before anything is copied.</summary>
public sealed record SlackPreview(string FirstLine, int ActionItems);

/// <summary>
/// The "summary ready" card for Slack huddles: when it comes up, and the record
/// that it came up. The rules are in docs/roadmap-slack-summary.md ("The card");
/// each one is here so that a test with a fake recording folder can hold it.
/// </summary>
public static partial class SlackOffer
{
    /// <summary>In the recording folder once the card was shown, so it never comes up twice.</summary>
    public const string FileName = "slack-offer.json";

    public static SlackOfferDecision Decide(string directory, SlackSummarySettings settings, bool recordingNow)
    {
        if (!settings.Enabled) return SlackOfferDecision.No;
        if (File.Exists(Path.Combine(directory, FileName))) return SlackOfferDecision.No;
        var summary = Path.Combine(directory, "summary.md");
        if (!File.Exists(summary)
            || File.Exists(Path.Combine(directory, "summary.stale"))
            || File.Exists(Path.Combine(directory, "summary.failed")))
            return SlackOfferDecision.No;
        if (ReadMeeting(directory) is not { } meeting) return SlackOfferDecision.No;
        if (!FromApp(meeting.ProcessFamily, settings.Apps)) return SlackOfferDecision.No;
        if (meeting.DurationSeconds < Math.Max(0, settings.MinMinutes) * 60) return SlackOfferDecision.No;
        // A new transcription of an old recording makes a new summary too; that
        // is not a huddle that just ended, and nobody wants it posted again.
        if (File.GetLastWriteTime(summary).Date != meeting.StartedAt.ToLocalTime().Date) return SlackOfferDecision.No;
        return recordingNow ? SlackOfferDecision.WaitForRecording : SlackOfferDecision.Offer;
    }

    /// <summary>A process name matches with or without ".exe", in any case.</summary>
    public static bool FromApp(string? processFamily, IEnumerable<string> apps)
    {
        if (string.IsNullOrWhiteSpace(processFamily)) return false;
        var name = Bare(processFamily);
        return apps.Any(app => string.Equals(Bare(app), name, StringComparison.OrdinalIgnoreCase));

        static string Bare(string value)
        {
            value = value.Trim();
            return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
        }
    }

    public static SlackMeeting? ReadMeeting(string directory)
    {
        var path = Path.Combine(directory, "meta.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            if (!root.TryGetProperty("started", out var started) || !started.TryGetDateTimeOffset(out var startedAt)) return null;
            var duration = root.TryGetProperty("duration_seconds", out var seconds) && seconds.TryGetInt32(out var value) ? value : 0;
            var family = root.TryGetProperty("process_family", out var process) && process.ValueKind == JsonValueKind.String
                ? process.GetString() : null;
            return new(startedAt, duration, family);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Written when the card comes up (<paramref name="copied"/> false) and again after a copy.</summary>
    public static void Record(string directory, bool copied, DateTimeOffset now)
    {
        var path = Path.Combine(directory, FileName);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["shown"] = true,
            ["copied"] = copied,
            ["time"] = now,
        }));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// The first line of text that is not a heading, and how many list items
    /// stand under a heading about actions or tasks — in the languages the
    /// template is used in.
    /// </summary>
    public static SlackPreview Preview(string markdown, int maxLength = 140)
    {
        string? first = null;
        var actions = 0;
        var underActions = false;
        foreach (var raw in Lines(markdown))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (Heading().Match(line) is { Success: true } heading)
            {
                underActions = ActionHeading().IsMatch(heading.Groups[2].Value);
                continue;
            }
            // A bold line on its own is the template's other kind of heading.
            if (BoldLine().Match(line) is { Success: true } bold)
            {
                underActions = ActionHeading().IsMatch(bold.Groups[1].Value);
                continue;
            }
            var item = ListItem().Match(raw);
            if (underActions && item.Success && item.Groups[1].Value.Length == 0) actions++;
            if (first is null)
            {
                var text = Plain(item.Success ? item.Groups[3].Value : line);
                if (text.Length > 0) first = text.Length > maxLength ? text[..(maxLength - 1)].TrimEnd() + "…" : text;
            }
        }
        return new(first ?? "", actions);
    }

    /// <summary>
    /// Plain text with Slack's own marks: <c>*bold*</c>, <c>_italic_</c>, and
    /// <c>•</c> for list items. It is what a paste gives where the rich text is
    /// not taken, and it reads correctly as plain text too.
    /// </summary>
    public static string SlackText(string markdown)
    {
        var output = new StringBuilder();
        var blank = 0;
        foreach (var raw in Lines(markdown))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0 || Rule().IsMatch(line))
            {
                if (output.Length > 0 && blank++ == 0) output.Append('\n');
                continue;
            }
            blank = 0;
            if (Heading().Match(line.Trim()) is { Success: true } heading)
            {
                output.Append('*').Append(Plain(heading.Groups[2].Value)).Append("*\n");
                continue;
            }
            if (ListItem().Match(line) is { Success: true } item)
            {
                var depth = item.Groups[1].Value.Replace("\t", "    ").Length / 2;
                var marker = item.Groups[2].Value;
                var bullet = char.IsDigit(marker[0]) ? marker : "•";
                var text = Checkbox().Replace(item.Groups[3].Value, "");
                output.Append(new string(' ', depth * 4)).Append(bullet).Append(' ').Append(Inline(text)).Append('\n');
                continue;
            }
            output.Append(Inline(line.Trim())).Append('\n');
        }
        return output.ToString().TrimEnd('\n') + "\n";
    }

    private static string Inline(string text)
    {
        text = Link().Replace(text, match => match.Groups[1].Value == match.Groups[2].Value
            ? match.Groups[2].Value : $"{match.Groups[1].Value} ({match.Groups[2].Value})");
        // Bold first, held as a control character, so the italic pass does not see its stars.
        text = Bold().Replace(text, "\u0001$2\u0001");
        text = StarItalic().Replace(text, "_$1_");
        return text.Replace('\u0001', '*');
    }

    /// <summary>The text with every mark removed, for the card.</summary>
    private static string Plain(string text)
    {
        text = Link().Replace(text, "$1");
        text = Bold().Replace(text, "$2");
        text = StarItalic().Replace(text, "$1");
        text = Checkbox().Replace(text, "");
        return text.Replace("`", "").Trim();
    }

    private static IEnumerable<string> Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\*\*([^*]+?):?\*\*:?$")]
    private static partial Regex BoldLine();

    [GeneratedRegex(@"^(\s*)([-*+•]|\d+[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^\[[ xX]\]\s*")]
    private static partial Regex Checkbox();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1")]
    private static partial Regex Bold();

    [GeneratedRegex(@"(?<![*\w])\*(?=\S)([^*]+?)(?<=\S)\*(?![*\w])")]
    private static partial Regex StarItalic();

    [GeneratedRegex(@"action|next step|to-?do|task|follow|úloh|ulohy|krok|úkol|задач|действ", RegexOptions.IgnoreCase)]
    private static partial Regex ActionHeading();
}

/// <summary>
/// The Windows clipboard's HTML format (CF_HTML): a header of byte offsets in
/// front of the HTML, which is what Slack and every browser read a rich paste from.
/// </summary>
public static class ClipboardHtml
{
    private const string StartMarker = "<!--StartFragment-->";
    private const string EndMarker = "<!--EndFragment-->";

    public static string Wrap(string fragment)
    {
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        var before = "<html><head><meta charset=\"utf-8\"></head><body>" + StartMarker;
        var after = EndMarker + "</body></html>";
        // The offsets are in UTF-8 bytes, and the header has the same length whatever the numbers.
        var headerLength = Encoding.UTF8.GetByteCount(string.Format(header, 0, 0, 0, 0));
        var startHtml = headerLength;
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(before);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(after);
        return string.Format(header, startHtml, endHtml, startFragment, endFragment) + before + fragment + after;
    }
}
