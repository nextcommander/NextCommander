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

    /// <summary>What each answer does, under the answer's list (in the order of <see cref="s_policies"/>).</summary>
    private static readonly string[] s_policyDescriptions =
    [
        "You decide for each conflicting file.",
        "Existing files in the destination are replaced.",
        "Existing files are kept; those files are not copied.",
        "Replaces a file only when the destination copy is older.",
        "Copied files get a new name, e.g. Movie (2).mkv.",
        "Existing files are renamed before copying.",
        "Replaces a file only when the source is larger.",
        "Replaces a file only when the source is smaller.",
        "Older files in the destination are renamed; newer ones are skipped.",
    ];

    /// <summary>Quick filters under the filter box: the patterns they add or take out.</summary>
    private static readonly (string Text, string Patterns)[] s_presets =
    [
        ("Video", "*.mkv *.mp4 *.avi *.mov"),
        ("Subtitles", "*.srt *.sub *.ass"),
        ("Images", "*.jpg *.png *.webp"),
        ("Info files", "*.nfo *.txt"),
    ];

    private readonly bool _move;
    private readonly CopyDialogOptions _options;
    private readonly TextBox _target;
    private readonly TextBox _filter = new() { PlaceholderText = "*.* (all files), e.g. *.jpg *.png | thumbs\\", Classes = { "nc-mono", "noloc" } };
    private readonly ComboBox _policy;
    private readonly CheckBox _verify;
    private readonly CheckBox _permissions = new();
    private readonly CheckBox _skipUnreadable = new();
    private readonly CheckBox _overwriteProtected = new();
    private readonly CheckBox _allTargets = new();
    private readonly TextBlock _size = new() { FontWeight = FontWeight.SemiBold, Classes = { "nc-meta", "noloc" } };
    private readonly TextBlock _files = new() { FontWeight = FontWeight.SemiBold, Classes = { "nc-meta", "noloc" } };
    private readonly TextBlock _folders = new() { FontWeight = FontWeight.SemiBold, Classes = { "nc-meta", "noloc" } };
    private readonly TextBlock _policyDescription = new() { Classes = { "nc-hint" }, MinHeight = 17 };
    private readonly TextBlock _error = new() { IsVisible = false, TextWrapping = TextWrapping.Wrap };
    private readonly Services.Design.NcDisclosure _advanced;
    private readonly ToggleButton _pin;
    private readonly IReadOnlyList<string> _suggestions;
    private readonly CancellationTokenSource _counting = new();
    private readonly Button _targetButton;
    private readonly Button _filterButton;
    private readonly List<(ToggleButton Chip, string[] Patterns)> _presetChips = [];
    private CopySummary? _summary;
    private IReadOnlyList<Core.Vfs.VfsEntry>? _found;
    private bool _showingFound;
    private bool _syncingPresets;
    private readonly WrapPanel _counts = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };

    public CopyDialog(bool move, string description, string target, bool verifyDefault, IReadOnlyList<string>? suggestions = null, CopyDialogOptions? options = null)
        : base(move ? "Move / Rename" : "Copy", 640)
    {
        _move = move;
        _options = options ?? new CopyDialogOptions();
        _suggestions = suggestions ?? [];
        var settings = _options.Settings;
        Services.Design.Nc.Apply(this);

        // what is copied: a folder badge, the description, the size and the counts
        var badge = new Border
        {
            Classes = { "nc-badge-lg" },
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(Services.Design.Nc.Icons.FolderFilled), Width = 26, Height = 26, Stretch = Stretch.Uniform },
        };
        ((Avalonia.Controls.Shapes.Path)badge.Child)[!Avalonia.Controls.Shapes.Shape.FillProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcFolderBrush");
        _counts.Children.AddRange([
            Meta(Services.Design.Nc.Icons.Disk, _size, null),
            Meta(Services.Design.Nc.Icons.File, _files, "files"),
            Meta(Services.Design.Nc.Icons.Folder, _folders, "folders"),
        ]);
        var head = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left);
        head.Children.Add(badge);
        head.Children.Add(new StackPanel
        {
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = description, Classes = { "nc-title" } }, _counts },
        });

        // target with its list, + F7
        _target = new TextBox { Text = target, Classes = { "nc-mono", "noloc" } };
        _targetButton = SideButton("F7", "The target list and the directory hotlist (F7, Ctrl+D)");
        _targetButton.Click += (_, _) => TargetMenu().ShowAt(_targetButton);
        var targetField = Services.Design.Nc.Field("Destination",
            Services.Design.Nc.Line(ListBox(_target, Services.Design.Nc.Icons.Folder, TargetChoices, "Recent destinations"), _targetButton),
            "Recent folders in the list");
        Completion = PathAutoComplete.Attach(_target);

        // "Only files of this type" with + F8 and the quick filters
        _filterButton = SideButton("F8", "Find files: search, and copy the files found (F8)");
        _filterButton.IsEnabled = _options.FindFiles is not null;
        _filterButton.Click += async (_, _) => await FindFilesAsync();
        var presets = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (text, patterns) in s_presets)
        {
            var chip = Services.Design.Nc.Chip(text);
            chip.Margin = new Thickness(0, 0, 6, 6);
            var list = patterns.Split(' ');
            chip.Click += (_, _) => TogglePreset(list);
            _presetChips.Add((chip, list));
            presets.Children.Add(chip);
        }
        var filterField = Services.Design.Nc.Field("Only files of this type",
            Services.Design.Nc.Line(ListBox(_filter, Services.Design.Nc.Icons.Filter, FilterChoices, "Saved filters"), _filterButton),
            "Separate with spaces · | excludes", presets);
        _filter.TextChanged += (_, _) =>
        {
            if (!_showingFound && _found is not null)
            {
                // typing a filter again: the selection is copied as before
                _found = null;
                ShowSummary(_options.Summary);
            }
            SyncPresets();
        };
        ShowSummary(_options.Summary);

        // NTFS permissions (Windows) and Verify
        _verify = new CheckBox { IsChecked = verifyDefault };
        var options1 = new List<Border>();
        if (OperatingSystem.IsWindows())
        {
            options1.Add(Services.Design.Nc.Option(_permissions, "Copy NTFS permissions", "May need administrator rights", "Admin"));
        }
        options1.Add(Services.Design.Nc.Option(_verify, "Verify", "Compare each file with its copy after writing"));
        _error[!TextBlock.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("NcBadBrush");

        // Advanced options: the answer when a file exists (pinned open, saved as the default) and the other switches
        _policy = new ComboBox
        {
            ItemsSource = s_policies.Select((p, i) => $"{i + 1}. {p.Text}").ToList(),
            SelectedIndex = Math.Max(0, Array.FindIndex(s_policies, p => p.Policy == DefaultPolicy)),
            Classes = { "nc-select" },
        };
        _pin = Services.Design.Nc.IconButton<ToggleButton>(Services.Design.Nc.Icons.Pin, "Always open the copy dialog with these options");
        _pin.IsChecked = settings?.CopyDialogExpanded == true;
        _pin.IsCheckedChanged += (_, _) =>
        {
            if (settings is not null)
            {
                settings.CopyDialogExpanded = _pin.IsChecked == true;
            }
            if (_pin.IsChecked == true)
            {
                ShowAdvanced(true);
            }
        };
        var save = Services.Design.Nc.IconButton<Button>(Services.Design.Nc.Icons.Save, "Use this answer by default");
        save.Click += (_, _) => SaveDefaultPolicy();
        _allTargets.IsEnabled = _options.TargetFolders > 0;
        var allTargetsHint = _options.TargetFolders > 0
            ? string.Format(CultureInfo.CurrentCulture, "{0} folders are selected in the target panel", _options.TargetFolders)
            : "Select more than one folder in the target panel";
        var advancedBody = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                Services.Design.Nc.Field("If a file already exists", _policy, null, _policyDescription),
                Services.Design.Nc.Options(
                    Services.Design.Nc.Option(_skipUnreadable, "Skip files that can't be opened for reading"),
                    Services.Design.Nc.Option(_overwriteProtected, "Overwrite or delete read-only, hidden and system files"),
                    Services.Design.Nc.Option(_allTargets, "Copy to all selected folders and links in the target panel", allTargetsHint)),
            },
        };
        advancedBody.Children[0].Margin = new Thickness(0, 10, 0, 0);
        if (advancedBody.Children[1] is Border inner)
        {
            // the switches of the advanced options are not framed again
            inner.BorderThickness = new Thickness(0);
            inner.Padding = new Thickness(0);
            inner.Margin = new Thickness(-10, 0);
        }
        _advanced = new Services.Design.NcDisclosure("Advanced options", advancedBody, _pin, save);
        _policy.SelectionChanged += (_, _) => ShowPolicy();
        ShowPolicy();

        // Tree · Cancel, Add to queue, Copy / Move (right click on the last two: the other operation)
        var tree = Services.Design.Nc.Button("Tree", "ghost", icon: Services.Design.Nc.Icons.Tree);
        tree.IsEnabled = _options.PickFolder is not null;
        tree.Click += async (_, _) => await PickFromTreeAsync();
        var cancel = Services.Design.Nc.Button("Cancel", "", "Esc");
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        var queue = Services.Design.Nc.Button("Add to queue", "", "F2");
        queue.Click += (_, _) => Finish(queue: true, switchOperation: false);
        var ok = Services.Design.Nc.Button(move ? "Move" : "Copy", "primary", "Enter");
        ok.IsDefault = true;
        ok.Click += (_, _) => Finish(queue: false, switchOperation: false);
        foreach (var (button, toQueue) in new[] { (ok, false), (queue, true) })
        {
            var other = new MenuItem { Header = move ? "Copy" : "Move" };
            other.Click += (_, _) => Finish(toQueue, switchOperation: true);
            button.ContextMenu = new ContextMenu { Items = { other } };
            button.ContextMenu.Opening += (_, _) => other.Icon = settings?.ShowMenuIcons != false ? MenuIcons.Create(move ? "nc_Copy" : "nc_RenMov") : null;
        }
        var footer = Services.Design.Nc.Footer([tree], cancel, queue, ok);

        var content = new StackPanel
        {
            Spacing = 20,
            Margin = new Thickness(24, 18, 24, 22),
            Children = { head, targetField, filterField, Services.Design.Nc.Options([.. options1]), _error, _advanced },
        };
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(content);
        Content = root;
        ShowAdvanced(settings?.CopyDialogExpanded == true);

        Opened += async (_, _) =>
        {
            _target.Focus();
            SelectTargetName();
            await CountAsync();
        };
        Closed += (_, _) => _counting.Cancel();
    }

    /// <summary>An icon, a number and its word ("35 files").</summary>
    private static StackPanel Meta(string icon, TextBlock value, string? word)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(0, 0, 14, 0), Children = { Services.Design.Nc.Icon(icon, 13, "NcMutedBrush", 1.4), value } };
        if (word is not null)
        {
            row.Children.Add(new TextBlock { Text = word, Classes = { "nc-meta" } });
        }
        return row;
    }

    /// <summary>The "+" button beside a field with its key (F7, F8).</summary>
    private static Button SideButton(string key, string tip)
    {
        var button = Services.Design.Nc.Button(string.Empty, "side", key, Services.Design.Nc.Icons.Plus);
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, "+ " + key);
        return button;
    }

    /// <summary>The chosen answer: its description and the line of the closed Advanced options.</summary>
    private void ShowPolicy()
    {
        var index = Math.Max(0, _policy.SelectedIndex);
        _policyDescription.Text = s_policyDescriptions[index];
        _advanced.Summary = string.Format(CultureInfo.CurrentCulture, "If file exists: {0}", s_policies[index].Text);
    }

    /// <summary>A quick filter adds its patterns to the filter (before "|"), or takes them out when all are there.</summary>
    private void TogglePreset(string[] patterns)
    {
        var text = _filter.Text ?? string.Empty;
        var bar = text.IndexOf('|', StringComparison.Ordinal);
        var include = (bar >= 0 ? text[..bar] : text).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var exclude = bar >= 0 ? text[(bar + 1)..].Trim() : string.Empty;
        if (patterns.All(p => include.Contains(p, StringComparer.OrdinalIgnoreCase)))
        {
            include.RemoveAll(p => patterns.Contains(p, StringComparer.OrdinalIgnoreCase));
        }
        else
        {
            include.AddRange(patterns.Where(p => !include.Contains(p, StringComparer.OrdinalIgnoreCase)));
        }
        _filter.Text = string.Join(' ', include) + (exclude.Length > 0 ? " | " + exclude : string.Empty);
        SyncPresets();
    }

    /// <summary>A quick filter is pressed while all its patterns are in the filter.</summary>
    private void SyncPresets()
    {
        if (_syncingPresets)
        {
            return;
        }
        _syncingPresets = true;
        var text = _filter.Text ?? string.Empty;
        var bar = text.IndexOf('|', StringComparison.Ordinal);
        var include = (bar >= 0 ? text[..bar] : text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var (chip, patterns) in _presetChips)
        {
            chip.IsChecked = patterns.All(p => include.Contains(p, StringComparer.OrdinalIgnoreCase));
        }
        _syncingPresets = false;
    }

    public CopyDialogResult? Result { get; private set; }

    /// <summary>Path completion of the target box.</summary>
    public PathAutoComplete Completion { get; }

    public TextBox TargetBox => _target;

    public TextBox FilterBox => _filter;

    public bool AdvancedShown => _advanced.IsOpen;

    /// <summary>The size and count texts (tests).</summary>
    public string SummaryText => $"{_size.Text} | {_files.Text} | {_folders.Text}";

    /// <summary>Opens or closes the advanced options.</summary>
    public void ShowAdvanced(bool show) => _advanced.IsOpen = show;

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

    /// <summary>A field with its icon and a drop-down list (built when it opens; Alt+Down opens it too).</summary>
    private static Control ListBox(TextBox box, string icon, Func<IReadOnlyList<string>> choices, string dropName)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        var drop = new Button { Content = Services.Design.Nc.Icon(Services.Design.Nc.Icons.ChevronDown, 14, null, 1.6), Flyout = flyout, Focusable = false, Classes = { "nc-tail", "flat" } };
        Avalonia.Automation.AutomationProperties.SetName(drop, dropName);
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
        return Services.Design.Nc.Combo(box, icon, drop);
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
