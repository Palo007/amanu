using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Amanu.Core.Processing;

/// <summary>One word of the list put in place of what the recognizer heard.</summary>
public sealed record CustomWordCorrection(int Segment, long StartMs, string Heard, string Word, int Distance);

/// <summary>
/// The user's list of names and company words, one entry per line, kept in the
/// app data folder (never in git). With an empty or missing list nothing here
/// does anything. Engines that take a word list get it; for the others,
/// <see cref="Correct"/> puts list entries in place of close-sounding words.
/// </summary>
public static class CustomWords
{
    public const string FileName = "custom-words.txt";
    public const int MaxEntries = 1000;
    public const int MaxWordsPerEntry = 5;
    public const int MaxCharactersPerEntry = 50;

    /// <summary>AMANU_CUSTOM_WORDS (a file path) overrides the app data folder, for measuring.</summary>
    public static string DefaultPath(string dataDirectory) =>
        Environment.GetEnvironmentVariable("AMANU_CUSTOM_WORDS") is { Length: > 0 } path
            ? Path.GetFullPath(path)
            : Path.Combine(dataDirectory, FileName);

    public static IReadOnlyList<string> Load(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllLines(path)) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Trims, drops empty lines, "#" comments, doubles, entries that are too long and characters the cloud services refuse.</summary>
    public static IReadOnlyList<string> Parse(IEnumerable<string> lines)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim().Trim('﻿').Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.IndexOfAny(['<', '>', '{', '}', '[', ']', '\\']) >= 0) continue;
            if (line.Length > MaxCharactersPerEntry) continue;
            if (line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > MaxWordsPerEntry) continue;
            if (!seen.Add(line)) continue;
            result.Add(line);
            if (result.Count >= MaxEntries) break;
        }
        return result;
    }

    /// <summary>Part of a cache key, so a changed list asks the cloud again. Empty for an empty list.</summary>
    public static string Digest(IReadOnlyList<string>? words) =>
        words is null or { Count: 0 }
            ? ""
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', words))))[..12];

    // ---- post-ASR correction ------------------------------------------------

    private static readonly Regex Token = new(@"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*", RegexOptions.Compiled);

    private static readonly string[] Endings =
        ["ovi", "och", "ami", "ach", "om", "ou", "mi", "ov", "em", "ím", "ám", "a", "u", "e", "i", "y", "í"];

    private sealed record Entry(string Text, string Letters, string Skeleton, int MaxDistance);

    private sealed record Candidate(int Start, int End, string Replacement, string Heard, int Distance);

    /// <summary>
    /// Conservative: a run of heard words is replaced only when it is as long as
    /// the entry (one word more or less), its letters are within one edit
    /// (two for entries of eight letters or more), it starts with the same
    /// letter and has the same consonant skeleton. Entries under five letters
    /// are never used. Segments keep their timing.
    /// </summary>
    public static (IReadOnlyList<TranscriptSegment> Segments, IReadOnlyList<CustomWordCorrection> Corrections) Correct(
        IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<string> words)
    {
        if (words.Count == 0) return (segments, []);
        var entries = words.Select(Prepare).Where(entry => entry.Letters.Length > 0).ToArray();
        var exact = entries.Select(entry => entry.Letters).ToHashSet();
        var corrections = new List<CustomWordCorrection>();
        var result = new TranscriptSegment[segments.Count];
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var (text, found) = CorrectText(segment.Text, entries, exact);
            result[index] = found.Count == 0 ? segment : segment with { Text = text };
            corrections.AddRange(found.Select(item => new CustomWordCorrection(index, segment.StartMs, item.Heard, item.Replacement, item.Distance)));
        }
        return (result, corrections);
    }

    private static (string Text, List<Candidate> Applied) CorrectText(string text, Entry[] entries, HashSet<string> exact)
    {
        var tokens = Token.Matches(text);
        var candidates = new List<Candidate>();
        if (tokens.Count == 0) return (text, candidates);
        foreach (var entry in entries)
        {
            var entryWords = entry.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            for (var length = Math.Max(1, entryWords - 1); length <= entryWords + 1; length++)
            {
                for (var first = 0; first + length <= tokens.Count; first++)
                {
                    var last = first + length - 1;
                    var start = tokens[first].Index;
                    var end = tokens[last].Index + tokens[last].Length;
                    var span = text[start..end];
                    // Never across a comma or a full stop: only blanks between the words.
                    if (length > 1 && !Regex.IsMatch(span, @"^[\p{L}\p{N}'’\s]+$")) continue;
                    // A run that holds a whole list entry already is right.
                    if (length > 1 && Enumerable.Range(first, length).Any(i => exact.Contains(Letters(tokens[i].Value)))) continue;
                    if (entry.Letters.Length < 5) continue;
                    // A heard word that is itself another list entry stays as it is.
                    if (length == 1 && exact.Contains(Letters(span)) && Letters(span) != entry.Letters) continue;
                    // One word may also carry a case ending (Slovak "-om", "-ou" ...) that is kept.
                    var variants = new List<(string Stem, string Tail)> { (span, "") };
                    if (length == 1)
                        foreach (var ending in Endings)
                            if (span.Length > ending.Length + 3 && span.EndsWith(ending, StringComparison.OrdinalIgnoreCase))
                                variants.Add((span[..^ending.Length], span[^ending.Length..]));
                    Candidate? best = null;
                    foreach (var (stem, tail) in variants)
                    {
                        var replacement = entry.Text + tail;
                        if (span == replacement) { best = null; break; }
                        var letters = Letters(stem);
                        if (letters.Length == 0) continue;
                        var distance = Distance(letters, entry.Letters);
                        if (distance > entry.MaxDistance) continue;
                        // Zero edits: the same letters in other capitals or spacing.
                        if (distance > 0 && (letters[0] != entry.Letters[0] || Skeleton(letters) != entry.Skeleton)) continue;
                        if (best is null || distance < best.Distance) best = new Candidate(start, end, replacement, span, distance);
                    }
                    if (best is not null) candidates.Add(best);
                }
            }
        }
        // Best first (fewest edits, then the longest run); no overlaps.
        var chosen = new List<Candidate>();
        foreach (var candidate in candidates.OrderBy(c => c.Distance).ThenByDescending(c => c.End - c.Start))
            if (chosen.All(other => candidate.End <= other.Start || candidate.Start >= other.End))
                chosen.Add(candidate);
        var builder = new StringBuilder(text);
        foreach (var candidate in chosen.OrderByDescending(c => c.Start))
            builder.Remove(candidate.Start, candidate.End - candidate.Start).Insert(candidate.Start, candidate.Replacement);
        return (builder.ToString(), chosen.OrderBy(c => c.Start).ToList());
    }

    private static Entry Prepare(string text)
    {
        var letters = Letters(text);
        return new Entry(text, letters, letters.Length == 0 ? "" : Skeleton(letters), letters.Length >= 8 ? 2 : 1);
    }

    private static string Letters(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(rune)) builder.Append(char.ToLowerInvariant(rune));
        return builder.ToString();
    }

    /// <summary>A rough consonant sound: spelling variants of one name give one skeleton.</summary>
    internal static string Skeleton(string letters)
    {
        var s = letters.Replace("ph", "f").Replace("ck", "k").Replace("wh", "w")
            .Replace("sch", "sk").Replace("ch", "#").Replace("sh", "#").Replace("x", "ks");
        var builder = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var next = i + 1 < s.Length ? s[i + 1] : '\0';
            c = c switch
            {
                'c' when next is 'e' or 'i' or 'y' => 's',
                'c' or 'q' => 'k',
                'z' => 's',
                'v' => 'f',
                'b' => 'p',
                'd' => 't',
                'g' => 'k',
                _ => c,
            };
            if (i > 0 && "aeiouyhw".IndexOf(c) >= 0) continue;
            if (builder.Length > 0 && builder[^1] == c) continue;
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>Edit distance with swaps of two neighbouring letters counted as one edit.</summary>
    internal static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        }
        return d[a.Length, b.Length];
    }
}
