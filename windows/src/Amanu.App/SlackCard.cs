using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Amanu.Core.Processing;
using Markdig;
using static Amanu.Core.Localization.Localized;

namespace Amanu.App;

/// <summary>
/// Puts a recording's summary on the clipboard for a Slack message: as HTML,
/// which Slack's message box turns into bold text and lists, and as plain text
/// with Slack's own marks for anything that takes only text.
/// </summary>
internal static class SlackClipboard
{
    public static async Task<bool> CopyAsync(string directory)
    {
        string markdown;
        try { markdown = await File.ReadAllTextAsync(Path.Combine(directory, "summary.md")); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
        var pipeline = new Markdig.MarkdownPipelineBuilder().UseEmphasisExtras().UseTaskLists().UsePipeTables().Build();
        var data = new DataObject();
        data.SetData(DataFormats.Html, ClipboardHtml.Wrap(Markdig.Markdown.ToHtml(markdown, pipeline)));
        data.SetData(DataFormats.UnicodeText, SlackOffer.SlackText(markdown));
        // Another program can hold the clipboard for a moment; a second try is usually enough.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (COMException) when (attempt < 3)
            {
                await Task.Delay(100);
            }
            catch (COMException)
            {
                return false;
            }
        }
    }

    /// <summary>Brings Slack to the front, or does nothing where Slack is not installed.</summary>
    public static void OpenSlack()
    {
        try { Process.Start(new ProcessStartInfo("slack://open") { UseShellExecute = true }); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}

/// <summary>
/// The "summary ready" card: a small window above the tray that does not take
/// the focus, so it does not interrupt typing, and stays until it is clicked.
/// </summary>
internal sealed class SlackCard : Window
{
    private static SlackCard? shown;
    private readonly string directory;
    private readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock done = Ui.Detail("");

    public static async Task ShowAsync(string directory)
    {
        // One card at a time: a newer summary replaces an older card nobody answered.
        shown?.Close();
        var card = new SlackCard(directory);
        await card.FillAsync();
        shown = card;
        card.Closed += (_, _) => { if (shown == card) shown = null; };
        card.Show();
        card.PlaceAboveTray();
    }

    private readonly TextBlock when = Ui.Detail("");
    private readonly TextBlock people = Ui.Detail("");
    private readonly TextBlock preview = Ui.Title("");
    private readonly TextBlock actions = Ui.Detail("");

    private SlackCard(string directory)
    {
        this.directory = directory;
        Title = T("Amanu · Slack huddle summary ready", "Amanu · саммари созвона Slack готово");
        Icon = App.WindowIcon;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;

        var heading = Ui.Title(Title);
        heading.FontWeight = FontWeights.SemiBold;
        var close = Ui.Button("×", Close);
        close.Padding = new Thickness(8, 0, 8, 2);
        close.ToolTip = T("Not now", "Не сейчас");
        DockPanel.SetDock(close, Dock.Right);
        var top = new DockPanel();
        top.Children.Add(close);
        top.Children.Add(heading);

        var copy = Ui.Button(T("Copy and open Slack", "Скопировать и открыть Slack"), () => _ = CopyAsync(), accent: true);
        var open = Ui.Button(T("Open summary", "Открыть саммари"), () =>
        {
            App.ShowRecording(directory);
            Close();
        });
        var later = Ui.Button(T("Not now", "Не сейчас"), Close);
        open.Margin = later.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(copy);
        buttons.Children.Add(open);
        buttons.Children.Add(later);
        done.Visibility = Visibility.Collapsed;
        done.Margin = new Thickness(0, 12, 0, 0);

        preview.Margin = new Thickness(0, 4, 0, 0);
        var stack = new StackPanel { Margin = new Thickness(16, 10, 10, 14) };
        stack.Children.Add(top);
        stack.Children.Add(when);
        stack.Children.Add(people);
        stack.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 8, 6, 6) }.Brush(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush"));
        stack.Children.Add(preview);
        stack.Children.Add(actions);
        stack.Children.Add(buttons);
        stack.Children.Add(done);
        var frame = new Border { BorderThickness = new Thickness(1), Child = stack }
            .Brush(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush")
            .Brush(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
        Content = frame;
    }

    private async Task FillAsync()
    {
        if (SlackOffer.ReadMeeting(directory) is { } meeting)
        {
            var start = meeting.StartedAt.ToLocalTime();
            var end = start.AddSeconds(meeting.DurationSeconds);
            var day = start.Date == DateTime.Today ? T("Today", "Сегодня") : start.ToString("d");
            when.Text = $"{day} {start:HH:mm}–{end:HH:mm} · {Math.Max(1, (int)Math.Round(meeting.DurationSeconds / 60.0))} {T("min", "мин")}";
        }
        var names = (await SpeakerFile.ReadAsync(directory, CancellationToken.None)).Names.Values
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        people.Text = string.Join(", ", names);
        people.Visibility = names.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        try
        {
            var summary = SlackOffer.Preview(await File.ReadAllTextAsync(Path.Combine(directory, "summary.md")));
            preview.Text = summary.FirstLine;
            actions.Text = summary.ActionItems switch
            {
                0 => "",
                1 => T("1 action item", "1 задача"),
                var count => T($"{count} action items", $"Задач: {count}"),
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        actions.Visibility = actions.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task CopyAsync()
    {
        if (!await SlackClipboard.CopyAsync(directory))
        {
            done.Text = T("Couldn’t copy the summary. Try again, or use Open summary.",
                "Не удалось скопировать саммари. Попробуйте ещё раз или откройте саммари.");
            done.Visibility = Visibility.Visible;
            return;
        }
        try { SlackOffer.Record(directory, copied: true, DateTimeOffset.Now); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        SlackClipboard.OpenSlack();
        buttons.Visibility = Visibility.Collapsed;
        done.Text = T("Copied. In Slack, open the channel and press Ctrl+V.", "Скопировано. В Slack откройте канал и нажмите Ctrl+V.");
        done.Visibility = Visibility.Visible;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        timer.Start();
    }

    /// <summary>Bottom right of the work area, which is above the taskbar wherever the taskbar is.</summary>
    private void PlaceAboveTray()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
    }
}
