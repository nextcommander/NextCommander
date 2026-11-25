using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;

namespace NextCommander.App.Views.Dialogs;

/// <summary>A recorded macro: its name, its command (uc_...), the commands it runs in order.</summary>
public sealed record MacroInfo(string Name, string Command, IReadOnlyList<string> Steps);

/// <summary>What the macros window works with (set by the main window).</summary>
/// <param name="Macros">The macros, by name.</param>
/// <param name="Run">Runs a macro by its command (the window is closed first).</param>
/// <param name="Delete">Deletes a macro with its keys and buttons (asked in the window already).</param>
public sealed record MacrosRequest(IReadOnlyList<MacroInfo> Macros, Func<string, Task> Run, Action<string> Delete);

/// <summary>
/// Tools > Macros > Macros... (nc_Macros; the user's template, 25 November 2026): a search over names, commands and
/// steps, the macros on the left, the selected one on the right (its command to copy, its steps in order); Delete asks
/// in the window (Keep / Delete), Enter runs the macro.
/// </summary>
public sealed class MacrosDialog : Window
{
    private const string CodeIcon = "M5 3.5L1.75 8 5 12.5M11 3.5L14.25 8 11 12.5M9.25 2.5l-2.5 11";

    private sealed record Line(MacroInfo Macro, IReadOnlyList<int>? InName, IReadOnlyList<int>? InCommand);

    private readonly MacrosRequest _request;
    private readonly List<MacroInfo> _macros;
    private readonly TextBox _search = new() { PlaceholderText = "Search macros by name or command…", MinHeight = 40, VerticalContentAlignment = VerticalAlignment.Center, FontSize = 15 };
    private readonly TextBlock _count = new() { FontSize = 12, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly StackPanel _list = new() { Margin = new Thickness(8), Spacing = 2 };
    private readonly ScrollViewer _detailScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock _hints = new() { Text = "↑↓ move · Del delete · Esc close", FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _delete = new() { Content = "Delete", Classes = { "dialog" } };
    private readonly Button _run = new() { Content = "Run", IsDefault = true, Classes = { "dialog" } };
    private readonly List<Border> _rows = [];
    private readonly IBrush _macroBrush;
    private readonly IBrush _macroSoft;
    private List<Line> _shown = [];
    private int _selected;
    private bool _confirming;
    private string _searched = string.Empty;

    public MacrosDialog(MacrosRequest request)
    {
        _request = request;
        _macros = [.. request.Macros];
        var dark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        _macroBrush = new SolidColorBrush(Color.Parse(dark ? "#B48CFF" : "#7A4FD6"));
        _macroSoft = new SolidColorBrush(Color.Parse(dark ? "#24B48CFF" : "#1A7A4FD6"));
        Title = "Macros";
        Width = 760;
        Height = 560;
        MinWidth = 520;
        MinHeight = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _search.InnerLeftContent = new Border { Child = DialogLook.Glyph("MiSearch", 16), Margin = new Thickness(12, 0, 4, 0), Opacity = 0.6 };
        _search.InnerRightContent = _count;
        _search.TextChanged += (_, _) => Search(_search.Text ?? string.Empty, fromBox: true);
        var search = new Border { Child = _search, Margin = new Thickness(16, 14, 16, 12) };

        var listScroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _list };
        var listBorder = new Border { Child = listScroll, BorderThickness = new Thickness(0, 0, 1, 0) };
        listBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        var detail = new Border { Child = _detailScroll };
        detail[!Border.BackgroundProperty] = new DynamicResourceExtension("FmPanelHeaderBrush");
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,1.1*") };
        Grid.SetColumn(detail, 1);
        body.Children.AddRange([listBorder, detail]);
        var bodyBorder = new Border { Child = body, BorderThickness = new Thickness(0, 1, 0, 0) };
        bodyBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");

        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "dialog" } };
        cancel.Click += (_, _) => Close();
        _delete.Foreground = new SolidColorBrush(Color.Parse(dark ? "#FF6B6B" : "#C4302B"));
        _delete.Click += (_, _) => AskDelete();
        _run.Click += async (_, _) => await RunAsync();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.AddRange([cancel, _delete, _run]);
        var foot = new DockPanel { Margin = new Thickness(16, 12) };
        DockPanel.SetDock(buttons, Dock.Right);
        foot.Children.AddRange([buttons, _hints]);
        var footBorder = new Border { Child = foot, BorderThickness = new Thickness(0, 1, 0, 0) };
        footBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");

        var root = new DockPanel();
        DockPanel.SetDock(search, Dock.Top);
        DockPanel.SetDock(footBorder, Dock.Bottom);
        root.Children.AddRange([search, footBorder, bodyBorder]);
        Content = root;

        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) => _search.Focus();
        Render();
    }

    /// <summary>The macros shown, in order (their commands; tests).</summary>
    public IReadOnlyList<string> Shown => [.. _shown.Select(l => l.Macro.Command)];

    /// <summary>The count beside the search (tests).</summary>
    public string CountText => _count.Text ?? string.Empty;

    /// <summary>The question of Delete is shown (tests).</summary>
    public bool IsConfirming => _confirming;

    public void Search(string text) => Search(text, fromBox: false);

    private void Search(string text, bool fromBox)
    {
        if (!fromBox)
        {
            _search.Text = text;
        }
        if (text == _searched)
        {
            return; // TextChanged comes later for a text set here: nothing new
        }
        _searched = text;
        _selected = 0;
        _confirming = false;
        Render();
    }

    /// <summary>Selects a line (tests; a click).</summary>
    public void Select(int index)
    {
        _selected = Math.Clamp(index, 0, Math.Max(0, _shown.Count - 1));
        _confirming = false;
        Render();
    }

    // ---------------------------------------------------------------- drawing

    private Control CodeGlyph(double size) => new Avalonia.Controls.Shapes.Path
    {
        Data = StreamGeometry.Parse(CodeIcon), Stroke = _macroBrush, StrokeThickness = 1.5, StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round, Width = size, Height = size, Stretch = Stretch.Uniform,
    };

    private Border Tile(double size, double glyph, double radius) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(radius), Background = _macroSoft,
        Child = CodeGlyph(glyph), VerticalAlignment = VerticalAlignment.Center,
    };

    private static string Steps(int n) => n == 1 ? "1 step" : $"{n} steps";

    private void Render()
    {
        var query = new string([.. (_search.Text ?? string.Empty).Where(c => !char.IsWhiteSpace(c))]);
        _shown = [];
        foreach (var macro in _macros)
        {
            if (query.Length == 0)
            {
                _shown.Add(new Line(macro, null, null));
                continue;
            }
            var inName = FrequentFoldersDialog.Match(query, macro.Name)?.Found;
            var inCommand = FrequentFoldersDialog.Match(query, macro.Command)?.Found;
            if (inName is not null || inCommand is not null || macro.Steps.Any(s => FrequentFoldersDialog.Match(query, s) is not null))
            {
                _shown.Add(new Line(macro, inName, inCommand));
            }
        }
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _shown.Count - 1));
        _count.Text = query.Length > 0 ? $"{_shown.Count} of {_macros.Count}" : _macros.Count == 1 ? "1 macro" : $"{_macros.Count} macros";

        _list.Children.Clear();
        _rows.Clear();
        for (var i = 0; i < _shown.Count; i++)
        {
            var row = LineView(_shown[i], i);
            _rows.Add(row);
            _list.Children.Add(row);
        }
        if (_shown.Count == 0)
        {
            _list.Children.Add(Empty(_macros.Count > 0 ? "No macros match" : "No macros yet",
                _macros.Count > 0 ? "Try another name or command." : "Recorded macros appear here."));
        }
        var current = _shown.Count > 0 ? _shown[_selected].Macro : null;
        _run.IsEnabled = _delete.IsEnabled = current is not null;
        _detailScroll.Content = current is null ? Empty("No macro selected", "Pick a macro to see what it runs.") : Detail(current);
        if (_selected < _rows.Count)
        {
            _rows[_selected].BringIntoView();
        }
    }

    private static StackPanel Empty(string title, string text)
    {
        var empty = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(12, 40, 12, 0) };
        empty.Children.AddRange([
            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = text, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
        ]);
        return empty;
    }

    private static TextBlock Marked(string text, IReadOnlyList<int>? found, double size, FontWeight weight, bool mono)
    {
        var block = new TextBlock { FontSize = size, FontWeight = weight, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
        if (mono)
        {
            block.FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace");
            block.Opacity = 0.7;
        }
        if (found is null)
        {
            block.Text = text;
            return block;
        }
        var marks = found.ToHashSet();
        for (var i = 0; i < text.Length; i++)
        {
            var run = new Run(text[i].ToString());
            if (marks.Contains(i))
            {
                run.FontWeight = FontWeight.Bold;
                run[!TextElement.ForegroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
            }
            block.Inlines!.Add(run);
        }
        return block;
    }

    private Border LineView(Line line, int index)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.AddRange([Marked(line.Macro.Name, line.InName, 14, FontWeight.SemiBold, mono: false), Marked(line.Macro.Command, line.InCommand, 12, FontWeight.Normal, mono: true)]);
        var count = new TextBlock { Text = Steps(line.Macro.Steps.Count), FontSize = 11.5, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(count, 2);
        grid.Children.AddRange([Tile(32, 17, 8), text, count]);
        var view = new Border
        {
            Child = grid, Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
        };
        if (index == _selected)
        {
            view[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
            view[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightListLowBrush");
        }
        view.PointerPressed += (_, e) =>
        {
            if (e.ClickCount >= 2)
            {
                _ = RunAsync();
            }
            else if (_selected != index || _confirming)
            {
                Select(index);
            }
            _search.Focus();
        };
        return view;
    }

    private static TextBlock Label(string text) =>
        new() { Text = text, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Opacity = 0.55 };

    private StackPanel Detail(MacroInfo macro)
    {
        var panel = new StackPanel { Spacing = 18, Margin = new Thickness(20, 18) };

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        head.Children.AddRange([Tile(42, 22, 10), new TextBlock { Text = macro.Name, FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } }]);
        panel.Children.Add(head);

        var id = new TextBlock
        {
            Text = macro.Command, FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"), FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" },
        };
        var copy = DialogLook.IconButton("MiCopy", "Copy command ID", async () =>
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(macro.Command);
            }
        });
        copy.Width = 28;
        copy.Height = 28;
        var idGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(copy, 1);
        idGrid.Children.AddRange([id, copy]);
        var idRow = new Border { Child = idGrid, Height = 36, Padding = new Thickness(10, 0, 4, 0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        idRow[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        idRow[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        var idField = new StackPanel { Spacing = 6 };
        idField.Children.AddRange([
            Label("COMMAND ID"), idRow,
            new TextBlock { Text = "Use this ID to bind the macro to a key or a toolbar button.", FontSize = 12, Opacity = 0.55, TextWrapping = TextWrapping.Wrap },
        ]);
        panel.Children.Add(idField);

        var steps = new StackPanel { Spacing = 6 };
        steps.Children.Add(Label(macro.Steps.Count == 1 ? "RUNS 1 COMMAND" : $"RUNS {macro.Steps.Count} COMMANDS IN ORDER"));
        for (var i = 0; i < macro.Steps.Count; i++)
        {
            var number = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 11.5, FontWeight = FontWeight.SemiBold, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } },
            };
            number[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            var step = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            step.Children.AddRange([number, new TextBlock { Text = macro.Steps[i], FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } }]);
            var box = new Border { Child = step, Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
            box[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
            box[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            steps.Children.Add(box);
        }
        panel.Children.Add(steps);

        if (_confirming)
        {
            var bad = _delete.Foreground ?? Brushes.Red;
            var question = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Text = $"Delete {macro.Name}? This can't be undone." };
            var keep = new Button { Content = "Keep", Classes = { "dialog" } };
            keep.Click += (_, _) =>
            {
                _confirming = false;
                Render();
                _search.Focus();
            };
            var really = new Button { Content = "Delete", Background = bad, Foreground = Brushes.White, Classes = { "dialog" } };
            really.Click += (_, _) => ConfirmDelete();
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
            Grid.SetColumn(keep, 1);
            Grid.SetColumn(really, 2);
            row.Children.AddRange([question, keep, really]);
            var confirm = new Border { Child = row, Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = bad };
            confirm.Background = bad is ISolidColorBrush solid ? new SolidColorBrush(solid.Color, 0.14) : null;
            panel.Children.Add(confirm);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => really.Focus());
        }
        return panel;
    }

    // ---------------------------------------------------------------- actions

    private async Task RunAsync()
    {
        if (_shown.Count == 0)
        {
            return;
        }
        var command = _shown[_selected].Macro.Command;
        Close();
        await _request.Run(command);
    }

    /// <summary>Delete: the question in the window (Keep / Delete).</summary>
    public void AskDelete()
    {
        if (_shown.Count > 0)
        {
            _confirming = true;
            Render();
        }
    }

    /// <summary>The answer Delete: the macro goes.</summary>
    public void ConfirmDelete()
    {
        if (_shown.Count == 0)
        {
            return;
        }
        var macro = _shown[_selected].Macro;
        _request.Delete(macro.Command);
        _macros.Remove(macro);
        _confirming = false;
        _hints.Text = $"Deleted {macro.Name}";
        Render();
        _search.Focus();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_confirming)
        {
            if (e.Key == Key.Escape)
            {
                _confirming = false;
                Render();
                _search.Focus();
                e.Handled = true;
            }
            return;
        }
        switch (e.Key)
        {
            case Key.Down or Key.Up when _shown.Count > 0:
                Select((_selected + (e.Key == Key.Down ? 1 : -1) + _shown.Count) % _shown.Count);
                e.Handled = true;
                break;
            case Key.Enter when e.Source is not Button:
                _ = RunAsync();
                e.Handled = true;
                break;
            case Key.Delete when _shown.Count > 0 && _search.CaretIndex >= (_search.Text?.Length ?? 0):
                AskDelete();
                e.Handled = true;
                break;
            case Key.Escape when !string.IsNullOrEmpty(_search.Text):
                _search.Text = string.Empty;
                e.Handled = true;
                break;
        }
    }
}
