using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Dialogs;

/// <summary>A folder of the history: when it was last entered and how often (unknown: null / 0).</summary>
public sealed record FolderHistoryEntry(VfsPath Path, DateTime? LastVisit, int Visits);

/// <summary>What the folder history works with (set by the main window).</summary>
/// <param name="Entries">The panel's folder history, the newest first.</param>
/// <param name="Pinned">The pinned folders (the setting; changed in place).</param>
/// <param name="PlaceOf">The drive / place of a folder for the chips ("C:", "/", a connection).</param>
/// <param name="Forget">Removes a folder from the panel's history.</param>
/// <param name="SetHistory">Sets the whole history (clearing, Undo).</param>
public sealed record FolderHistoryRequest(
    IReadOnlyList<FolderHistoryEntry> Entries,
    List<string> Pinned,
    Action SaveSettings,
    Func<VfsPath, string> PlaceOf,
    Func<VfsPath, Task> Open,
    Func<VfsPath, Task> OpenInOtherPanel,
    Func<VfsPath, Task> OpenInNewTab,
    Action<VfsPath> Forget,
    Action<IReadOnlyList<VfsPath>> SetHistory);

/// <summary>
/// The folder history (Alt+Down, nc_DirectoryHistory): the folders of the panel's history in sections (pinned, today,
/// yesterday, earlier) with when they were entered and how often; a search, chips per drive, and per folder: pin, open
/// in the other panel or a new tab, remove (with Undo). 1–9 open the first nine at once.
/// </summary>
public sealed class FolderHistoryDialog : Window
{
    private sealed record Line(VfsPath Path, DateTime? LastVisit, int Visits, bool Pinned, string Section);

    private readonly FolderHistoryRequest _request;
    private readonly List<FolderHistoryEntry> _entries;
    private readonly TextBox _search = new() { PlaceholderText = "Search folders", MinHeight = 36, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly StackPanel _list = new() { Margin = new Thickness(12, 0, 12, 12) };
    private readonly TextBlock _summary = DialogLook.Subtitle(string.Empty);
    private readonly TextBlock _hints = new() { Text = "1–9 quick open · Ctrl+Enter new tab · Del remove", FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _note = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _undo = new() { Content = "Undo", IsVisible = false, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2) };
    private readonly List<Button> _needsSelection = [];
    private List<Line> _shown = [];
    private string? _place;
    private int _selected;
    private Action? _undoAction;

    public FolderHistoryDialog(FolderHistoryRequest request)
    {
        _request = request;
        _entries = [.. request.Entries];
        Title = "Folder history";
        Width = 780;
        Height = 640;
        MinWidth = 560;
        MinHeight = 400;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // above: the badge, the title, the summary; clear the history
        var tint = new Border { CornerRadius = new CornerRadius(9), Opacity = 0.16 };
        tint[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
        var badge = new Panel { Width = 34, Height = 34 };
        badge.Children.AddRange([tint, DialogLook.Glyph("MiHistory", 19, accent: true)]);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        titles.Children.AddRange([new TextBlock { Text = "Folder history", FontSize = 15, FontWeight = FontWeight.SemiBold }, _summary]);
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.AddRange([badge, titles]);
        var clear = DialogLook.IconButton("MiTrash", "Clear history (pinned folders stay)", ClearHistory);
        clear.Background = Brushes.Transparent;
        clear.BorderThickness = new Thickness(0);
        var head = new DockPanel { Margin = new Thickness(20, 14) };
        DockPanel.SetDock(clear, Dock.Right);
        head.Children.AddRange([clear, titleRow]);

        _search.InnerLeftContent = new Border { Child = DialogLook.Glyph("MiSearch", 15), Margin = new Thickness(11, 0, 2, 0), Opacity = 0.6 };
        _search.InnerRightContent = new Border
        {
            Child = new TextBlock { Text = "Ctrl+F", FontSize = 11, Opacity = 0.6, Classes = { "noloc" } },
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 1),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            [!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush"),
        };
        _search.TextChanged += (_, _) =>
        {
            _selected = 0;
            Render();
        };
        var controls = new StackPanel { Spacing = 10, Margin = new Thickness(20, 14, 20, 10) };
        controls.Children.AddRange([_search, _chips]);

        // below: the keys (or a note with Undo), other panel, new tab, Cancel, Open
        _undo.Click += (_, _) =>
        {
            _undoAction?.Invoke();
            _undoAction = null;
            ShowNote(null);
            Render();
        };
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.AddRange([_hints, _note, _undo]);
        var other = FootButton("MiSplitView", "Other panel", "Open in the other panel (Shift+Enter)", () => OpenAsync(_request.OpenInOtherPanel));
        var tab = FootButton("MiTabs", "New tab", "Open in a new tab (Ctrl+Enter)", () => OpenAsync(_request.OpenInNewTab));
        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "dialog" } };
        cancel.Click += (_, _) => Close();
        var open = new Button { Content = "Open", IsDefault = true, Classes = { "dialog" } };
        open.Click += async (_, _) => await OpenAsync(_request.Open);
        _needsSelection.AddRange([other, tab, open]);
        var line = new Border { Width = 1, Height = 22, Margin = new Thickness(2, 0) };
        line[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.AddRange([other, tab, line, cancel, open]);
        var foot = new DockPanel { Margin = new Thickness(20, 12) };
        DockPanel.SetDock(buttons, Dock.Right);
        foot.Children.AddRange([buttons, left]);

        var root = new DockPanel();
        var headBorder = Bordered(head, new Thickness(0, 0, 0, 1));
        var footBorder = Bordered(foot, new Thickness(0, 1, 0, 0));
        DockPanel.SetDock(headBorder, Dock.Top);
        DockPanel.SetDock(controls, Dock.Top);
        DockPanel.SetDock(footBorder, Dock.Bottom);
        root.Children.AddRange([headBorder, controls, footBorder, new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }]);
        Content = root;

        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) => _search.Focus();
        Render();
    }

    /// <summary>The folders shown, in order (tests).</summary>
    public IReadOnlyList<VfsPath> Shown => [.. _shown.Select(l => l.Path)];

    /// <summary>The section of each folder shown (tests).</summary>
    public IReadOnlyList<string> Sections => [.. _shown.Select(l => l.Section)];

    public void Search(string text) => _search.Text = text;

    // ---------------------------------------------------------------- lines

    private bool IsPinned(VfsPath path) => _request.Pinned.Contains(path.ToString(), StringComparer.Ordinal);

    private static string SectionOf(DateTime? time, DateTime now) =>
        time is not { } t ? "Earlier" : t.Date == now.Date ? "Today" : t.Date == now.Date.AddDays(-1) ? "Yesterday" : "Earlier";

    private List<Line> AllLines()
    {
        var now = DateTime.Now;
        var lines = new List<Line>();
        // pinned first, in their order (also when they left the history)
        foreach (var text in _request.Pinned)
        {
            if (VfsPath.TryParse(text, out var path))
            {
                var entry = _entries.FirstOrDefault(e => e.Path == path!);
                lines.Add(new Line(path!, entry?.LastVisit, entry?.Visits ?? 0, true, "Pinned"));
            }
        }
        string[] order = ["Today", "Yesterday", "Earlier"];
        lines.AddRange(_entries.Where(e => !IsPinned(e.Path))
            .Select((e, i) => (Line: new Line(e.Path, e.LastVisit, e.Visits, false, SectionOf(e.LastVisit, now)), Index: i))
            .OrderBy(x => Array.IndexOf(order, x.Line.Section)).ThenBy(x => x.Index)
            .Select(x => x.Line));
        return lines;
    }

    private void Render()
    {
        var query = _search.Text?.Trim() ?? string.Empty;
        var all = AllLines();
        var places = all.Select(l => _request.PlaceOf(l.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (_place is not null && !places.Contains(_place, StringComparer.OrdinalIgnoreCase))
        {
            _place = null;
        }
        _shown = all.Where(l => (_place is null || string.Equals(_request.PlaceOf(l.Path), _place, StringComparison.OrdinalIgnoreCase))
            && (query.Length == 0 || l.Path.ToDisplayString().Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToList();
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _shown.Count - 1));

        var pinned = all.Count(l => l.Pinned);
        _summary.Text = pinned == 0 ? $"{all.Count} folders" : $"{all.Count} folders, {pinned} pinned";
        RenderChips(places);

        _list.Children.Clear();
        string? section = null;
        for (var i = 0; i < _shown.Count; i++)
        {
            if (_shown[i].Section != section)
            {
                section = _shown[i].Section;
                _list.Children.Add(new TextBlock { Text = section, FontSize = 11, FontWeight = FontWeight.SemiBold, Opacity = 0.55, Margin = new Thickness(10, 12, 0, 6) });
            }
            _list.Children.Add(LineView(_shown[i], i));
        }
        if (_shown.Count == 0)
        {
            var empty = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 60, 0, 0) };
            empty.Children.AddRange([
                new Border { Child = DialogLook.Glyph("MiSearch", 32), Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = all.Count > 0 ? "No matching folders" : "History is empty", FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = all.Count > 0 ? "Try another search or drive." : "Folders you visit will show up here.", FontSize = 12, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center },
            ]);
            _list.Children.Add(empty);
        }
        foreach (var button in _needsSelection)
        {
            button.IsEnabled = _shown.Count > 0;
        }
    }

    private void RenderChips(List<string> places)
    {
        _chips.Children.Clear();
        foreach (var place in new string?[] { null }.Concat(places))
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            if (place is not null)
            {
                content.Children.Add(DialogLook.Glyph("MiDrive", 13));
            }
            var label = new TextBlock { Text = place ?? "All drives", VerticalAlignment = VerticalAlignment.Center };
            if (place is not null)
            {
                label.Classes.Add("noloc"); // a drive's name
            }
            content.Children.Add(label);
            var chip = new ToggleButton
            {
                Content = content,
                IsChecked = string.Equals(place, _place, StringComparison.OrdinalIgnoreCase),
                Height = 28,
                FontSize = 12,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Classes = { "chip" },
            };
            var which = place;
            chip.Click += (_, _) =>
            {
                _place = which;
                _selected = 0;
                Render();
            };
            _chips.Children.Add(chip);
        }
    }

    private static string Ago(DateTime? time)
    {
        if (time is not { } t)
        {
            return string.Empty;
        }
        var span = DateTime.Now - t;
        if (span.TotalMinutes < 1)
        {
            return "Just now";
        }
        if (span.TotalMinutes < 60)
        {
            return $"{(int)span.TotalMinutes} min ago";
        }
        if (span.TotalHours < 24)
        {
            return $"{(int)Math.Round(span.TotalHours)} h ago";
        }
        var days = (int)Math.Round(span.TotalDays);
        return days <= 1 ? "Yesterday" : $"{days} days ago";
    }

    /// <summary>The folder's name and the folder it is in ("C:\" for a drive's root: its name is the root itself).</summary>
    private static (string Name, string Parent, bool Root) Split(VfsPath path)
    {
        var text = path.ToDisplayString();
        var trimmed = text.TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        if (path.IsRoot || cut < 0 || trimmed.Length == 0)
        {
            return (text, string.Empty, true);
        }
        return (trimmed[(cut + 1)..], trimmed[..(cut + 1)], false);
    }

    private Border LineView(Line line, int index)
    {
        var selected = index == _selected;
        var (name, parent, root) = Split(line.Path);
        var number = new TextBlock
        {
            Text = index < 9 ? (index + 1).ToString(CultureInfo.InvariantCulture) : string.Empty,
            Width = 18,
            FontSize = 11,
            Opacity = 0.5,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "noloc" },
        };
        var icon = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1) };
        icon[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        icon[!Border.BackgroundProperty] = new DynamicResourceExtension("FmPanelHeaderBrush");
        var geometry = (Geometry?)Application.Current?.FindResource(root ? "FmDriveIcon" : "FmFolderIcon") ?? (Geometry?)Application.Current?.FindResource("FmFolderIcon");
        icon.Child = new PathIcon { Width = 18, Height = 18, Data = geometry, Foreground = new SolidColorBrush(Color.Parse(root ? "#8E8E93" : "#E8B44C")) };

        var nameLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        nameLine.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, FontSize = 14, Classes = { "noloc" } });
        if (line.Pinned)
        {
            nameLine.Children.Add(new PathIcon { Width = 12, Height = 12, Data = Services.Icons.IconPacks.Glyph("MiStar"), Foreground = new SolidColorBrush(Color.Parse("#E8B44C")) });
        }
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(nameLine);
        if (parent.Length > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = parent,
                FontSize = 11.5,
                Opacity = 0.55,
                FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
                TextTrimming = TextTrimming.LeadingCharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
                Classes = { "noloc" },
            });
        }

        var meta = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Spacing = 3 };
        if (Ago(line.LastVisit) is { Length: > 0 } ago)
        {
            meta.Children.Add(new TextBlock { Text = ago, FontSize = 12, Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Right });
        }
        if (line.Visits > 0)
        {
            meta.Children.Add(new TextBlock { Text = line.Visits == 1 ? "1 visit" : $"{line.Visits} visits", FontSize = 11, Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Right });
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, IsVisible = selected };
        actions.Children.AddRange([
            Action("MiStar", line.Pinned ? "Unpin" : "Pin to top (Ctrl+P)", () => TogglePin(line.Path)),
            Action("MiSplitView", "Open in the other panel", () => OpenAsync(_request.OpenInOtherPanel, line)),
            Action("MiTabs", "Open in a new tab", () => OpenAsync(_request.OpenInNewTab, line)),
            Action("MiX", "Remove from history (Del)", () => Remove(line)),
        ]);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"), ColumnSpacing = 12 };
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(body, 2);
        Grid.SetColumn(meta, 3);
        Grid.SetColumn(actions, 4);
        grid.Children.AddRange([number, icon, body, meta, actions]);
        var view = new Border
        {
            Child = grid,
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 2),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(view, line.Path.ToDisplayString());
        if (selected)
        {
            view[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
            view[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightListLowBrush");
        }
        view.PointerEntered += (_, _) => actions.IsVisible = true;
        view.PointerExited += (_, _) => actions.IsVisible = index == _selected;
        view.PointerPressed += (_, e) =>
        {
            if (e.ClickCount >= 2)
            {
                _ = OpenAsync(_request.Open, line);
            }
            else if (_selected != index)
            {
                _selected = index;
                Render();
            }
        };
        return view;
    }

    private static Button Action(string glyph, string tip, Func<Task> click)
    {
        var button = DialogLook.IconButton(glyph, tip, click);
        button.Width = 30;
        button.Height = 30;
        button.Background = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
        return button;
    }

    private static Button Action(string glyph, string tip, Action click) => Action(glyph, tip, () =>
    {
        click();
        return Task.CompletedTask;
    });

    private static Button FootButton(string glyph, string text, string tip, Func<Task> click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.AddRange([DialogLook.Glyph(glyph, 15), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }]);
        var button = new Button { Content = content, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        ToolTip.SetTip(button, tip);
        button.Click += async (_, _) => await click();
        return button;
    }

    // ---------------------------------------------------------------- actions

    private async Task OpenAsync(Func<VfsPath, Task> how, Line? line = null)
    {
        if ((line ?? (_shown.Count > 0 ? _shown[_selected] : null)) is not { } target)
        {
            return;
        }
        Close();
        await how(target.Path);
    }

    /// <summary>Pins a folder to the top of the history, or unpins it (Ctrl+P).</summary>
    public void TogglePin(VfsPath path)
    {
        var text = path.ToString();
        if (!_request.Pinned.Remove(text))
        {
            _request.Pinned.Add(text);
        }
        _request.SaveSettings();
        ShowNote(IsPinned(path) ? "Pinned to top." : "Unpinned.");
        Render();
    }

    /// <summary>Removes a folder from the history (pinned ones stay pinned); Undo puts it back.</summary>
    private void Remove(Line line)
    {
        var index = _entries.FindIndex(e => e.Path == line.Path);
        if (index < 0)
        {
            return;
        }
        var entry = _entries[index];
        _entries.RemoveAt(index);
        _request.Forget(line.Path);
        ShowNote("Removed from history.", () =>
        {
            _entries.Insert(Math.Min(index, _entries.Count), entry);
            _request.SetHistory([.. _entries.Select(e => e.Path)]);
        });
        Render();
    }

    /// <summary>Empties the history (pinned folders stay); Undo puts it back.</summary>
    public void ClearHistory()
    {
        if (_entries.Count == 0)
        {
            return;
        }
        var old = _entries.ToList();
        _entries.Clear();
        _request.SetHistory([]);
        ShowNote("History cleared, pinned folders kept.", () =>
        {
            _entries.AddRange(old);
            _request.SetHistory([.. old.Select(e => e.Path)]);
        });
        _selected = 0;
        Render();
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
        var searching = _search.IsFocused && !string.IsNullOrEmpty(_search.Text);
        switch (e.Key)
        {
            case Key.Down or Key.Up when _shown.Count > 0:
                _selected = Math.Clamp(_selected + (e.Key == Key.Down ? 1 : -1), 0, _shown.Count - 1);
                Render();
                e.Handled = true;
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.Control:
                _ = OpenAsync(_request.OpenInNewTab);
                e.Handled = true;
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.Shift:
                _ = OpenAsync(_request.OpenInOtherPanel);
                e.Handled = true;
                break;
            case Key.Delete when !searching && _shown.Count > 0:
                Remove(_shown[_selected]);
                e.Handled = true;
                break;
            case Key.P when e.KeyModifiers == KeyModifiers.Control && _shown.Count > 0:
                TogglePin(_shown[_selected].Path);
                e.Handled = true;
                break;
            case Key.F when e.KeyModifiers == KeyModifiers.Control:
                _search.Focus();
                _search.SelectAll();
                e.Handled = true;
                break;
            case Key.Escape when !string.IsNullOrEmpty(_search.Text):
                _search.Text = string.Empty;
                e.Handled = true;
                break;
            case >= Key.D1 and <= Key.D9 when e.KeyModifiers == KeyModifiers.None && !searching:
            case >= Key.NumPad1 and <= Key.NumPad9 when e.KeyModifiers == KeyModifiers.None && !searching:
                var number = e.Key >= Key.NumPad1 ? e.Key - Key.NumPad1 : e.Key - Key.D1;
                if (number < _shown.Count)
                {
                    _ = OpenAsync(_request.Open, _shown[number]);
                    e.Handled = true;
                }
                break;
        }
    }

    private static Border Bordered(Control child, Thickness border)
    {
        var box = new Border { Child = child, BorderThickness = border };
        box[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return box;
    }
}
