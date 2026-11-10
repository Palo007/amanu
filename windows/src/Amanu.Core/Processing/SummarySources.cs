using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Amanu.Core.Processing;

/// <summary>
/// Chooses what a summary is written from — the final transcript, the live
/// captions in live-transcript.md, or both — and, for both, cuts the two into
/// pieces that cover the same stretch of the meeting, so a long meeting that
/// needs several requests never sends the end of one beside the start of the
/// other.
/// </summary>
public static partial class SummarySources
{
    /// <summary>Added to the summary instructions when both transcripts are sent.</summary>
    public const string BothInstructions = """
        You are given two transcripts of the same meeting, made from the same audio by two different speech engines.
        "Final transcript" was made from the recording after the meeting. Its speaker labels and names are the more reliable ones.
        "Live transcript" was captioned while the meeting was going on. It often hears words, names and numbers better, but it only knows "me" and "them" as speakers, and it may miss or split lines.
        Both count time from the start of the recording, so lines with similar timestamps are the same moment.
        Treat them as two witnesses to one meeting: where they disagree, take the reading that makes sense in context; take names, numbers and technical terms from whichever transcript has them clearly; take who said what from the final transcript.
        Write one note about the meeting. Do not repeat anything because it appears in both, and do not mention that there were two transcripts.
        """;

    /// <returns>
    /// The transcript to summarize, and the live transcript to send beside it
    /// (null unless both are to be sent). Live captions with no lines in them
    /// count as none, and the final transcript is used.
    /// </returns>
    public static (string Transcript, string? Live) Choose(string? source, string transcript, string? live)
    {
        if (live is null || !TimedLine().IsMatch(live)) return (transcript, null);
        return source?.Trim().ToLowerInvariant() switch
        {
            "live" => (live, null),
            "both" => (transcript, live),
            _ => (transcript, null),
        };
    }

    /// <summary>
    /// Splits the final transcript as <see cref="SummaryChunker"/> does and gives
    /// each piece the live lines from the same stretch of time.
    /// </summary>
    public static IReadOnlyList<(string Final, string Live)> Pair(string final, string live, int maximumCharacters = 60_000)
    {
        var finalChunks = SummaryChunker.Split(final, maximumCharacters);
        if (finalChunks.Count == 1) return [(final, live)];
        // Where each later piece of the final transcript begins; a piece with no
        // timestamp of its own begins where the one before it did.
        var starts = new long[finalChunks.Count];
        for (var index = 1; index < finalChunks.Count; index++)
            starts[index] = FirstTime(finalChunks[index]) ?? starts[index - 1];
        var pieces = Enumerable.Range(0, finalChunks.Count).Select(_ => new StringBuilder()).ToArray();
        var time = -1L;
        foreach (var line in live.Split('\n'))
        {
            if (Time(line) is { } at) time = at;
            var piece = 0;
            while (piece + 1 < starts.Length && time >= starts[piece + 1]) piece++;
            pieces[piece].Append(line).Append('\n');
        }
        return finalChunks.Select((chunk, index) => (chunk, pieces[index].ToString())).ToList();
    }

    private static long? FirstTime(string text)
    {
        foreach (var line in text.Split('\n'))
            if (Time(line) is { } at) return at;
        return null;
    }

    /// <summary>The time a line of either transcript begins at, in milliseconds.</summary>
    public static long? Time(string line)
    {
        var match = TimedLine().Match(line);
        if (!match.Success) return null;
        var hours = match.Groups["h"].Success ? int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) : 0;
        var minutes = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        return ((hours * 60L + minutes) * 60 + seconds) * 1000;
    }

    // transcript.md writes "**[1:05] speaker:**", live-transcript.md "[01:05] **me:**".
    [GeneratedRegex(@"^\**\[(?:(?<h>\d+):)?(?<m>\d+):(?<s>\d{2})\]", RegexOptions.Multiline)]
    private static partial Regex TimedLine();
}
