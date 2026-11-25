using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Dialogs;

/// <summary>A folder of the frequent list: its address and its score (visits, older ones counting less).</summary>
public sealed record FrequentFolder(string Path, double Score);

/// <summary>What the list of frequent folders works with (set by the main window).</summary>
/// <param name="Folders">The folders, the most used first.</param>
/// <param name="Open">Opens a folder (or a typed path) in the active panel.</param>
/// <param name="OpenInOtherPanel">... in the other panel.</param>
/// <param name="OpenInNewTab">... in a new tab of the active panel.</param>
/// <param name="Remove">Takes a folder off the list; gives what Undo puts back.</param>
public sealed record FrequentFoldersRequest(
    IReadOnlyList<FrequentFolder> Folders,
    Func<string, Task> Open,
    Func<string, Task> OpenInOtherPanel,
    Func<string, Task> OpenInNewTab,
    Func<string, Action?> Remove);

/// <summary>
/// The most frequently used folders (Alt+Shift+Down, nc_FrequentDirectories; the user's template, 25 November 2026): a
/// search that finds the typed letters in order anywhere in the address (the letters found marked, hits in the folder's
/// own name first), the folders ranked with their score and a bar, a kind icon (folder, cloud folder, drive, place),
/// Del takes one off the list (Undo); Enter opens, Shift+Enter in the other panel, Ctrl+Enter in a new tab, and with no
/// match Enter goes to what was typed as a path.
/// </summary>
public sealed class FrequentFoldersDialog : Window
{
    /// <summary>A line of the list: the folder, its place in the ranking, the letters of the address the search found.</summary>
    private sealed record Line(FrequentFolder Folder, int Rank, IReadOnlyList<int>? Found, double Fit);

    private enum Kind
    {
        Folder,
        Cloud,
        Drive,
        Place,
    }

    private readonly FrequentFoldersRequest _request;
    private readonly List<FrequentFolder> _folders;
    private readonly TextBox _search = new() { PlaceholderText = "Type to search folders…", MinHeight = 40, VerticalContentAlignment = VerticalAlignment.Center, FontSize = 15 };
    private readonly TextBlock _count = new() { FontSize = 12, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly StackPanel _list = new() { Margin = new Thickness(8, 0, 8, 8), Spacing = 2 };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock _hints = new() { Text = "↑↓ move · Del remove", FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _note = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _undo = new() { Content = "Undo", IsVisible = false, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2) };
    private readonly List<Button> _needsTarget = [];
    private readonly List<Border> _rows = [];
    private List<Line> _shown = [];
    private int _selected;
    private Action? _undoAction;

    public FrequentFoldersDialog(FrequentFoldersRequest request)
    {
        _request = request;
        _folders = [.. request.Folders];
        Title = "Most frequently used folders";
        Width = 760;
        Height = 640;
        MinWidth = 520;
        MinHeight = 380;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _search.InnerLeftContent = new Border { Child = DialogLook.Glyph("MiSearch", 16), Margin = new Thickness(12, 0, 4, 0), Opacity = 0.6 };
        _search.InnerRightContent = _count;
        _search.TextChanged += (_, _) =>
        {
            _selected = 0;
            Render();
        };
        var search = new Border { Child = _search, Margin = new Thickness(16, 14, 16, 12) };

        var head = new DockPanel { Margin = new Thickness(18, 10, 18, 6) };
        var visits = Caption("Visits");
        DockPanel.SetDock(visits, Dock.Right);
        head.Children.AddRange([visits, Caption("Folder")]);
        var listArea = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        _scroll.Content = _list;
        listArea.Children.AddRange([head, _scroll]);

        _undo.Click += (_, _) =>
        {
            _undoAction?.Invoke();
            _undoAction = null;
            ShowNote(null);
            Render();
        };
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.AddRange([_hints, _note, _undo]);
        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "dialog" } };
        cancel.Click += (_, _) => Close();
        var other = FootButton("Open in other panel", "Shift+Enter", () => OpenAsync(_request.OpenInOtherPanel));
        var tab = FootButton("New tab", "Ctrl+Enter", () => OpenAsync(_request.OpenInNewTab));
        var open = new Button { Content = "Open", IsDefault = true, Classes = { "dialog" } };
        open.Click += async (_, _) => await OpenAsync(_request.Open);
        _needsTarget.AddRange([other, tab, open]);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.AddRange([cancel, other, tab, open]);
        var foot = new DockPanel { Margin = new Thickness(16, 12) };
        DockPanel.SetDock(buttons, Dock.Right);
        foot.Children.AddRange([buttons, left]);
        var footBorder = new Border { Child = foot, BorderThickness = new Thickness(0, 1, 0, 0) };
        footBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        footBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("FmPanelHeaderBrush");
        var listBorder = new Border { Child = listArea, BorderThickness = new Thickness(0, 1, 0, 0) };
        listBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");

        var root = new DockPanel();
        DockPanel.SetDock(search, Dock.Top);
        DockPanel.SetDock(footBorder, Dock.Bottom);
        root.Children.AddRange([search, footBorder, listBorder]);
        Content = root;

        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) => _search.Focus();
        Render();
    }

    /// <summary>The folders shown, in order (tests).</summary>
    public IReadOnlyList<string> Shown => [.. _shown.Select(l => l.Folder.Path)];

    /// <summary>The line selected (tests).</summary>
    public int Selected => _selected;

    /// <summary>The count beside the search ("3 of 11", "11 folders"; tests).</summary>
    public string CountText => _count.Text ?? string.Empty;

    public void Search(string text)
    {
        _search.Text = text;
        _selected = 0;
        Render();
    }

    private static TextBlock Caption(string text) =>
        new() { Text = text, FontSize = 11.5, FontWeight = FontWeight.SemiBold, Opacity = 0.5 };

    // ---------------------------------------------------------------- the search

    /// <summary>
    /// Whether every letter of <paramref name="query"/> is in <paramref name="text"/> in order (case and dots of the
    /// Turkish i do not matter); the places of the letters found and how well they fit (letters next to each other count
    /// more, gaps less), or null.
    /// </summary>
    internal static (IReadOnlyList<int> Found, double Fit)? Match(string query, string text)
    {
        var culture = CultureInfo.GetCultureInfo("tr-TR");
        var lower = text.ToLower(culture);
        var found = new List<int>();
        var from = 0;
        var fit = 0.0;
        var run = 0;
        foreach (var letter in query.ToLower(culture))
        {
            var at = lower.IndexOf(letter, from);
            if (at < 0)
            {
                return null;
            }
            run = found.Count > 0 && at == found[^1] + 1 ? run + 1 : 0;
            fit += 1 + run * 2 - Math.Min(at - from, 10) * 0.05;
            found.Add(at);
            from = at + 1;
        }
        return (found, fit);
    }

    /// <summary>A place (computer:///, trash:///), a drive's root, a cloud sync folder, or a folder.</summary>
    private static Kind KindOf(string path)
    {
        if (path.Contains(":///", StringComparison.Ordinal))
        {
            return Kind.Place;
        }
        if (VfsPath.TryParse(path, out var parsed) && parsed is { IsRoot: true })
        {
            return Kind.Drive;
        }
        return path.Contains("OneDrive", StringComparison.OrdinalIgnoreCase) || path.Contains("Google Drive", StringComparison.OrdinalIgnoreCase)
            || path.Contains("Dropbox", StringComparison.OrdinalIgnoreCase) || path.Contains("iCloud", StringComparison.OrdinalIgnoreCase)
            ? Kind.Cloud : Kind.Folder;
    }

    /// <summary>The folder's own name and the folder it is in (a drive or a place: its whole address as the name).</summary>
    private static (string Name, string Parent) Split(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        return KindOf(path) is Kind.Drive or Kind.Place || cut < 0 ? (path, string.Empty) : (trimmed[(cut + 1)..], trimmed[..(cut + 1)]);
    }

    private void Render()
    {
        var query = new string([.. (_search.Text ?? string.Empty).Where(c => !char.IsWhiteSpace(c))]);
        var lines = new List<Line>();
        for (var i = 0; i < _folders.Count; i++)
        {
            var folder = _folders[i];
            if (query.Length == 0)
            {
                lines.Add(new Line(folder, i, null, 0));
                continue;
            }
            if (Match(query, folder.Path) is not { } m)
            {
                continue;
            }
            // letters found in the folder's own name count more; then how much the folder is used
            var nameStart = folder.Path.Length - Split(folder.Path).Name.Length;
            var inName = m.Found.Count(at => at >= nameStart);
            lines.Add(new Line(folder, i, m.Found, m.Fit + inName * 1.5 + Math.Log10(folder.Score + 1)));
        }
        _shown = query.Length == 0 ? lines : [.. lines.OrderByDescending(l => l.Fit)];
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _shown.Count - 1));
        _count.Text = query.Length == 0
            ? $"{_folders.Count} folders"
            : $"{_shown.Count} of {_folders.Count}";

        var most = _folders.Count == 0 ? 1 : Math.Max(1, _folders.Max(f => f.Score));
        _list.Children.Clear();
        _rows.Clear();
        for (var i = 0; i < _shown.Count; i++)
        {
            var row = LineView(_shown[i], i, most);
            _rows.Add(row);
            _list.Children.Add(row);
        }
        if (_shown.Count == 0)
        {
            var empty = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 48, 0, 0) };
            empty.Children.AddRange([
                new TextBlock
                {
                    Text = _folders.Count == 0 ? "No folder is used often yet." : $"No folders match “{_search.Text?.Trim()}”",
                    FontWeight = FontWeight.SemiBold, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center,
                },
                new TextBlock { Text = "Press Enter to go to it as a path.", Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center, IsVisible = _folders.Count > 0 },
            ]);
            _list.Children.Add(empty);
        }
        var typed = !string.IsNullOrWhiteSpace(_search.Text);
        foreach (var button in _needsTarget)
        {
            button.IsEnabled = _shown.Count > 0 || typed;
        }
        if (_selected < _rows.Count)
        {
            _rows[_selected].BringIntoView();
        }
    }

    /// <summary>A text with the letters at <paramref name="found"/> (counted from <paramref name="offset"/> in the address) marked.</summary>
    private static TextBlock Marked(string text, IReadOnlyList<int>? found, int offset, double size, FontWeight weight, bool mono)
    {
        var block = new TextBlock { FontSize = size, FontWeight = weight, TextTrimming = mono ? TextTrimming.LeadingCharacterEllipsis : TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
        if (mono)
        {
            block.FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace");
            block.Opacity = 0.6;
        }
        var marks = found is null ? [] : found.Select(at => at - offset).Where(at => at >= 0 && at < text.Length).ToHashSet();
        if (marks.Count == 0)
        {
            block.Text = text;
            return block;
        }
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

    private Border LineView(Line line, int index, double most)
    {
        var selected = index == _selected;
        var path = line.Folder.Path;
        var kind = KindOf(path);
        var shownPath = VfsPath.TryParse(path, out var parsed) && parsed is { IsLocal: true } ? parsed.ToDisplayString() : path;
        var (name, parent) = Split(path);

        var rank = new TextBlock
        {
            Text = (line.Rank + 1).ToString(CultureInfo.InvariantCulture), Width = 20, FontSize = 11.5, Opacity = 0.5,
            TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" },
        };
        var icon = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8) };
        icon[!Border.BackgroundProperty] = new DynamicResourceExtension("FmPanelHeaderBrush");
        var (glyph, color) = kind switch
        {
            Kind.Drive => ("MiDrive", "#8E8E93"),
            Kind.Place => ("MiMonitor", "#8E8E93"),
            Kind.Cloud => ("FmFolderIcon", "#5AA9FF"),
            _ => ("FmFolderIcon", "#E8B44C"),
        };
        icon.Child = new PathIcon
        {
            Width = 18, Height = 18,
            Data = Services.Icons.IconPacks.Glyph(glyph) ?? Services.Icons.IconPacks.Glyph("FmFolderIcon"),
            Foreground = new SolidColorBrush(Color.Parse(color)),
        };

        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameOffset = path.Length - name.Length;
        body.Children.Add(Marked(name, line.Found, nameOffset, 14, FontWeight.SemiBold, mono: false));
        if (parent.Length > 0)
        {
            body.Children.Add(Marked(parent, line.Found, 0, 11.5, FontWeight.Normal, mono: true));
        }

        // the score's bar: its share of the most used folder's score
        var fill = new Border { Width = 84 * Math.Clamp(line.Folder.Score / most, 0.05, 1), HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2) };
        fill[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
        var bar = new Border { Width = 84, Height = 4, CornerRadius = new CornerRadius(2), Child = fill };
        bar[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        var score = new StackPanel { Spacing = 4, Width = 84, VerticalAlignment = VerticalAlignment.Center };
        score.Children.AddRange([
            new TextBlock
            {
                Text = line.Folder.Score.ToString("0.0", CultureInfo.CurrentCulture), FontSize = 12.5, FontWeight = FontWeight.SemiBold,
                Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Right, Classes = { "noloc" },
            },
            bar,
        ]);
        var remove = DialogLook.IconButton("MiTrash", "Remove from list (Del)", () =>
        {
            Remove(line.Folder);
            return Task.CompletedTask;
        });
        remove.Width = 28;
        remove.Height = 28;
        remove.Background = Brushes.Transparent;
        remove.BorderThickness = new Thickness(0);
        remove.Opacity = selected ? 1 : 0;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"), ColumnSpacing = 12 };
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(body, 2);
        Grid.SetColumn(score, 3);
        Grid.SetColumn(remove, 4);
        grid.Children.AddRange([rank, icon, body, score, remove]);
        var view = new Border
        {
            Child = grid, Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(view, shownPath);
        if (selected)
        {
            view[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
            view[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightListLowBrush");
        }
        view.PointerEntered += (_, _) => remove.Opacity = 1;
        view.PointerExited += (_, _) => remove.Opacity = index == _selected ? 1 : 0;
        view.PointerPressed += (_, e) =>
        {
            if (e.ClickCount >= 2)
            {
                _ = OpenAsync(_request.Open, line.Folder.Path);
            }
            else if (_selected != index)
            {
                _selected = index;
                Render();
            }
        };
        return view;
    }

    private static Button FootButton(string text, string key, Func<Task> click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        var hint = new Border
        {
            Child = new TextBlock { Text = key, FontSize = 10.5, Opacity = 0.7, Classes = { "noloc" } },
            BorderThickness = new Thickness(1, 1, 1, 2), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0), VerticalAlignment = VerticalAlignment.Center,
            [!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush"),
        };
        content.Children.AddRange([new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }, hint]);
        var button = new Button { Content = content, Classes = { "dialog" } };
        button.Click += async (_, _) => await click();
        return button;
    }

    // ---------------------------------------------------------------- actions

    /// <summary>The folder selected, or (no match) what was typed: it is opened as a path.</summary>
    private string? Target() => _shown.Count > 0 ? _shown[_selected].Folder.Path : _search.Text?.Trim() is { Length: > 0 } typed ? typed : null;

    private async Task OpenAsync(Func<string, Task> how, string? path = null)
    {
        if ((path ?? Target()) is not { } target)
        {
            return;
        }
        Close();
        await how(target);
    }

    /// <summary>Takes a folder off the list; Undo puts it back where it was.</summary>
    public void Remove(FrequentFolder folder)
    {
        var index = _folders.IndexOf(folder);
        if (index < 0)
        {
            return;
        }
        _folders.RemoveAt(index);
        var restore = _request.Remove(folder.Path);
        ShowNote($"“{Split(folder.Path).Name}” removed from the list.", () =>
        {
            _folders.Insert(Math.Min(index, _folders.Count), folder);
            restore?.Invoke();
        });
        Render();
        _search.Focus();
    }

    private void ShowNote(string? text, Action? undo = null)
    {
        _note.Text = text ?? string.Empty;
        _undoAction = undo;
        _undo.IsVisible = undo is not null;
        _hints.IsVisible = string.IsNullOrEmpty(text);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var atEnd = !_search.IsFocused || _search.CaretIndex >= (_search.Text?.Length ?? 0);
        switch (e.Key)
        {
            case Key.Down or Key.Up when _shown.Count > 0:
                _selected = (_selected + (e.Key == Key.Down ? 1 : -1) + _shown.Count) % _shown.Count;
                Render();
                e.Handled = true;
                break;
            case Key.PageDown or Key.PageUp when _shown.Count > 0:
                _selected = Math.Clamp(_selected + (e.Key == Key.PageDown ? 8 : -8), 0, _shown.Count - 1);
                Render();
                e.Handled = true;
                break;
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta):
                _ = OpenAsync(_request.OpenInNewTab);
                e.Handled = true;
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.Shift:
                _ = OpenAsync(_request.OpenInOtherPanel);
                e.Handled = true;
                break;
            case Key.Enter:
                _ = OpenAsync(_request.Open);
                e.Handled = true;
                break;
            case Key.Delete when _shown.Count > 0 && atEnd:
                Remove(_shown[_selected].Folder);
                e.Handled = true;
                break;
            case Key.Escape when !string.IsNullOrEmpty(_search.Text):
                _search.Text = string.Empty;
                e.Handled = true;
                break;
        }
    }
}
