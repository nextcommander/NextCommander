using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NextCommander.App.Services;
using NextCommander.Core.Config;
using NextCommander.Core.Formatting;
using NextCommander.Core.Operations;

namespace NextCommander.App.Views.Dialogs;

/// <summary>What the copy dialog shows besides the target: the size and counts, lists kept in the settings, the tree.</summary>
public sealed record CopyDialogOptions
{
    /// <summary>The target and filter lists (+ F7 / + F8), the pinned options and the default answer are kept here.</summary>
    public AppSettings? Settings { get; init; }

    /// <summary>Size, files and folders of what is copied, as far as known at once.</summary>
    public CopySummary? Summary { get; init; }

    /// <summary>Counts the contents of the folders in the background (the numbers are updated when it is done).</summary>
    public Func<CancellationToken, Task<CopySummary>>? Count { get; init; }

    /// <summary>The folders marked in the target panel (Copy to all selected folders).</summary>
    public int TargetFolders { get; init; }

    /// <summary>+ F8: opens Find files over the dialog; the files found are copied instead of the selection (null: cancelled).</summary>
    public Func<Window, Task<IReadOnlyList<Core.Vfs.VfsEntry>?>>? FindFiles { get; init; }

    /// <summary>Chooses the target folder from a tree (Tree button, F10): owner, current target → folder or null.</summary>
    public Func<Window, string, Task<string?>>? PickFolder { get; init; }
}

/// <summary>Size and counts of the items of a copy.</summary>
public readonly record struct CopySummary(long Bytes, int Files, int Folders);

/// <summary>
/// F5/F6 dialog: the target (with the target list, + F7 adds or removes the target),
/// the file filter (+ F8 keeps it in the filter list), the size and counts, NTFS permissions and Verify; OK, F2 Queue,
/// Tree, Cancel and Options &gt;&gt; (overwrite answer, pinned open, saved as the default, and the advanced check boxes).
/// Right click on OK or F2 Queue moves instead of copying (and the other way round).
/// </summary>
public sealed class CopyDialog : DialogWindow
{
    private static readonly (string Text, ConflictPolicy Policy)[] s_policies =
    [
        ("Ask user", ConflictPolicy.Ask),
        ("Overwrite all", ConflictPolicy.Overwrite),
        ("Skip all", ConflictPolicy.Skip),
        ("Overwrite all older", ConflictPolicy.OverwriteOlder),
        ("Auto-rename copied", ConflictPolicy.AutoRenameSource),
        ("Auto-rename target files", ConflictPolicy.AutoRenameTarget),
        ("Copy all larger files (overwrite smaller)", ConflictPolicy.OverwriteSmaller),
        ("Copy all smaller files (overwrite larger)", ConflictPolicy.OverwriteLarger),
        ("Rename older target files, skip newer", ConflictPolicy.RenameOlderTarget),
    ];

    /// <summary>A push pin (keep the options open).</summary>
    private static readonly Geometry s_pin = Geometry.Parse("M6,1.5 L10,1.5 L10,2.5 L9.3,3 L9.3,7 L11.5,9 L11.5,10 L8.5,10 L8,14.5 L7.5,10 L4.5,10 L4.5,9 L6.7,7 L6.7,3 L6,2.5 Z");

    /// <summary>The answer chosen at first when a file exists (Configuration > Copy / delete).</summary>
    public static ConflictPolicy DefaultPolicy { get; set; } = ConflictPolicy.Ask;

    /// <summary>The choices for an existing target file: text and policy (Configuration offers the same list).</summary>
    public static IReadOnlyList<(string Text, ConflictPolicy Policy)> Policies => s_policies;

    private readonly bool _move;
    private readonly CopyDialogOptions _options;
    private readonly TextBox _target;
    private readonly TextBox _filter = new() { PlaceholderText = "*.* (all files), e.g. *.jpg *.png | thumbs\\" };
    private readonly ComboBox _policy;
    private readonly CheckBox _verify;
    private readonly CheckBox _permissions = new() { Content = "Copy NTFS permissions (may need administrator rights)" };
    private readonly CheckBox _skipUnreadable = new() { Content = "Skip all which cannot be opened for reading" };
    private readonly CheckBox _overwriteProtected = new() { Content = "Overwrite/Delete read only/hidden/system" };
    private readonly CheckBox _allTargets = new() { Content = "Copy to all selected folders/links in the target panel" };
    private readonly TextBlock _size = new() { VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
    private readonly TextBlock _files = new() { VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
    private readonly TextBlock _folders = new() { VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, IsVisible = false, TextWrapping = TextWrapping.Wrap };
    private readonly Border _advanced;
    private readonly Button _optionsButton;
    private readonly ToggleButton _pin;
    private readonly IReadOnlyList<string> _suggestions;
    private readonly CancellationTokenSource _counting = new();
    private readonly Button _targetButton;
    private readonly Button _filterButton;
    private CopySummary? _summary;
    private IReadOnlyList<Core.Vfs.VfsEntry>? _found;
    private bool _showingFound;
    private readonly StackPanel _counts = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };

    public CopyDialog(bool move, string description, string target, bool verifyDefault, IReadOnlyList<string>? suggestions = null, CopyDialogOptions? options = null)
        : base(move ? "Move / Rename" : "Copy", 640)
    {
        _move = move;
        _options = options ?? new CopyDialogOptions();
        _suggestions = suggestions ?? [];
        var settings = _options.Settings;

        Body.Children.Add(new TextBlock { Text = description, TextTrimming = TextTrimming.CharacterEllipsis });

        // target with its list, + F7
        _target = new TextBox { Text = target, Classes = { "noloc" } };
        _targetButton = ListButton("+ F7", "The target list and the directory hotlist (F7, Ctrl+D)", TargetMenu);
        Body.Children.Add(Line(ListBox(_target, TargetChoices), _targetButton));
        Completion = PathAutoComplete.Attach(_target);

        // "Only files of this type", the size and the counts
        var counts = _counts;
        counts.Children.AddRange([_size, new Border { Width = 26 }, Glyph(Services.Icons.FileKind.File), _files, new Border { Width = 26 },
            Glyph(Services.Icons.FileKind.Folder), _folders]);
        var filterHead = new DockPanel();
        DockPanel.SetDock(counts, Dock.Right);
        filterHead.Children.AddRange([counts, new TextBlock { Text = "Only files of this type:", VerticalAlignment = VerticalAlignment.Center }]);
        Body.Children.Add(filterHead);
        _filterButton = new Button { Content = "+ F8", MinWidth = 60, HorizontalContentAlignment = HorizontalAlignment.Center, IsEnabled = _options.FindFiles is not null };
        ToolTip.SetTip(_filterButton, "Find files: search, and copy the files found (F8)");
        _filterButton.Click += async (_, _) => await FindFilesAsync();
        Body.Children.Add(Line(ListBox(_filter, FilterChoices), _filterButton));
        _filter.TextChanged += (_, _) =>
        {
            if (!_showingFound && _found is not null)
            {
                // typing a filter again: the selection is copied as before
                _found = null;
                ShowSummary(_options.Summary);
            }
        };
        ShowSummary(_options.Summary);

        // NTFS permissions (Windows) and Verify
        _verify = new CheckBox { Content = "Verify", IsChecked = verifyDefault };
        var checks = new DockPanel();
        DockPanel.SetDock(_verify, Dock.Right);
        checks.Children.Add(_verify);
        if (OperatingSystem.IsWindows())
        {
            checks.Children.Add(_permissions);
        }
        Body.Children.Add(checks);
        Body.Children.Add(_error);

        // OK, F2 Queue, Tree, Cancel, Options >>
        var ok = new Button { Content = "OK", IsDefault = true };
        ok.Click += (_, _) => Finish(queue: false, switchOperation: false);
        var queue = new Button { Content = "F2 Queue" };
        queue.Click += (_, _) => Finish(queue: true, switchOperation: false);
        var tree = new Button { Content = "Tree", IsEnabled = _options.PickFolder is not null };
        tree.Click += async (_, _) => await PickFromTreeAsync();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();
        _optionsButton = new Button { Content = "Options >>" };
        _optionsButton.Click += (_, _) => ShowAdvanced(true);
        // right click: the other operation (copy instead of move, move instead of copy)
        foreach (var (button, toQueue) in new[] { (ok, false), (queue, true) })
        {
            var other = new MenuItem { Header = move ? "Copy" : "Move" };
            other.Click += (_, _) => Finish(toQueue, switchOperation: true);
            button.ContextMenu = new ContextMenu { Items = { other } };
            button.ContextMenu.Opening += (_, _) => other.Icon = settings?.ShowMenuIcons != false ? MenuIcons.Create(move ? "nc_Copy" : "nc_RenMov") : null;
        }
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"), ColumnSpacing = 6 };
        var column = 0;
        foreach (var button in new[] { ok, queue, tree, cancel, _optionsButton })
        {
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(button, column++);
            buttons.Children.Add(button);
        }
        Body.Children.Add(buttons);

        // Advanced options
        _policy = new ComboBox
        {
            ItemsSource = s_policies.Select((p, i) => $"{i + 1}. {p.Text}").ToList(),
            SelectedIndex = Math.Max(0, Array.FindIndex(s_policies, p => p.Policy == DefaultPolicy)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _pin = new ToggleButton
        {
            IsChecked = settings?.CopyDialogExpanded == true,
            Content = new Avalonia.Controls.Shapes.Path { Data = s_pin, Fill = Brushes.SteelBlue, Width = 14, Height = 14, Stretch = Stretch.Uniform },
            Padding = new Thickness(6, 4),
        };
        ToolTip.SetTip(_pin, "Always open the copy dialog with these options");
        _pin.IsCheckedChanged += (_, _) =>
        {
            if (settings is not null)
            {
                settings.CopyDialogExpanded = _pin.IsChecked == true;
            }
        };
        var save = new Button { Content = new Viewbox { Width = 14, Height = 14, Child = MenuIcons.Create("copy.savedefault") ?? new Panel() }, Padding = new Thickness(6, 4) };
        ToolTip.SetTip(save, "Use this answer by default");
        save.Click += (_, _) => SaveDefaultPolicy();
        _allTargets.IsEnabled = _options.TargetFolders > 0;
        if (_options.TargetFolders > 0)
        {
            ToolTip.SetTip(_allTargets, string.Format(CultureInfo.CurrentCulture, "{0} folders are selected in the target panel", _options.TargetFolders));
        }
        var advanced = new StackPanel { Spacing = 6 };
        var overwriteHead = new DockPanel();
        DockPanel.SetDock(_pin, Dock.Right);
        overwriteHead.Children.AddRange([_pin, new TextBlock { Text = "Overwrite options", VerticalAlignment = VerticalAlignment.Bottom }]);
        advanced.Children.AddRange([overwriteHead, Line(_policy, save), _skipUnreadable, _overwriteProtected, _allTargets]);
        var group = new StackPanel { Spacing = 4 };
        group.Children.AddRange([new TextBlock { Text = "Advanced options", FontWeight = FontWeight.SemiBold }, advanced]);
        _advanced = new Border
        {
            Child = group,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 4, 8, 8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
        };
        Body.Children.Add(_advanced);
        ShowAdvanced(settings?.CopyDialogExpanded == true);

        Opened += async (_, _) =>
        {
            _target.Focus();
            SelectTargetName();
            await CountAsync();
        };
        Closed += (_, _) => _counting.Cancel();
    }

    public CopyDialogResult? Result { get; private set; }

    /// <summary>Path completion of the target box.</summary>
    public PathAutoComplete Completion { get; }

    public TextBox TargetBox => _target;

    public TextBox FilterBox => _filter;

    public bool AdvancedShown => _advanced.IsVisible;

    /// <summary>The size and count texts (tests).</summary>
    public string SummaryText => $"{_size.Text} | {_files.Text} | {_folders.Text}";

    /// <summary>Opens or closes the advanced options (Options &gt;&gt;).</summary>
    public void ShowAdvanced(bool show)
    {
        _advanced.IsVisible = show;
        _optionsButton.IsVisible = !show;
    }

    /// <summary>Speed limit text ("10M", "500k/s", empty = unlimited) to bytes per second.</summary>
    public static bool TryParseSpeed(string? text, out long bytesPerSecond)
    {
        bytesPerSecond = 0;
        var t = text?.Trim() ?? string.Empty;
        if (t.EndsWith("/s", StringComparison.OrdinalIgnoreCase) || t.EndsWith("/sn", StringComparison.OrdinalIgnoreCase))
        {
            t = t[..t.LastIndexOf('/')];
        }
        try
        {
            bytesPerSecond = Core.Search.SearchInputParser.ParseSize(t) ?? 0;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- pieces

    private static DockPanel Line(Control main, Control side)
    {
        side.Margin = new Thickness(6, 0, 0, 0);
        var line = new DockPanel();
        DockPanel.SetDock(side, Dock.Right);
        line.Children.AddRange([side, main]);
        return line;
    }

    /// <summary>A text box with a drop-down list (built when it opens; Alt+Down opens it too).</summary>
    private static Control ListBox(TextBox box, Func<IReadOnlyList<string>> choices)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var drop = new Button { Content = "▾", Flyout = flyout, Focusable = false, Padding = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Stretch };
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            foreach (var choice in choices())
            {
                var item = new MenuItem { Header = new TextBlock { Text = choice, Classes = { "noloc" } } };
                item.Click += (_, _) =>
                {
                    box.Text = choice;
                    box.Focus();
                    box.CaretIndex = choice.Length;
                };
                flyout.Items.Add(item);
            }
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && e.KeyModifiers == KeyModifiers.Alt)
            {
                if (choices().Count > 0)
                {
                    flyout.ShowAt(drop);
                }
                e.Handled = true;
            }
        };
        // the box in a panel of its own: path completion puts its popup next to it (a last child of the dock panel would fill it)
        var host = new Grid();
        host.Children.Add(box);
        var line = new DockPanel();
        DockPanel.SetDock(drop, Dock.Right);
        line.Children.AddRange([drop, host]);
        return line;
    }

    private static Button ListButton(string text, string tip, Func<MenuFlyout> menu)
    {
        var button = new Button { Content = text, MinWidth = 60, HorizontalContentAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => menu().ShowAt(button);
        return button;
    }

    private MenuItem MenuEntry(string header, string? icon, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        if (icon is not null && _options.Settings?.ShowMenuIcons != false)
        {
            item.Icon = MenuIcons.Create(icon);
        }
        return item;
    }

    private static bool InList(List<string>? list, string? text) =>
        list is not null && text?.Trim() is { Length: > 0 } value && list.Contains(value, StringComparer.OrdinalIgnoreCase);

    /// <summary>+ F7: add the target to the target list (at the top) or remove it; the directory hotlist as the target.</summary>
    public MenuFlyout TargetMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(InList(_options.Settings?.CopyTargetList, _target.Text)
            ? MenuEntry("Remove current dir", "find.remove", ToggleTarget)
            : MenuEntry("Add current dir (Position=0)", "copy.addtarget", ToggleTarget));
        var hotlist = MenuEntry("Directory hotlist (Ctrl+D)", "nc_DirectoryHotlist", () => { });
        AddHotlist(hotlist.Items);
        hotlist.IsEnabled = hotlist.Items.Count > 0;
        menu.Items.Add(hotlist);
        return menu;
    }

    /// <summary>The folders of the directory hotlist ("Group/Name" in submenus); commands are left out.</summary>
    private void AddHotlist(ItemCollection root)
    {
        foreach (var entry in _options.Settings?.Hotlist ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Path) || entry.Path.TrimStart().StartsWith("nc_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var parts = entry.Name.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var parent = root;
            foreach (var group in parts.SkipLast(1))
            {
                var existing = parent.OfType<MenuItem>().FirstOrDefault(i => i.Tag as string == group && i.Items.Count > 0);
                if (existing is null)
                {
                    existing = new MenuItem { Header = new TextBlock { Text = group, Classes = { "noloc" } }, Tag = group };
                    parent.Add(existing);
                }
                parent = existing.Items;
            }
            var path = Environment.ExpandEnvironmentVariables(entry.Path.Trim());
            var item = new MenuItem { Header = new TextBlock { Text = parts.Length > 0 ? parts[^1] : path, Classes = { "noloc" } } };
            ToolTip.SetTip(item, path);
            item.Click += (_, _) => UseFolder(path);
            parent.Add(item);
        }
    }

    /// <summary>A folder as the target; a single file keeps its name.</summary>
    public void UseFolder(string folder)
    {
        var text = _target.Text ?? string.Empty;
        var name = !text.EndsWith('\\') && !text.EndsWith('/') && Path.GetFileName(text) is { Length: > 0 } file && _summary is { Files: 1, Folders: 0 }
            ? file : string.Empty;
        var separator = folder.Contains('/', StringComparison.Ordinal) && !folder.Contains('\\', StringComparison.Ordinal) ? "/" : Path.DirectorySeparatorChar.ToString();
        _target.Text = folder.TrimEnd('\\', '/') + separator + name;
        _target.Focus();
        _target.CaretIndex = _target.Text.Length;
    }

    /// <summary>+ F8: Find files opens over the dialog; the files found there are copied (instead of the selection).</summary>
    public async Task FindFilesAsync()
    {
        if (_options.FindFiles is not { } find || await find(this) is not { Count: > 0 } found)
        {
            return;
        }
        UseFound(found);
    }

    /// <summary>The files found by Find files become what is copied; the filter box says so.</summary>
    public void UseFound(IReadOnlyList<Core.Vfs.VfsEntry> found)
    {
        _found = found;
        _counting.Cancel();
        _showingFound = true;
        _filter.Text = string.Format(CultureInfo.CurrentCulture, "Find files: {0} results", found.Count);
        _showingFound = false;
        ShowSummary(new CopySummary(found.Where(e => !e.IsDirectory).Sum(e => e.Size), found.Count(e => !e.IsDirectory), found.Count(e => e.IsDirectory)));
        // several files: the target is a folder (a single file name would not fit them)
        var text = _target.Text ?? string.Empty;
        if (found.Count > 1 && !text.EndsWith('\\') && !text.EndsWith('/') && Path.GetDirectoryName(text) is { Length: > 0 } folder
            && _options.Summary is { Files: 1, Folders: 0 })
        {
            _target.Text = folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        }
    }

    private static Control Glyph(Services.Icons.FileKind kind) => new Viewbox
    {
        Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center,
        Child = Services.Icons.FileKinds.GeometryFor(kind) is { } glyph ? Services.Icons.IconPacks.Draw(glyph, Services.Icons.FileKinds.BrushFor(kind)) : new Panel(),
    };

    /// <summary>The kept target list, then the suggestions (other panel, recent targets, its subfolders).</summary>
    public IReadOnlyList<string> TargetChoices() =>
        [.. (_options.Settings?.CopyTargetList ?? []).Concat(_suggestions).Distinct(StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<string> FilterChoices() => _options.Settings?.CopyFilterList ?? [];

    /// <summary>+ F7: the target goes into the target list, or out of it when it is there.</summary>
    public void ToggleTarget() => Toggle(_options.Settings?.CopyTargetList, _target.Text);

    /// <summary>+ F8: the filter goes into the filter list, or out of it when it is there.</summary>
    public void ToggleFilter() => Toggle(_options.Settings?.CopyFilterList, _filter.Text);

    private static void Toggle(List<string>? list, string? text)
    {
        var value = text?.Trim() ?? string.Empty;
        if (list is null || value.Length == 0)
        {
            return;
        }
        if (list.RemoveAll(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            list.Insert(0, value);
        }
    }

    /// <summary>The disk icon: the chosen answer becomes the default of the dialog (and of Configuration > Copy and delete).</summary>
    public void SaveDefaultPolicy()
    {
        var policy = s_policies[Math.Max(0, _policy.SelectedIndex)].Policy;
        DefaultPolicy = policy;
        if (_options.Settings is { } settings)
        {
            settings.DefaultConflictPolicy = policy;
        }
    }

    private void ShowSummary(CopySummary? summary)
    {
        _summary = summary;
        _counts.IsVisible = summary is not null; // unpacking: nothing counted

        _size.Text = summary is { } s ? SizeFormatter.Human(s.Bytes) : "?";
        _files.Text = summary is { } f ? f.Files.ToString("N0", CultureInfo.CurrentCulture) : "?";
        _folders.Text = summary is { } d ? d.Folders.ToString("N0", CultureInfo.CurrentCulture) : "?";
    }

    /// <summary>The contents of the folders counted in the background; the numbers follow when done.</summary>
    public async Task CountAsync()
    {
        if (_options.Count is not { } count || _found is not null)
        {
            return;
        }
        try
        {
            var counted = await count(_counting.Token);
            if (_found is null)
            {
                ShowSummary(counted);
            }
        }
        catch (OperationCanceledException)
        {
            // closed meanwhile
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // the numbers known at once stay
        }
    }

    /// <summary>The name part of a single file target is selected (typing replaces the name, the folder stays).</summary>
    private void SelectTargetName()
    {
        var text = _target.Text ?? string.Empty;
        var cut = text.LastIndexOfAny(['\\', '/']) + 1;
        if (cut > 0 && cut < text.Length)
        {
            var dot = text.LastIndexOf('.');
            _target.SelectionStart = cut;
            _target.SelectionEnd = dot > cut ? dot : text.Length;
        }
        else
        {
            _target.SelectAll();
        }
    }

    private async Task PickFromTreeAsync()
    {
        if (_options.PickFolder is { } pick && await pick(this, _target.Text ?? string.Empty) is { } folder)
        {
            UseFolder(folder);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F2:
                Finish(queue: true, switchOperation: false);
                break;
            case Key.F7:
                TargetMenu().ShowAt(_targetButton);
                break;
            case Key.D when e.KeyModifiers == KeyModifiers.Control:
                var hotlistMenu = new MenuFlyout();
                AddHotlist(hotlistMenu.Items);
                if (hotlistMenu.Items.Count > 0)
                {
                    hotlistMenu.ShowAt(_targetButton);
                }
                break;
            case Key.F8 when _options.FindFiles is not null:
                _ = FindFilesAsync();
                break;
            case Key.F10 when _options.PickFolder is not null:
                _ = PickFromTreeAsync();
                break;
            default:
                base.OnKeyDown(e);
                return;
        }
        e.Handled = true;
    }

    /// <summary>OK / F2 Queue (with <paramref name="switchOperation"/>: the other operation, from the right click menu).</summary>
    public void Finish(bool queue, bool switchOperation)
    {
        var text = _target.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }
        var filter = _found is null && _filter.Text?.Trim() is { Length: > 0 } f && f is not "*" and not "*.*" ? f : null;
        Result = new CopyDialogResult(text, s_policies[Math.Max(0, _policy.SelectedIndex)].Policy, _verify.IsChecked == true, filter, queue)
        {
            Move = switchOperation ? !_move : _move,
            CopyPermissions = OperatingSystem.IsWindows() && _permissions.IsChecked == true,
            SkipUnreadable = _skipUnreadable.IsChecked == true,
            OverwriteProtected = _overwriteProtected.IsChecked == true,
            ToAllTargetFolders = _allTargets.IsEnabled && _allTargets.IsChecked == true,
            Items = _found,
        };
        if (filter is not null && _found is null && _options.Settings is { } settings)
        {
            // the filters used come back in the drop-down
            settings.CopyFilterList.RemoveAll(v => string.Equals(v, filter, StringComparison.OrdinalIgnoreCase));
            settings.CopyFilterList.Insert(0, filter);
            if (settings.CopyFilterList.Count > 20)
            {
                settings.CopyFilterList.RemoveRange(20, settings.CopyFilterList.Count - 20);
            }
        }
        Close();
    }

    /// <summary>Sets the check boxes of the advanced options (tests).</summary>
    public void SetAdvanced(bool skipUnreadable, bool overwriteProtected, bool allTargets, bool permissions = false)
    {
        _skipUnreadable.IsChecked = skipUnreadable;
        _overwriteProtected.IsChecked = overwriteProtected;
        _allTargets.IsChecked = allTargets;
        _permissions.IsChecked = permissions;
    }

    public int PolicyIndex
    {
        get => _policy.SelectedIndex;
        set => _policy.SelectedIndex = value;
    }
}
