using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using NextCommander.Core.Config;
using NextCommander.Core.Plugins;

namespace NextCommander.App.Views.Dialogs;

/// <summary>What the plugins window works with (set by the main window).</summary>
/// <param name="Loaded">The plugins loaded at the start.</param>
/// <param name="Roots">The plugin folders: the settings folder's first (where installing puts a plugin), then the program's.</param>
/// <param name="Open">Opens a plugin: its entry point when it has one (rclone remotes ...), else its folder in the panel.</param>
/// <param name="Restart">Starts the program again (false: it could not).</param>
public sealed record PluginsRequest(
    IReadOnlyList<LoadedPlugin> Loaded,
    IReadOnlyList<string> Roots,
    AppSettings Settings,
    Action SaveSettings,
    Func<string, Task<string?>> PickFile,
    Func<LoadedPlugin?, string, Task> Open,
    Func<bool> Restart);

/// <summary>
/// The plugins (Configuration > Plugins..., nc_Plugins): every installed plugin with its version, state, what it adds
/// (file systems, columns, packers, viewers) and its folder; a search, filters with counts, and per plugin: copy its
/// path, open its folder, turn it on or off. Installing from a zip file puts a plugin into the settings folder. Plugins
/// are loaded at the start, so changes ask for a restart ("Restart now").
/// </summary>
public sealed class PluginsDialog : Window
{
    /// <summary>One line: a plugin found in a plugin folder (or only named in the settings as turned off).</summary>
    public sealed record PluginRow(string Name, PluginManifest? Manifest, string? Folder, LoadedPlugin? Loaded)
    {
        public int FileSystems => Loaded?.FileSystems.Count ?? 0;

        public int Columns => Loaded?.Columns.Count ?? 0;

        public int Packers => Loaded?.Packers.Count ?? 0;

        public int Viewers => Loaded?.Viewers.Count ?? 0;
    }

    private enum Filter
    {
        All,
        FileSystems,
        Columns,
        Disabled,
    }

    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };

    private readonly PluginsRequest _request;
    private readonly TextBox _search = new() { PlaceholderText = "Search by name, type or path", MinHeight = 36, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly StackPanel _list = new() { Margin = new Thickness(12, 4, 12, 12) };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly TextBlock _summary = DialogLook.Subtitle(string.Empty);
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock _note = new() { FontSize = 12, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _restart = new() { Content = "Restart now", IsVisible = false, Margin = new Thickness(10, 0, 0, 0) };
    private readonly Button _open = new() { Content = "Open", IsDefault = true, Classes = { "dialog" } };
    private readonly TextBlock _hints = new() { Text = "↑ ↓ choose · Enter open · Esc close", FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
    private List<PluginRow> _rows = [];
    private List<PluginRow> _shown = [];
    private Filter _filter = Filter.All;
    private int _selected;

    public PluginsDialog(PluginsRequest request)
    {
        _request = request;
        Title = "Plugins";
        Width = 780;
        Height = 620;
        MinWidth = 560;
        MinHeight = 400;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // above: the badge, the title and the summary; install from a file
        var tint = new Border { CornerRadius = new CornerRadius(9), Opacity = 0.16 };
        tint[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
        var badge = new Panel { Width = 34, Height = 34 };
        badge.Children.AddRange([tint, DialogLook.Glyph("MiChip", 19, accent: true)]);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        titles.Children.AddRange([new TextBlock { Text = "Plugins", FontSize = 15, FontWeight = FontWeight.SemiBold }, _summary]);
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.AddRange([badge, titles]);
        var install = DialogLook.IconButton("MiPlus", "Install from a file (a zip file with a plugin, or a plugin.json)", InstallAsync);
        install.Background = Brushes.Transparent;
        install.BorderThickness = new Thickness(0);
        var head = new DockPanel { Margin = new Thickness(20, 14) };
        DockPanel.SetDock(install, Dock.Right);
        head.Children.AddRange([install, titleRow]);

        // the search and the filters
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

        _scroll.Content = _list;

        // below: the keys (or the note about a restart), Close and Open
        var hints = _hints;
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.AddRange([hints, _note, _restart]);
        _note.Margin = new Thickness(0);
        _restart.Click += (_, _) =>
        {
            _request.SaveSettings();
            if (!_request.Restart())
            {
                _note.Text = "Start NextCommander again to use the changes.";
            }
        };
        var close = new Button { Content = "Close", IsCancel = true, Classes = { "dialog" } };
        close.Click += (_, _) => Close();
        _open.Click += async (_, _) => await OpenSelectedAsync();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.AddRange([close, _open]);
        var foot = new DockPanel { Margin = new Thickness(20, 12) };
        DockPanel.SetDock(buttons, Dock.Right);
        foot.Children.AddRange([buttons, left]);

        var root = new DockPanel();
        var headBorder = Bordered(head, new Thickness(0, 0, 0, 1));
        var footBorder = Bordered(foot, new Thickness(0, 1, 0, 0));
        DockPanel.SetDock(headBorder, Dock.Top);
        DockPanel.SetDock(controls, Dock.Top);
        DockPanel.SetDock(footBorder, Dock.Bottom);
        root.Children.AddRange([headBorder, controls, footBorder, _scroll]);
        Content = root;

        KeyDown += OnKeyDownAsync;
        Opened += (_, _) => _search.Focus();
        Reload();
    }

    /// <summary>The plugin lines (tests).</summary>
    public IReadOnlyList<PluginRow> Rows => _rows;

    /// <summary>The lines the search and the filter leave (tests).</summary>
    public IReadOnlyList<PluginRow> Shown => _shown;

    /// <summary>Whether a change waits for a restart (tests).</summary>
    public bool NeedsRestart => _restart.IsVisible;

    public void Search(string text) => _search.Text = text;

    /// <summary>The plugins found in the plugin folders, those loaded, and those turned off in the settings.</summary>
    private void Reload()
    {
        var found = PluginLoader.Discover(_request.Roots);
        var rows = found.Select(f => new PluginRow(f.Name, f.Manifest, f.Folder,
            _request.Loaded.FirstOrDefault(l => string.Equals(l.Folder, f.Folder, StringComparison.OrdinalIgnoreCase)))).ToList();
        rows.AddRange(_request.Settings.DisabledPlugins.Where(d => !rows.Any(r => string.Equals(r.Name, d, StringComparison.OrdinalIgnoreCase)))
            .Select(d => new PluginRow(d, null, null, null)));
        _rows = [.. rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)];
        Render();
    }

    private bool IsDisabled(PluginRow row) => _request.Settings.DisabledPlugins.Contains(row.Name, StringComparer.OrdinalIgnoreCase);

    private bool Matches(PluginRow row, Filter filter) => filter switch
    {
        Filter.FileSystems => row.FileSystems > 0,
        Filter.Columns => row.Columns > 0,
        Filter.Disabled => IsDisabled(row),
        _ => true,
    };

    private void Render()
    {
        var query = _search.Text?.Trim() ?? string.Empty;
        bool Found(PluginRow row) => query.Length == 0
            || $"{row.Name} {row.Folder} {row.Manifest?.Description} {(row.FileSystems > 0 ? "file system" : string.Empty)} {(row.Columns > 0 ? "column" : string.Empty)}"
                .Contains(query, StringComparison.CurrentCultureIgnoreCase);
        _shown = _rows.Where(r => Matches(r, _filter) && Found(r)).ToList();
        _selected = Math.Clamp(_selected, 0, Math.Max(0, _shown.Count - 1));

        var enabled = _rows.Count(r => !IsDisabled(r));
        _summary.Text = _shown.Count == _rows.Count ? $"{_rows.Count} installed, {enabled} enabled" : $"{_shown.Count} of {_rows.Count} shown";
        RenderChips();

        _list.Children.Clear();
        if (_shown.Count == 0)
        {
            _list.Children.Add(Empty());
        }
        for (var i = 0; i < _shown.Count; i++)
        {
            _list.Children.Add(Line(_shown[i], i));
        }
        _open.IsEnabled = _shown.Count > 0;
        UpdateRestart();
    }

    private void RenderChips()
    {
        _chips.Children.Clear();
        foreach (var (filter, text) in new[] { (Filter.All, "All"), (Filter.FileSystems, "File systems"), (Filter.Columns, "Column providers"), (Filter.Disabled, "Disabled") })
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            content.Children.AddRange([
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = _rows.Count(r => Matches(r, filter)).ToString(CultureInfo.CurrentCulture), Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } },
            ]);
            var chip = new ToggleButton
            {
                Content = content,
                IsChecked = filter == _filter,
                Height = 28,
                FontSize = 12,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                Classes = { "chip" },
            };
            var which = filter;
            chip.Click += (_, _) =>
            {
                _filter = which;
                _selected = 0;
                Render();
            };
            _chips.Children.Add(chip);
        }
    }

    private Control Empty()
    {
        var stack = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 60, 0, 0) };
        stack.Children.Add(new Border { Child = DialogLook.Glyph("MiChip", 32), Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center });
        if (_rows.Count == 0)
        {
            stack.Children.Add(new TextBlock { Text = "No plugins installed", FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(new TextBlock
            {
                Text = "A plugin is a folder with a plugin.json in the plugins folder of the settings folder or of the program. + installs one from a zip file.",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = 440,
            });
            var folder = new Button { Content = "Open the plugins folder", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            folder.Click += async (_, _) =>
            {
                Directory.CreateDirectory(_request.Roots[0]);
                await _request.Open(null, _request.Roots[0]);
                Close();
            };
            stack.Children.Add(folder);
        }
        else
        {
            stack.Children.Add(new TextBlock { Text = "No matching plugins", FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(new TextBlock { Text = "Try another search or clear the filter.", FontSize = 12, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center });
        }
        return stack;
    }

    /// <summary>The state of a plugin now and after a restart.</summary>
    private (string Text, string Color) StateOf(PluginRow row)
    {
        var disabled = IsDisabled(row);
        if (row.Folder is null)
        {
            return ("Not installed", "#8E8E93");
        }
        if (row.Loaded?.Error is not null && !disabled)
        {
            return ("Error", "#E5484D");
        }
        if (disabled)
        {
            return row.Loaded is not null ? ("Off after a restart", "#F08A2C") : ("Disabled", "#8E8E93");
        }
        return row.Loaded is null ? ("On after a restart", "#F08A2C") : ("Enabled", "#2FA36B");
    }

    private Control Line(PluginRow row, int index)
    {
        var selected = index == _selected;
        var (glyph, color) = row.FileSystems > 0 ? ("MiDrive", "#3B82F6") : row.Columns > 0 ? ("MiColumns", "#E0A12B")
            : row.Packers > 0 ? ("MiCombine", "#A970FF") : row.Viewers > 0 ? ("MiEye", "#14A3A3") : ("MiChip", "#8E8E93");
        var icon = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new PathIcon { Width = 20, Height = 20, Data = Services.Icons.IconPacks.Glyph(glyph), Foreground = new SolidColorBrush(Color.Parse(color)) },
        };
        icon[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        icon[!Border.BackgroundProperty] = new DynamicResourceExtension("FmPanelHeaderBrush");

        // name, version, state
        var (stateText, stateColor) = StateOf(row);
        var state = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 1), Background = new SolidColorBrush(Color.Parse(stateColor), 0.15), VerticalAlignment = VerticalAlignment.Center };
        var stateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        stateRow.Children.AddRange([
            new Avalonia.Controls.Shapes.Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(Color.Parse(stateColor)), VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = stateText, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(stateColor)) },
        ]);
        state.Child = stateRow;
        if (row.Loaded?.Error is { } error)
        {
            ToolTip.SetTip(state, error);
        }
        var line1 = new WrapPanel { ItemSpacing = 8, LineSpacing = 2, Margin = new Thickness(0, 0, 0, 4) };
        line1.Children.Add(new TextBlock { Text = row.Name, FontWeight = FontWeight.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } });
        if (row.Manifest?.Version is { Length: > 0 } version)
        {
            var ver = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1), VerticalAlignment = VerticalAlignment.Center };
            ver[!Border.BackgroundProperty] = new DynamicResourceExtension("FmPanelHeaderBrush");
            ver.Child = new TextBlock { Text = "v" + version, FontSize = 11, Opacity = 0.75, FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"), Classes = { "noloc" } };
            line1.Children.Add(ver);
        }
        line1.Children.Add(state);

        // what it adds
        var tags = new WrapPanel { ItemSpacing = 6, LineSpacing = 4, Margin = new Thickness(0, 0, 0, 5) };
        void Tag(string tagGlyph, int count, string one, string many, bool always)
        {
            if (count == 0 && !always)
            {
                return;
            }
            var tag = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(7, 2), Opacity = count == 0 ? 0.55 : 1 };
            tag[!Border.BackgroundProperty] = new DynamicResourceExtension("FmPanelHeaderBrush");
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            content.Children.AddRange([DialogLook.Glyph(tagGlyph, 12), new TextBlock { Text = count == 1 ? one : string.Format(CultureInfo.CurrentCulture, many, count), FontSize = 11, VerticalAlignment = VerticalAlignment.Center }]);
            tag.Child = content;
            tags.Children.Add(tag);
        }
        if (row.Loaded is not null)
        {
            Tag("MiDrive", row.FileSystems, "1 file system", "{0} file systems", always: true);
            Tag("MiColumns", row.Columns, "1 column provider", "{0} column providers", always: true);
            Tag("MiCombine", row.Packers, "1 packer", "{0} packers", always: false);
            Tag("MiEye", row.Viewers, "1 viewer", "{0} viewers", always: false);
        }
        else
        {
            tags.Children.Add(new TextBlock
            {
                Text = row.Folder is null ? "Turned off in the settings; its folder is gone."
                    : IsDisabled(row) ? "Turned off: not loaded."
                    : "Not loaded yet: what it adds shows after a restart.",
                FontSize = 11,
                Opacity = 0.6,
            });
        }

        var body = new StackPanel();
        body.Children.AddRange([line1, tags]);
        if (row.Manifest?.Description is { Length: > 0 } description)
        {
            body.Children.Add(new TextBlock { Text = description, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4), Classes = { "noloc" } });
        }
        if (row.Folder is { } folder)
        {
            var path = new TextBlock
            {
                Text = folder,
                FontSize = 11.5,
                Opacity = 0.55,
                FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
                TextTrimming = TextTrimming.LeadingCharacterEllipsis,
                Classes = { "noloc" },
            };
            ToolTip.SetTip(path, folder);
            body.Children.Add(path);
        }

        // the actions: copy the path, open the folder, turn on / off
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Opacity = selected ? 1 : 0 };
        if (row.Folder is { } where)
        {
            actions.Children.Add(Action("MiCopy", "Copy path", async () =>
            {
                if (Clipboard is { } clipboard)
                {
                    await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, where);
                    _note.Text = "Path copied.";
                    UpdateRestart();
                }
            }));
            actions.Children.Add(Action("MiFolderOpen", "Open folder", async () =>
            {
                await _request.Open(null, where);
                Close();
            }));
        }
        var disabledNow = IsDisabled(row);
        actions.Children.Add(Action(row.Folder is null ? "MiX" : "MiPower", row.Folder is null ? "Remove from the list" : disabledNow ? "Enable" : "Disable", () =>
        {
            SetDisabled(row, !disabledNow);
            return Task.CompletedTask;
        }));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 14 };
        Grid.SetColumn(body, 1);
        Grid.SetColumn(actions, 2);
        grid.Children.AddRange([icon, body, actions]);
        var line = new Border
        {
            Child = grid,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 4),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = row,
        };
        if (selected)
        {
            line[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
            line[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightListLowBrush");
        }
        line.PointerEntered += (_, _) => actions.Opacity = 1;
        line.PointerExited += (_, _) => actions.Opacity = index == _selected ? 1 : 0;
        line.PointerPressed += (_, e) =>
        {
            if (e.ClickCount >= 2)
            {
                _ = OpenSelectedAsync();
            }
            else if (_selected != index)
            {
                _selected = index;
                Render();
            }
        };
        return line;
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

    /// <summary>Turns a plugin on or off (in the settings, saved at once; it takes effect after a restart).</summary>
    public void SetDisabled(PluginRow row, bool disabled)
    {
        var others = _request.Settings.DisabledPlugins.Where(d => !string.Equals(d, row.Name, StringComparison.OrdinalIgnoreCase));
        _request.Settings.DisabledPlugins = disabled ? [.. others, row.Name] : [.. others];
        _request.SaveSettings();
        _note.Text = string.Empty;
        Reload(); // a plugin whose folder is gone leaves the list when it is no longer turned off
    }

    /// <summary>A change that needs a restart: a plugin turned on / off against what was loaded, or one installed.</summary>
    private void UpdateRestart()
    {
        var pending = _installed || _rows.Any(r => r.Folder is not null && (r.Loaded is null) != IsDisabled(r));
        _restart.IsVisible = pending;
        if (pending && _note.Text is null or "")
        {
            _note.Text = "Changes take effect after a restart.";
        }
        _hints.IsVisible = !pending && string.IsNullOrEmpty(_note.Text);
    }

    private bool _installed;

    private async Task OpenSelectedAsync()
    {
        if (_shown.Count == 0)
        {
            return;
        }
        var row = _shown[_selected];
        if (row.Folder is null)
        {
            return;
        }
        await _request.Open(row.Loaded, row.Folder);
        Close();
    }

    /// <summary>Installs a plugin from a zip file (or the folder of a plugin.json) into the settings folder's plugins.</summary>
    public async Task InstallAsync()
    {
        if (await _request.PickFile("Install a plugin (zip file or plugin.json)") is not { } file)
        {
            return;
        }
        try
        {
            _note.Text = InstallFrom(file, _request.Roots[0]) is { } name ? $"Installed: {name}." : "No plugin.json found in it.";
            _installed = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            _note.Text = ex.Message;
        }
        Reload();
    }

    /// <summary>
    /// Copies a plugin into <paramref name="pluginRoot"/>: from a zip file (its plugin.json at the top or in one folder) or
    /// from the folder of a plugin.json. Returns its name; null when there is no plugin.json.
    /// </summary>
    public static string? InstallFrom(string file, string pluginRoot)
    {
        string source;
        string? temporary = null;
        if (string.Equals(Path.GetFileName(file), "plugin.json", StringComparison.OrdinalIgnoreCase))
        {
            source = Path.GetDirectoryName(file)!;
        }
        else
        {
            temporary = Path.Combine(Path.GetTempPath(), "nc-plugin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary); // an empty zip file makes no folder
            ZipFile.ExtractToDirectory(file, temporary);
            var manifest = Directory.GetFiles(temporary, "plugin.json", SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault();
            if (manifest is null)
            {
                Directory.Delete(temporary, recursive: true);
                return null;
            }
            source = Path.GetDirectoryName(manifest)!;
        }
        try
        {
            var manifestText = File.ReadAllText(Path.Combine(source, "plugin.json"));
            var name = JsonSerializer.Deserialize<PluginManifest>(manifestText, s_json)?.Name;
            var folderName = !string.IsNullOrWhiteSpace(name) ? name : temporary is not null && source == temporary ? Path.GetFileNameWithoutExtension(file) : Path.GetFileName(source);
            foreach (var bad in Path.GetInvalidFileNameChars())
            {
                folderName = folderName.Replace(bad, '_');
            }
            var target = Path.Combine(pluginRoot, folderName);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true); // the new version (a loaded plugin's files are in use: then this fails)
            }
            CopyFolder(source, target);
            return string.IsNullOrWhiteSpace(name) ? folderName : name;
        }
        finally
        {
            if (temporary is not null && Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var folder in Directory.GetDirectories(source))
        {
            CopyFolder(folder, Path.Combine(target, Path.GetFileName(folder)));
        }
    }

    private void OnKeyDownAsync(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down or Key.Up when _shown.Count > 0:
                _selected = Math.Clamp(_selected + (e.Key == Key.Down ? 1 : -1), 0, _shown.Count - 1);
                Render();
                if (_list.Children.Count > _selected)
                {
                    _list.Children[_selected].BringIntoView();
                }
                e.Handled = true;
                break;
            case Key.Escape:
                e.Handled = true;
                if (!string.IsNullOrEmpty(_search.Text))
                {
                    _search.Text = string.Empty;
                }
                else
                {
                    Close();
                }
                break;
            case Key.F when e.KeyModifiers == KeyModifiers.Control:
                _search.Focus();
                _search.SelectAll();
                e.Handled = true;
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
