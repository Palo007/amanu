using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amanu.Core.Configuration;
using Amanu.Core.Processing;
using static Amanu.Core.Localization.Localized;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;

namespace Amanu.App;

/// <summary>
/// Every setting setup does not ask, drawn from <see cref="SettingsSchema"/>: the
/// window holds no list of its own, so the two cannot drift. Each row is a label,
/// its control, and one line saying what it does; an empty field shows in grey
/// what happens when nothing is set.
/// </summary>
/// <remarks>
/// A value equal to its default is cleared rather than written, and an emptied
/// field means "unset": config.json then reads as the list of the person's own
/// decisions, and a default that improves in a later version still reaches them.
/// </remarks>
internal sealed class AdvancedSettings
{
    private readonly AmanuRuntime runtime;
    private readonly List<Action> refreshers = [];
    private bool refreshing;

    public StackPanel View { get; } = new() { Margin = new Thickness(Ui.Gutter, 20, Ui.Gutter, 8) };

    public AdvancedSettings(AmanuRuntime runtime)
    {
        this.runtime = runtime;
        // The Setup tab's arrangement, a grey heading over a card of rows, so
        // that the two tabs read alike and a section's end can be seen.
        foreach (var section in SettingsSchema.AdvancedSections)
            View.Children.Add(Ui.Section(section.Title, Ui.Box([.. section.Entries.Select(Row)])));
        runtime.SettingsChanged += OnSettingsChanged;
        Refresh();
    }

    public void Detach() => runtime.SettingsChanged -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, EventArgs e) => View.Dispatcher.InvokeAsync(Refresh);

    private void Refresh()
    {
        refreshing = true;
        try { foreach (var refresh in refreshers) refresh(); }
        finally { refreshing = false; }
    }

    /// <summary>
    /// A switch on the left of its words, as in Setup; a menu or a number on the
    /// right; a field that holds a path, a URL or a list under its words, where it
    /// has the card's width. A row without a switch keeps the switch's space empty,
    /// so every title in a card starts at one edge.
    /// </summary>
    private Grid Row(SettingEntry entry)
    {
        var help = Ui.Detail(entry.NeedsRestart
            ? entry.Help + " " + T("Takes effect at the next launch.", "Подействует при следующем запуске.")
            : entry.Help);
        if (entry.Kind == SettingKind.Toggle) return Ui.Row(Toggle(entry), Ui.Title(entry.Label), help);

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Ui.Title(entry.Label));
        words.Children.Add(help);
        FrameworkElement? right = null;
        switch (entry.Kind)
        {
            case SettingKind.Choice:
                // The chosen option's own sentence, in place of help that walks through them all.
                var note = Ui.Detail("").Brush(TextBlock.ForegroundProperty, Ui.Primary);
                note.Margin = new Thickness(0, 4, 0, 0);
                words.Children.Add(note);
                right = Choice(entry, note);
                break;
            case SettingKind.Number:
                right = Text(entry);
                right.VerticalAlignment = VerticalAlignment.Top;
                break;
            default:
                // As wide as the card allows up to a limit, from the left edge: a
                // field left to size itself shrank to the grey of its placeholder.
                var control = Control(entry);
                control.HorizontalAlignment = HorizontalAlignment.Stretch;
                var holder = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                holder.ColumnDefinitions.Add(new ColumnDefinition { MaxWidth = entry.Kind is SettingKind.MultilineText or SettingKind.List ? 520 : 420 });
                holder.Children.Add(control);
                words.Children.Add(holder);
                break;
        }

        var grid = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Ui.Indent) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);
        if (right is not null)
        {
            right.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
        }
        return grid;
    }

    /// <summary>The controls that sit under their words.</summary>
    private FrameworkElement Control(SettingEntry entry) => entry.Kind switch
    {
        SettingKind.Command => Command(entry),
        SettingKind.File => File(entry),
        _ => Text(entry),
    };

    /// <summary>
    /// A path field and a button that opens the file, made first when it is
    /// missing, so the list can be started from here rather than from Explorer.
    /// </summary>
    private FrameworkElement File(SettingEntry entry)
    {
        var panel = new DockPanel();
        var open = Ui.Button(T("Open", "Открыть"), () =>
        {
            var path = ProcessingCoordinator.PeoplePath(runtime.Settings);
            try
            {
                if (!System.IO.File.Exists(path))
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                    System.IO.File.WriteAllText(path,
                        "# One person per line: full name first, then other names with commas.\n" +
                        "# Put (me) at the end of your own line.\n" +
                        "# Joseph Smith, Joe, Jojo\n");
                }
            }
            catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException) { }
            AmanuRuntime.Open(path);
        });
        open.Margin = new Thickness(8, 0, 0, 0);
        open.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(open, Dock.Right);
        panel.Children.Add(open);
        panel.Children.Add(Text(entry));
        return panel;
    }

    private CheckBox Toggle(SettingEntry entry)
    {
        var toggle = Ui.Switch(entry.Label);
        refreshers.Add(() => toggle.IsChecked = runtime.GetValue(entry.Path)?.GetValue<bool>() == true);
        toggle.Click += (_, _) => Commit(entry, JsonValue.Create(toggle.IsChecked == true));
        return toggle;
    }

    private FrameworkElement Choice(SettingEntry entry, TextBlock optionNote)
    {
        var combo = new ComboBox { MinWidth = 180, ItemsSource = entry.Options, VerticalAlignment = VerticalAlignment.Top };
        System.Windows.Automation.AutomationProperties.SetName(combo, entry.Label);
        refreshers.Add(() =>
        {
            var value = runtime.GetValue(entry.Path)?.ToString();
            combo.SelectedItem = value;
            optionNote.Text = value is not null && entry.OptionHelp?.GetValueOrDefault(value) is { } words ? $"{value}: {words}" : "";
            optionNote.Visibility = optionNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        combo.SelectionChanged += (_, _) =>
        {
            if (!refreshing && combo.SelectedItem is string value) Commit(entry, JsonValue.Create(value));
        };
        return combo;
    }

    private FrameworkElement Text(SettingEntry entry)
    {
        var multiline = entry.Kind is SettingKind.MultilineText or SettingKind.List;
        var (view, box) = Ui.Field(Placeholder(entry), entry.Kind == SettingKind.Number ? 140 : double.NaN, multiline);
        // Wide enough for a path or a URL, never wider than the column: a fixed
        // width ran past the window's right edge at 150% scaling.
        if (entry.Kind != SettingKind.Number) view.MaxWidth = multiline ? 520 : 420;
        if (entry.Kind == SettingKind.List) box.MinHeight = 90;
        System.Windows.Automation.AutomationProperties.SetName(box, entry.Label);
        refreshers.Add(() =>
        {
            if (box.IsKeyboardFocusWithin) return;
            var value = runtime.GetValue(entry.Path);
            box.Text = Display(entry, value);
        });
        void Leave()
        {
            if (refreshing) return;
            var stored = Display(entry, runtime.GetValue(entry.Path));
            if (box.Text == stored) return;
            switch (Resolve(entry, box.Text))
            {
                case (true, JsonValue { } server) when KeyRouting.ServerSettings.Contains(entry.Path)
                                                     && !KeyRouting.AcceptableServer(server.ToString()):
                    SetupForm.RefuseServer(Ui.OwnerOf(View));
                    box.Text = stored;
                    break;
                case (true, var value):
                    Commit(entry, value);
                    break;
                default:
                    box.Text = stored;
                    break;
            }
        }
        box.LostKeyboardFocus += (_, _) => Leave();
        if (!multiline) box.KeyDown += (_, args) => { if (args.Key == Key.Enter) Leave(); };
        return view;
    }

    /// <summary>The Windows shape of <c>on_stop</c>: a program, and its arguments one per line.</summary>
    private FrameworkElement Command(SettingEntry entry)
    {
        var (programView, program) = Ui.Field(T("program, e.g. C:\\Tools\\sync.exe", "программа, например C:\\Tools\\sync.exe"));
        var (argumentsView, arguments) = Ui.Field(T("arguments, one per line — {session} is the folder", "аргументы по строке — {session} это папка"), multiline: true);
        arguments.MinHeight = 70;
        argumentsView.Margin = new Thickness(0, 6, 0, 0);
        var stack = new StackPanel { MaxWidth = 420 };
        stack.Children.Add(programView);
        stack.Children.Add(argumentsView);
        refreshers.Add(() =>
        {
            if (stack.IsKeyboardFocusWithin) return;
            var hook = runtime.Settings.OnStop;
            program.Text = hook?.Executable ?? "";
            arguments.Text = string.Join(Environment.NewLine, hook?.Arguments ?? []);
        });
        stack.LostKeyboardFocus += (_, args) =>
        {
            if (refreshing || stack.IsKeyboardFocusWithin) return;
            var executable = program.Text.Trim();
            var value = executable.Length == 0 ? null : JsonSerializer.SerializeToNode(new CommandHook
            {
                Executable = executable,
                Arguments = arguments.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            }, AppSettings.JsonOptions);
            if (JsonNode.DeepEquals(value, runtime.GetValue(entry.Path))) return;
            Commit(entry, value);
        };
        return stack;
    }

    private string Placeholder(SettingEntry entry) => runtime.DescribeDefault(entry);

    /// <summary>What the field shows: nothing when the value is the default, so the grey default can be read.</summary>
    private string Display(SettingEntry entry, JsonNode? value)
    {
        var fallback = runtime.DefaultFor(entry.Path);
        if (value is null || JsonNode.DeepEquals(value, fallback)) return "";
        return entry.Kind == SettingKind.List && value is JsonArray list
            ? string.Join(Environment.NewLine, list.Select(item => item?.ToString()))
            : value.ToString();
    }

    /// <summary>
    /// A control's contents in the schema's terms: a value to write, null to clear,
    /// or unusable — letters in a number field — which changes nothing.
    /// </summary>
    private static (bool Usable, JsonNode? Value) Resolve(SettingEntry entry, string raw)
    {
        var text = raw.Trim();
        switch (entry.Kind)
        {
            case SettingKind.Number:
                if (text.Length == 0) return (true, null);
                // A decimal setting takes "0,6" too: the comma is the decimal
                // mark in most of the languages its users type in.
                if (entry.Decimal)
                    return double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out var share) && share is >= 0 and <= 1
                        ? (true, JsonValue.Create(share))
                        : (false, null);
                return int.TryParse(text, out var number) && number >= 0 ? (true, JsonValue.Create(number)) : (false, null);
            case SettingKind.List:
                var items = Lines(raw);
                return (true, items.Count == 0 ? null : new JsonArray([.. items.Select(item => (JsonNode)JsonValue.Create(item)!)]));
            default:
                return (true, text.Length == 0 ? null : JsonValue.Create(text));
        }
    }

    private static List<string> Lines(string text) =>
        text.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private void Commit(SettingEntry entry, JsonNode? value)
    {
        if (!Ui.TryUpdate(Ui.OwnerOf(View), () => runtime.SetValue(entry.Path, value))) Refresh();
    }
}
