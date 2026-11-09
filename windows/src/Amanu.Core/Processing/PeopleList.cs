using System.Text;
using System.Text.RegularExpressions;

namespace Amanu.Core.Processing;

/// <summary>One person who may be on a call: the full name, the other names they are called by, and whether it is the user.</summary>
public sealed record Person(string FullName, IReadOnlyList<string> OtherNames, bool IsMe);

/// <summary>
/// people.txt: who may be on the user's calls. One person per line, full name
/// first, then other names with commas ("Radoslav Benes, Rado, Radko"); "(me)"
/// at the end of a line marks the user. It is the format amanu-gemini reads, so
/// one file serves both. The list is kept in the app data folder, never in git,
/// and a missing or empty list changes nothing.
/// </summary>
public static partial class PeopleList
{
    public const string FileName = "people.txt";
    public const int MaxPeople = 500;

    [GeneratedRegex(@"\s*\(me\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex MeMarker();

    /// <summary>The setting when it names a file, otherwise people.txt in the app data folder.</summary>
    public static string PathFor(string? setting, string dataDirectory) =>
        string.IsNullOrWhiteSpace(setting)
            ? Path.Combine(dataDirectory, FileName)
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(setting.Trim()));

    public static IReadOnlyList<Person> Load(string path)
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

    /// <summary>Drops empty lines, "#" comments and a second line for the same full name.</summary>
    public static IReadOnlyList<Person> Parse(IEnumerable<string> lines)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Person>();
        foreach (var raw in lines)
        {
            var line = raw.Trim().Trim('﻿').Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var text = MeMarker().Replace(line, "");
            var names = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (names.Length == 0 || !seen.Add(names[0])) continue;
            var others = names.Skip(1).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(name => !name.Equals(names[0], StringComparison.OrdinalIgnoreCase)).ToArray();
            result.Add(new Person(names[0], others, text.Length != line.Length));
            if (result.Count >= MaxPeople) break;
        }
        return result;
    }

    /// <summary>The user's own full name, from the line marked "(me)".</summary>
    public static string? Me(IReadOnlyList<Person> people) => people.FirstOrDefault(person => person.IsMe)?.FullName;

    /// <summary>
    /// The full name for a name the model gave: the same full name, or the one
    /// person who is called by it. A name two people share stays as it was given,
    /// because picking one of them would be a guess.
    /// </summary>
    public static string FullNameFor(string name, IReadOnlyList<Person> people)
    {
        var trimmed = name.Trim();
        if (people.FirstOrDefault(person => person.FullName.Equals(trimmed, StringComparison.OrdinalIgnoreCase)) is { } exact)
            return exact.FullName;
        var matches = people.Where(person => person.OtherNames.Contains(trimmed, StringComparer.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0].FullName : trimmed;
    }

    /// <summary>The list as prompt lines: "Full Name (also called: A, B)". Empty for an empty list.</summary>
    public static string Describe(IReadOnlyList<Person> people)
    {
        var text = new StringBuilder();
        foreach (var person in people)
        {
            text.Append("- ").Append(person.FullName);
            if (person.OtherNames.Count > 0) text.Append(" (also called: ").Append(string.Join(", ", person.OtherNames)).Append(')');
            if (person.IsMe) text.Append(" — the person recording");
            text.Append('\n');
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>The full names, for the custom-words list that steers how the transcription spells them.</summary>
    public static IReadOnlyList<string> FullNames(IReadOnlyList<Person> people) => people.Select(person => person.FullName).ToArray();
}
