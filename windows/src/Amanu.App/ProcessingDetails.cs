using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Amanu.Core.Processing;
using static Amanu.Core.Localization.Localized;

namespace Amanu.App;

/// <summary>
/// A fold-out under a progress bar with the last lines of the recording's
/// transcribe.log, so a pass that waits on a busy or stuck service says what it
/// is waiting for instead of only showing a moving bar. It reads the log only
/// while it is open, and again every two seconds.
/// </summary>
public sealed class ProcessingDetails
{
    private const int Lines = 14;
    private const int TailBytes = 16 * 1024;
    private readonly TextBlock text = new()
    {
        FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
    };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? directory;

    public ProcessingDetails()
    {
        text.SetResourceReference(TextBlock.ForegroundProperty, Ui.Secondary);
        View = new Expander
        {
            Header = T("What is happening", "Что происходит"),
            Content = new ScrollViewer { Content = text, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 4, 0, 0),
        };
        View.Expanded += (_, _) => { Refresh(); timer.Start(); };
        View.Collapsed += (_, _) => timer.Stop();
        timer.Tick += (_, _) => Refresh();
        View.Unloaded += (_, _) => timer.Stop();
        View.Loaded += (_, _) => { if (View.IsExpanded) timer.Start(); };
    }

    public Expander View { get; }

    /// <summary>Shown while a recording is queued, busy, waiting or failed; hidden once it is done.</summary>
    public void Show(ProcessingStatus? status)
    {
        var visible = status is { SessionDirectory: not null } shown && (shown.IsBusy || shown.Stage is "failed" or "deferred");
        directory = visible ? status!.SessionDirectory : null;
        View.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) timer.Stop();
        else if (View.IsExpanded) { Refresh(); timer.Start(); }
    }

    private void Refresh()
    {
        if (directory is null) return;
        text.Text = Format(Tail(Path.Combine(directory, "transcribe.log")))
            ?? T("Nothing written yet. The first step starts when the recording's turn comes.",
                 "Пока ничего. Первый шаг начнётся, когда подойдёт очередь записи.");
    }

    /// <summary>The last lines of the log, read without stopping the pass that is writing it.</summary>
    internal static IReadOnlyList<string> Tail(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (start > 0 && lines.Count > 0) lines.RemoveAt(0); // The first line was cut in the middle.
            return lines.TakeLast(Lines).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Each line with its UTC timestamp shown as local clock time, newest last.</summary>
    internal static string? Format(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return null;
        var output = new StringBuilder();
        foreach (var line in lines)
        {
            var space = line.IndexOf(' ');
            if (space > 0 && DateTimeOffset.TryParse(line[..space], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
                output.Append(at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append("  ").AppendLine(line[(space + 1)..]);
            else output.AppendLine(line);
        }
        return output.ToString().TrimEnd();
    }
}
