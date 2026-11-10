using System.Globalization;

namespace Amanu.Core.Processing;

/// <summary>
/// Keeps the finished live caption lines beside the recording as
/// live-transcript.md, so the live engine's words can be compared with the final
/// transcript afterwards. Only lines the engine has made final are written,
/// once each, as they arrive, so a crash keeps everything said before it.
/// </summary>
public static class LiveTranscriptFile
{
    public const string FileName = "live-transcript.md";
    private static readonly object Sync = new();

    /// <param name="atMs">When the line began, from the start of the recording.</param>
    /// <param name="engine">Named in the heading when the file is first written.</param>
    public static void Append(string sessionDirectory, long atMs, string speaker, string text, string engine)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        var path = Path.Combine(sessionDirectory, FileName);
        var at = TimeSpan.FromMilliseconds(Math.Max(0, atMs));
        var stamp = at.TotalHours >= 1
            ? at.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : at.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        lock (Sync)
        {
            if (!File.Exists(path))
                File.WriteAllText(path, $"# Live transcript\n\nCaptions shown during the meeting by {engine}. The final transcript is made separately after the meeting.\n\n");
            File.AppendAllText(path, $"[{stamp}] **{speaker}:** {text}\n\n");
        }
    }
}
