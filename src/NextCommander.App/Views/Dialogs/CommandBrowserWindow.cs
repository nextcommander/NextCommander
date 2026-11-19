using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using NextCommander.App.Services;
using NextCommander.Core.Commands;
using NextCommander.Core.Config;
using NextCommander.Core.Localization;

namespace NextCommander.App.Views.Dialogs;

/// <summary>What the commands and shortcuts window works with (set by the main window).</summary>
/// <param name="Categories">Command → the main menu it is in ("Files", "Show" ...); others are "Other".</param>
/// <param name="FunctionKeys">The commands of the function key bar.</param>
/// <param name="Run">Runs a command (after the window closed).</param>
/// <param name="AddToButtonBar">Adds a button for the command (name, description).</param>
public sealed record CommandBrowserRequest(
    IReadOnlyList<CommandDefinition> Commands,
    HotkeyMap Hotkeys,
    AppSettings Settings,
    Action SaveSettings,
    Func<string, Task> Run,
    Action<string, string> AddToButtonBar,
    IReadOnlyDictionary<string, string>? Categories = null,
    IReadOnlySet<string>? FunctionKeys = null);

/// <summary>
/// Commands and shortcuts (nc_CommandBrowser): every command by the menu it is in, with favorites, recently run and
/// changed shortcuts; a search over names, descriptions and shortcuts (or by pressing a key combination); on the right the
/// chosen command with its shortcuts (add by pressing keys, remove, back to the default, a warning when the keys are
/// taken), where else it is, Run now and Add to button bar. Shortcut changes are saved at once.
/// </summary>
public sealed class CommandBrowserWindow : Window
{
    /// <summary>The categories in the order of the main menu, with their glyphs.</summary>
    private static readonly (string Name, string Glyph)[] s_categories =
    [
        ("Files", "MiDoc"), ("Edit", "MiPencil"), ("Mark", "MiSelectAll"), ("Commands", "MiHammer"), ("Net", "MiNetwork"),
        ("Show", "MiEye"), ("Configuration", "MiGear"), ("Start", "MiPower"), ("Help", "MiInfo"), ("Other", "MiTag"),
    ];

    private const int MaxRecent = 20;
    private static readonly FontFamily s_mono = new("Cascadia Mono,Consolas,Menlo,monospace");
    private static readonly IBrush s_star = new SolidColorBrush(Color.Parse("#D4A000"));
    private static readonly IBrush s_warn = new SolidColorBrush(Color.Parse("#C98A00"));

    private readonly CommandBrowserRequest _request;
    private readonly List<CommandDefinition> _commands;
    private readonly TextBox _search = new() { PlaceholderText = "Search commands, descriptions or shortcuts", MinHeight = 38, FontSize = 14.5, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _byKey = new() { Classes = { "tb" }, Height = 30, MinHeight = 30, Padding = new Thickness(10, 0), Margin = new Thickness(0, 0, 4, 0), Focusable = false };
    private readonly StackPanel _cats = new() { Spacing = 2, Margin = new Thickness(8, 10) };
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Single, Background = Brushes.Transparent };
    private readonly StackPanel _details = new() { Spacing = 20, Margin = new Thickness(20) };
    private readonly StackPanel _info = new() { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _toast = new() { IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 76), Padding = new Thickness(14, 9), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), BoxShadow = BoxShadows.Parse("0 12 36 0 #40000000") };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string _category = "all";
    private string? _current;
    private bool _recording;
    private (string Gesture, string Other)? _conflict;
    private List<CommandDefinition> _shown = [];
    private bool _filling;

    public CommandBrowserWindow(CommandBrowserRequest request)
    {
        _request = request;
        _commands = [.. request.Commands.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
        Title = "Commands and shortcuts";
        Width = 1120;
        Height = 760;
        MinWidth = 900;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _search.InnerLeftContent = new Border { Child = DialogLook.Glyph("MiSearch", 15), Margin = new Thickness(12, 0, 4, 0), Opacity = 0.6 };
        _search.InnerRightContent = _byKey;
        _search.TextChanged += (_, _) =>
        {
            if (!_filling)
            {
                RenderList();
            }
        };
        _byKey.IsCheckedChanged += (_, _) =>
        {
            _filling = true;
            _search.Text = string.Empty;
            _filling = false;
            _search.PlaceholderText = _byKey.IsChecked == true ? "Press a key combination to find its command" : "Search commands, descriptions or shortcuts";
            UpdateByKeyButton();
            RenderList();
            _search.Focus();
        };
        ToolTip.SetTip(_byKey, "Press a key combination to find its command");
        UpdateByKeyButton();

        _list.ItemTemplate = new FuncDataTemplate<Row>((row, _) => row is null ? new Panel() : RowView(row));
        _list.SelectionChanged += (_, _) =>
        {
            if (_filling)
            {
                return; // the list being filled
            }
            if (_list.SelectedItem is Row { Command: { } command } && command.Name != _current)
            {
                _current = command.Name;
                _recording = false;
                _conflict = null;
                RenderDetails();
            }
            else if (_list.SelectedItem is Row { Command: null } && _list.SelectedIndex + 1 < _list.ItemCount)
            {
                _list.SelectedIndex++; // a group's header is not chosen
            }
        };
        _list.DoubleTapped += async (_, e) =>
        {
            if (e.Source is Visual v && Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<ListBoxItem>(v, includeSelf: true) is not null)
            {
                await RunAsync();
            }
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _toast.IsVisible = false;
        };
        _toast[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        _toast[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");

        Content = BuildLayout();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _current = _commands.FirstOrDefault()?.Name;
        Render();
        Opened += (_, _) => _search.Focus();
        Closed += (_, _) => _toastTimer.Stop();
    }

    private sealed record Row(CommandDefinition? Command, string? Header);

    /// <summary>The command chosen on the right.</summary>
    public string? Current => _current;

    /// <summary>The commands listed now (tests).</summary>
    public IReadOnlyList<string> Shown => [.. _shown.Select(c => c.Name)];

    /// <summary>The category chosen: "all", "fav", "recent", "mod" or a menu's name.</summary>
    public string Category
    {
        get => _category;
        set
        {
            _category = value;
            Render();
        }
    }

    /// <summary>The command run when the window closed with Run (null: closed).</summary>
    public string? Result { get; private set; }

    public void Search(string text)
    {
        _filling = true;
        _search.Text = text;
        _filling = false;
        RenderList();
    }

    /// <summary>The "by shortcut" mode: the search box takes a key combination.</summary>
    public bool ByShortcut
    {
        get => _byKey.IsChecked == true;
        set => _byKey.IsChecked = value;
    }

    public void Select(string command)
    {
        _current = command;
        _recording = false;
        _conflict = null;
        Render();
    }

    /// <summary>The warning shown when a shortcut is taken: (keys, the command using them).</summary>
    public (string Gesture, string Other)? Conflict => _conflict;

    // ---------------------------------------------------------------- shortcuts

    private static string Normalize(string gesture)
    {
        try
        {
            return KeyGesture.Parse(gesture).ToString();
        }
        catch (ArgumentException)
        {
            return gesture;
        }
    }

    private static string NameOf(string commandLine) => commandLine.Split(' ', 2)[0];

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The shortcuts of a command now.</summary>
    public IReadOnlyList<string> KeysOf(string command) =>
        [.. _request.Hotkeys.Bindings.Where(b => Same(NameOf(b.Command), command)).Select(b => b.Gesture)];

    private bool IsDefault(string gesture, string command) =>
        HotkeyMap.Defaults.TryGetValue(gesture, out var d) && Same(NameOf(d), command);

    /// <summary>A command whose shortcuts differ from the defaults.</summary>
    public bool IsModified(string command) =>
        _request.Settings.Hotkeys.Any(h => Same(NameOf(h.Value), command)
            || (HotkeyMap.Defaults.TryGetValue(h.Key, out var d) && Same(NameOf(d), command)));

    private string? CommandOn(string gesture)
    {
        var normal = Normalize(gesture);
        return _request.Hotkeys.Bindings.FirstOrDefault(b => Same(Normalize(b.Gesture), normal)).Command;
    }

    private void Changed()
    {
        _request.Hotkeys.Rebuild();
        _request.SaveSettings();
    }

    /// <summary>Adds keys to the chosen command; keys of another command give a warning first (unless <paramref name="force"/>).</summary>
    public void AssignShortcut(string gesture, bool force = false)
    {
        if (_current is not { } command)
        {
            return;
        }
        _recording = false;
        gesture = Normalize(gesture);
        if (KeysOf(command).Any(k => Same(Normalize(k), gesture)))
        {
            Toast("That shortcut is already set");
            RenderDetails();
            return;
        }
        if (CommandOn(gesture) is { } other && !Same(NameOf(other), command) && !force)
        {
            _conflict = (gesture, NameOf(other));
            RenderDetails();
            return;
        }
        var existing = _request.Settings.Hotkeys.Keys.FirstOrDefault(k => Same(Normalize(k), gesture)) ?? gesture;
        if (IsDefault(existing, command))
        {
            _request.Settings.Hotkeys.Remove(existing);
        }
        else
        {
            _request.Settings.Hotkeys[existing] = command;
        }
        _conflict = null;
        Changed();
        Render();
        Toast(string.Format(CultureInfo.CurrentCulture, "{0} now runs {1}", gesture, Localizer.T(Describe(command))));
    }

    /// <summary>Takes a shortcut away from the chosen command.</summary>
    public void RemoveShortcut(string gesture)
    {
        var existing = _request.Settings.Hotkeys.Keys.FirstOrDefault(k => Same(Normalize(k), Normalize(gesture)));
        if (HotkeyMap.Defaults.Keys.FirstOrDefault(k => Same(Normalize(k), Normalize(gesture))) is { } builtIn)
        {
            _request.Settings.Hotkeys[existing ?? builtIn] = string.Empty; // an empty value turns a default off
        }
        else if (existing is not null)
        {
            _request.Settings.Hotkeys.Remove(existing);
        }
        Changed();
        Render();
        Toast(string.Format(CultureInfo.CurrentCulture, "{0} removed", gesture));
    }

    /// <summary>The chosen command's shortcuts as they were at the start.</summary>
    public void ResetShortcuts()
    {
        if (_current is not { } command)
        {
            return;
        }
        foreach (var key in _request.Settings.Hotkeys.Where(h => Same(NameOf(h.Value), command)
            || (HotkeyMap.Defaults.TryGetValue(h.Key, out var d) && Same(NameOf(d), command))).Select(h => h.Key).ToList())
        {
            _request.Settings.Hotkeys.Remove(key);
        }
        Changed();
        Render();
    }

    private void StartRecording()
    {
        _recording = true;
        _conflict = null;
        RenderDetails();
    }

    // ---------------------------------------------------------------- data

    private string Describe(string command) => _commands.FirstOrDefault(c => Same(c.Name, command))?.Description ?? command;

    private string CategoryOf(CommandDefinition command) =>
        _request.Categories?.GetValueOrDefault(command.Name) is { } c && s_categories.Any(x => x.Name == c) ? c : "Other";

    private static string GlyphOf(string category) => s_categories.FirstOrDefault(c => c.Name == category).Glyph ?? "MiTag";

    private bool IsFavorite(string command) => _request.Settings.FavoriteCommands.Contains(command, StringComparer.OrdinalIgnoreCase);

    public void ToggleFavorite(string command)
    {
        var list = _request.Settings.FavoriteCommands;
        if (list.RemoveAll(c => Same(c, command)) == 0)
        {
            list.Add(command);
        }
        _request.SaveSettings();
        Render();
    }

    private bool InCategory(CommandDefinition c, string category) => category switch
    {
        "all" => true,
        "fav" => IsFavorite(c.Name),
        "recent" => _request.Settings.RecentCommands.Contains(c.Name, StringComparer.OrdinalIgnoreCase),
        "mod" => IsModified(c.Name),
        _ => CategoryOf(c) == category,
    };

    private int Score(CommandDefinition c, string query)
    {
        if (query.Length == 0)
        {
            return 1;
        }
        var keys = KeysOf(c.Name);
        var text = Localizer.T(c.Description);
        var hay = string.Join(' ', [c.Name, c.Description, text, Localizer.T(CategoryOf(c)), .. keys]).ToLowerInvariant();
        if (!query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => hay.Contains(w, StringComparison.Ordinal)))
        {
            return 0;
        }
        var name = c.Name.ToLowerInvariant();
        var score = 1;
        if (name == query || name == "nc_" + query)
        {
            score += 50;
        }
        if (name.Contains(query, StringComparison.Ordinal))
        {
            score += 10;
        }
        if (text.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
        {
            score += 8;
        }
        if (keys.Any(k => k.Equals(query, StringComparison.OrdinalIgnoreCase)))
        {
            score += 40;
        }
        return score;
    }

    private List<CommandDefinition> Filtered()
    {
        var query = (_search.Text ?? string.Empty).Trim();
        var list = _commands.Where(c => InCategory(c, _category));
        if (ByShortcut)
        {
            return query.Length == 0 ? [.. list] : [.. list.Where(c => KeysOf(c.Name).Any(k => Same(Normalize(k), Normalize(query))))];
        }
        var lower = query.ToLowerInvariant();
        var scored = list.Select(c => (Command: c, Score: Score(c, lower))).Where(x => x.Score > 0);
        if (lower.Length > 0)
        {
            scored = scored.OrderByDescending(x => x.Score);
        }
        else if (_category == "recent")
        {
            var recent = _request.Settings.RecentCommands;
            scored = scored.OrderBy(x => recent.FindIndex(r => Same(r, x.Command.Name)));
        }
        return [.. scored.Select(x => x.Command)];
    }

    // ---------------------------------------------------------------- layout

    private Control BuildLayout()
    {
        var top = new Border { Child = _search, Padding = new Thickness(18, 14), BorderThickness = new Thickness(0, 0, 0, 1) };
        top[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");

        var cats = new Border { Child = new ScrollViewer { Content = _cats }, BorderThickness = new Thickness(0, 0, 1, 0) };
        cats[!Border.BackgroundProperty] = new DynamicResourceExtension("FmToolBarBrush");
        cats[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        var details = new Border { Child = new ScrollViewer { Content = _details }, BorderThickness = new Thickness(1, 0, 0, 0) };
        details[!Border.BackgroundProperty] = new DynamicResourceExtension("FmToolBarBrush");
        details[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(PaddingProperty, new Thickness(0)), new Setter(MinHeightProperty, 0.0) },
        });
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*,330") };
        Grid.SetColumn(_list, 1);
        Grid.SetColumn(details, 2);
        body.Children.AddRange([cats, _list, details]);

        var close = new Button { Content = "Close", Classes = { "tb" } };
        close.Click += (_, _) => Close();
        var run = new Button { Content = "Run", IsDefault = true, Classes = { "tb", "primary" }, MinWidth = 80 };
        run.Click += async (_, _) => await RunAsync();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.AddRange([close, run]);
        var foot = new DockPanel { Margin = new Thickness(18, 12) };
        DockPanel.SetDock(buttons, Dock.Right);
        foot.Children.AddRange([buttons, _info]);
        var footBorder = new Border { Child = foot, BorderThickness = new Thickness(0, 1, 0, 0) };
        footBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("FmToolBarBrush");
        footBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footBorder, Dock.Bottom);
        root.Children.AddRange([top, footBorder, body]);
        var layers = new Panel();
        layers.Children.AddRange([root, _toast]);
        return layers;
    }

    private void UpdateByKeyButton()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.AddRange([DialogLook.Glyph("MiKeyboard", 14), new TextBlock { Text = ByShortcut ? "Press keys…" : "By shortcut", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center }]);
        _byKey.Content = row;
        _byKey.Classes.Set("ghost", !ByShortcut);
    }

    /// <summary>A shortcut drawn as key caps: Ctrl + Shift + K.</summary>
    private static Control Kbd(string gesture, double size = 11.5)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var parts = gesture.EndsWith("++", StringComparison.Ordinal) ? [.. gesture[..^2].Split('+'), "+"] : gesture.Split('+');
        foreach (var part in parts.Where(p => p.Length > 0))
        {
            var cap = new Border
            {
                MinWidth = 22,
                Height = 22,
                Padding = new Thickness(6, 0),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1, 1, 1, 2),
                Child = new TextBlock { Text = part, FontSize = size, FontWeight = FontWeight.Medium, Opacity = 0.8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } },
            };
            cap[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
            cap[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            row.Children.Add(cap);
        }
        return row;
    }

    private static Border Card(params Control[] rows)
    {
        var stack = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            var row = new Border { Child = rows[i], BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0) };
            row[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            stack.Children.Add(row);
        }
        var card = new Border { Child = stack, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true };
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return card;
    }

    private static Border Chip(string text, bool translate = true)
    {
        var label = new TextBlock { Text = text, FontSize = 12, Opacity = 0.8 };
        if (!translate)
        {
            label.Classes.Add("noloc");
        }
        var tag = new Border { Child = label, CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2), BorderThickness = new Thickness(1) };
        tag[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundListLowBrush");
        tag[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return tag;
    }

    private static Button SmallButton(string glyph, string text, bool ghost = true)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.AddRange([DialogLook.Glyph(glyph, 13), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }]);
        var button = new Button { Content = row, Classes = { "tb" }, Height = 28, MinHeight = 28, Padding = new Thickness(10, 0), FontSize = 12.5 };
        if (ghost)
        {
            button.Classes.Add("ghost");
        }
        return button;
    }

    private static TextBlock SectionTitle(string text) => new() { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 8) };

    // ---------------------------------------------------------------- render

    private void Render()
    {
        RenderCategories();
        RenderList();
    }

    private void RenderCategories()
    {
        _cats.Children.Clear();
        void Item(string key, string glyph, string text, int count)
        {
            var on = key == _category;
            var marker = new Border { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(-10, 9, 7, 9), IsVisible = on };
            marker[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
            var number = new TextBlock { Text = count.ToString(CultureInfo.CurrentCulture), FontSize = 11.5, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } };
            var row = new DockPanel();
            DockPanel.SetDock(marker, Dock.Left);
            DockPanel.SetDock(number, Dock.Right);
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            label.Children.AddRange([DialogLook.Glyph(glyph, 15), new TextBlock { Text = text, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center }]);
            row.Children.AddRange([marker, number, label]);
            var item = new Border { Child = row, Height = 34, Padding = new Thickness(10, 0), CornerRadius = new CornerRadius(5), Cursor = new Cursor(StandardCursorType.Hand), Focusable = true };
            if (on)
            {
                item[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightListLowBrush");
            }
            else
            {
                item.Background = Brushes.Transparent;
            }
            item.PointerPressed += (_, e) =>
            {
                Category = key;
                e.Handled = true;
            };
            item.KeyDown += (_, e) =>
            {
                if (e.Key is Key.Enter or Key.Space)
                {
                    Category = key;
                    e.Handled = true;
                }
            };
            _cats.Children.Add(item);
        }
        Item("all", "MiColumns", "All commands", _commands.Count);
        Item("fav", "MiStar", "Favorites", _commands.Count(c => IsFavorite(c.Name)));
        Item("recent", "MiHistory", "Recently used", _commands.Count(c => InCategory(c, "recent")));
        Item("mod", "MiKeyboard", "Modified", _commands.Count(c => IsModified(c.Name)));
        var line = new Border { Height = 1, Margin = new Thickness(6, 8) };
        line[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        _cats.Children.Add(line);
        foreach (var (name, glyph) in s_categories)
        {
            var count = _commands.Count(c => CategoryOf(c) == name);
            if (count > 0)
            {
                Item(name, glyph, name, count);
            }
        }
    }

    private void RenderList()
    {
        _shown = Filtered();
        if (_shown.Count > 0 && !_shown.Any(c => Same(c.Name, _current ?? string.Empty)))
        {
            _current = _shown[0].Name;
        }
        var rows = new List<Row>();
        if (_category == "all" && string.IsNullOrWhiteSpace(_search.Text))
        {
            foreach (var (name, _) in s_categories)
            {
                var group = _shown.Where(c => CategoryOf(c) == name).ToList();
                if (group.Count > 0)
                {
                    rows.Add(new Row(null, name));
                    rows.AddRange(group.Select(c => new Row(c, null)));
                }
            }
        }
        else
        {
            rows.AddRange(_shown.Select(c => new Row(c, null)));
        }
        if (_shown.Count == 0)
        {
            rows.Add(new Row(null, ByShortcut && !string.IsNullOrWhiteSpace(_search.Text)
                ? string.Format(CultureInfo.CurrentCulture, "No command uses {0} yet.", _search.Text) : "No commands match."));
        }
        var filling = _filling;
        _filling = true;
        _list.ItemsSource = rows;
        var index = rows.FindIndex(r => r.Command is { } c && Same(c.Name, _current ?? string.Empty));
        _list.SelectedIndex = index;
        _filling = filling;
        if (index >= 0)
        {
            _list.ScrollIntoView(index);
        }

        _info.Children.Clear();
        _info.Children.Add(new TextBlock { Text = string.Format(CultureInfo.CurrentCulture, "{0} of {1} commands", _shown.Count, _commands.Count), FontSize = 12.5, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center });
        void Hint(string text, params string[] keys)
        {
            var hint = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var key in keys)
            {
                hint.Children.Add(Kbd(key, 11));
            }
            hint.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center });
            _info.Children.Add(hint);
        }
        Hint("move", "↑", "↓");
        Hint("run", "Enter");
        Hint("assign shortcut", "F2");
        RenderDetails();
    }

    /// <summary>A text with the words of the search marked.</summary>
    private TextBlock Marked(string text, double size, IBrush? foreground = null, FontFamily? font = null)
    {
        var block = new TextBlock { FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, Classes = { "noloc" } };
        if (font is not null)
        {
            block.FontFamily = font;
        }
        if (foreground is not null)
        {
            block.Foreground = foreground;
        }
        var words = ByShortcut ? [] : (_search.Text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).ToList();
        var inlines = new InlineCollection();
        var position = 0;
        while (position < text.Length)
        {
            var (at, length) = words.Select(w => (At: text.IndexOf(w, position, StringComparison.CurrentCultureIgnoreCase), w.Length))
                .Where(x => x.At >= 0).OrderBy(x => x.At).FirstOrDefault((-1, 0));
            if (at < 0)
            {
                inlines.Add(new Run(text[position..]));
                break;
            }
            if (at > position)
            {
                inlines.Add(new Run(text[position..at]));
            }
            var mark = new Run(text.Substring(at, length));
            mark[!TextElement.ForegroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
            mark.FontWeight = FontWeight.SemiBold;
            inlines.Add(mark);
            position = at + length;
        }
        block.Inlines = inlines;
        return block;
    }

    private Control RowView(Row row)
    {
        if (row.Command is not { } command)
        {
            var header = new TextBlock { Text = row.Header, FontSize = 12, FontWeight = FontWeight.SemiBold, Opacity = 0.7, Margin = new Thickness(18, 12, 18, 6) };
            if (_shown.Count == 0)
            {
                header.Margin = new Thickness(20, 60);
                header.HorizontalAlignment = HorizontalAlignment.Center;
                header.FontWeight = FontWeight.Normal;
            }
            return header;
        }
        var on = Same(command.Name, _current ?? string.Empty);
        var category = CategoryOf(command);
        var iconBox = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(7), Child = DialogLook.Glyph(GlyphOf(category), 15, accent: on) };
        iconBox[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundListLowBrush");
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        title.Children.Add(Marked(Localizer.T(command.Description), 13.5));
        if (IsFavorite(command.Name))
        {
            title.Children.Add(new TextBlock { Text = "★", Foreground = s_star, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } });
        }
        if (IsModified(command.Name))
        {
            var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
            dot[!Avalonia.Controls.Shapes.Shape.FillProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
            ToolTip.SetTip(dot, "Shortcut modified");
            title.Children.Add(dot);
        }
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = Marked(command.Name, 11.5, font: s_mono);
        name.Opacity = 0.6;
        texts.Children.AddRange([title, name]);
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (var key in KeysOf(command.Name).Take(2))
        {
            keys.Children.Add(Kbd(key));
        }
        var line = new DockPanel { Margin = new Thickness(18, 8) };
        DockPanel.SetDock(iconBox, Dock.Left);
        DockPanel.SetDock(keys, Dock.Right);
        texts.Margin = new Thickness(12, 0);
        line.Children.AddRange([iconBox, keys, texts]);
        var border = new Border { Child = line, BorderThickness = new Thickness(0, 0, 0, 1) };
        border[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return border;
    }

    private void RenderDetails()
    {
        _details.Children.Clear();
        if (_current is null || _commands.FirstOrDefault(c => Same(c.Name, _current)) is not { } command)
        {
            return;
        }
        var category = CategoryOf(command);

        // the icon, the favorite star, the description and the name
        var tint = new Border { CornerRadius = new CornerRadius(10), Opacity = 0.14 };
        tint[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
        var badge = new Panel { Width = 44, Height = 44 };
        badge.Children.AddRange([tint, DialogLook.Glyph(GlyphOf(category), 22, accent: true)]);
        var favorite = new Button
        {
            Content = IsFavorite(command.Name) ? new TextBlock { Text = "★", Foreground = s_star, FontSize = 17, Classes = { "noloc" } } : DialogLook.Glyph("MiStar", 16),
            Classes = { "tb", "ghost", "icon" },
            VerticalAlignment = VerticalAlignment.Top,
        };
        var favoriteTip = IsFavorite(command.Name) ? "Remove from favorites" : "Add to favorites";
        ToolTip.SetTip(favorite, favoriteTip);
        Avalonia.Automation.AutomationProperties.SetName(favorite, favoriteTip);
        favorite.Click += (_, _) => ToggleFavorite(command.Name);
        var head = new DockPanel();
        DockPanel.SetDock(favorite, Dock.Right);
        head.Children.AddRange([favorite, new Border { Child = badge, HorizontalAlignment = HorizontalAlignment.Left }]);
        var copy = new Button { Content = DialogLook.Glyph("MiCopy", 13), Classes = { "tb", "ghost", "icon" }, Width = 24, Height = 24, MinWidth = 24, MinHeight = 24 };
        ToolTip.SetTip(copy, "Copy the command name");
        copy.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(command.Name);
                Toast(string.Format(CultureInfo.CurrentCulture, "Copied {0}", command.Name));
            }
        };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        nameRow.Children.AddRange([new TextBlock { Text = command.Name, FontFamily = s_mono, FontSize = 12.5, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } }, copy]);
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.AddRange([head, new TextBlock { Text = command.Description, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) }, nameRow]);
        _details.Children.Add(titles);

        // the shortcuts
        var keys = KeysOf(command.Name);
        var rows = new List<Control>();
        foreach (var key in keys)
        {
            var remove = new Button { Content = DialogLook.Glyph("MiX", 12), Classes = { "tb", "ghost", "icon" }, Width = 28, Height = 28, MinWidth = 28, MinHeight = 28 };
            ToolTip.SetTip(remove, "Remove");
            Avalonia.Automation.AutomationProperties.SetName(remove, "Remove " + key);
            var which = key;
            remove.Click += (_, _) => RemoveShortcut(which);
            var kind = new TextBlock { Text = IsDefault(key, command.Name) && !_request.Settings.Hotkeys.ContainsKey(key) ? "Default" : "Custom", FontSize = 12, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
            var row = new DockPanel { MinHeight = 44, Margin = new Thickness(10, 0) };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.AddRange([remove, new StackPanel { Orientation = Orientation.Horizontal, Children = { Kbd(key), kind }, VerticalAlignment = VerticalAlignment.Center }]);
            rows.Add(row);
        }
        if (_recording)
        {
            var pulse = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(Color.Parse("#E5484D")), VerticalAlignment = VerticalAlignment.Center };
            var cancel = SmallButton("MiX", "Cancel");
            cancel.Click += (_, _) =>
            {
                _recording = false;
                RenderDetails();
            };
            var text = new TextBlock { Text = "Press a key combination…", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
            text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
            var record = new DockPanel { Margin = new Thickness(10, 8) };
            DockPanel.SetDock(cancel, Dock.Right);
            record.Children.AddRange([cancel, new StackPanel { Orientation = Orientation.Horizontal, Children = { pulse, text } }]);
            var box = new Border { Child = record };
            box[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightListLowBrush");
            rows.Add(box);
        }
        else if (_conflict is { } conflict)
        {
            var message = new WrapPanel { ItemSpacing = 4 };
            message.Children.AddRange([
                Kbd(conflict.Gesture),
                new TextBlock { Text = string.Format(CultureInfo.CurrentCulture, "is already used by {0} ({1}).", Localizer.T(Describe(conflict.Other)), conflict.Other), FontSize = 12.5, Foreground = s_warn, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
            ]);
            var take = SmallButton("MiCheck", "Use it here instead", ghost: false);
            take.Click += (_, _) =>
            {
                var gesture = conflict.Gesture;
                var other = conflict.Other;
                // the other command loses the keys: a custom binding goes, a default one is turned off first
                var custom = _request.Settings.Hotkeys.Keys.FirstOrDefault(k => Same(Normalize(k), gesture));
                if (custom is not null)
                {
                    _request.Settings.Hotkeys.Remove(custom);
                }
                AssignShortcut(gesture, force: true);
                Toast(string.Format(CultureInfo.CurrentCulture, "{0} moved from {1}", gesture, Localizer.T(Describe(other))));
            };
            var drop = SmallButton("MiX", "Cancel");
            drop.Click += (_, _) =>
            {
                _conflict = null;
                RenderDetails();
            };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            actions.Children.AddRange([take, drop]);
            var warn = new StackPanel { Spacing = 8, Margin = new Thickness(10) };
            warn.Children.AddRange([message, actions]);
            rows.Add(new Border { Child = warn, Background = new SolidColorBrush(Color.Parse("#FCE100"), 0.14) });
        }
        else
        {
            var add = SmallButton("MiKeyboard", "Add a shortcut");
            add.Click += (_, _) => StartRecording();
            var none = new TextBlock { Text = keys.Count == 0 ? "No shortcut yet" : string.Empty, FontSize = 12, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center };
            var row = new DockPanel { MinHeight = 44, Margin = new Thickness(10, 0) };
            DockPanel.SetDock(none, Dock.Right);
            row.Children.AddRange([none, new Border { Child = add, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center }]);
            rows.Add(row);
        }
        var shortcutsTitle = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        if (IsModified(command.Name))
        {
            var reset = SmallButton("MiRefresh", "Default");
            ToolTip.SetTip(reset, "Back to the default shortcuts");
            reset.Click += (_, _) => ResetShortcuts();
            DockPanel.SetDock(reset, Dock.Right);
            shortcutsTitle.Children.Add(reset);
        }
        var shortcutsLabel = SectionTitle("Shortcuts");
        shortcutsLabel.Margin = new Thickness(0);
        shortcutsLabel.VerticalAlignment = VerticalAlignment.Center;
        shortcutsTitle.Children.Add(shortcutsLabel);
        var shortcuts = new StackPanel();
        shortcuts.Children.AddRange([shortcutsTitle, Card([.. rows])]);
        _details.Children.Add(shortcuts);

        // the category and where else the command is
        var where = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        where.Children.Add(Chip(category));
        var categorySection = new StackPanel();
        categorySection.Children.AddRange([SectionTitle("Category"), where]);
        _details.Children.Add(categorySection);
        var also = AlsoOn(command.Name);
        if (also.Count > 0)
        {
            var places = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
            foreach (var place in also)
            {
                places.Children.Add(Chip(place));
            }
            var alsoSection = new StackPanel();
            alsoSection.Children.AddRange([SectionTitle("Also on"), places]);
            _details.Children.Add(alsoSection);
        }

        // run, add to the button bar
        var run = new Button { Content = WithGlyph("MiArrowRight", "Run now"), Classes = { "tb" } };
        run.Click += async (_, _) => await RunAsync();
        var bar = new Button { Content = WithGlyph("MiPlus", "Add to button bar"), Classes = { "tb" } };
        bar.Click += (_, _) =>
        {
            _request.AddToButtonBar(command.Name, command.Description);
            RenderDetails();
            Toast(string.Format(CultureInfo.CurrentCulture, "{0} added to the button bar", command.Name));
        };
        var buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        buttons.Children.AddRange([run, bar]);
        _details.Children.Add(buttons);
    }

    private static StackPanel WithGlyph(string glyph, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.AddRange([DialogLook.Glyph(glyph, 14), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }]);
        return row;
    }

    /// <summary>Where else the command is: the button bars, the function key bar.</summary>
    private List<string> AlsoOn(string command)
    {
        var places = new List<string>();
        if (_request.Settings.ButtonBar.Any(b => Same(NameOf(b.Command), command)))
        {
            places.Add("Button bar");
        }
        if (_request.Settings.VerticalButtonBar.Any(b => Same(NameOf(b.Command), command)))
        {
            places.Add("Vertical button bar");
        }
        if (_request.FunctionKeys?.Contains(command) == true)
        {
            places.Add("Function key bar");
        }
        return places;
    }

    private void Toast(string text)
    {
        _toast.Child = new TextBlock { Text = text, FontSize = 13 };
        _toast.IsVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ---------------------------------------------------------------- run, keys

    /// <summary>Closes the window and runs the chosen command (remembered under Recently used).</summary>
    public async Task RunAsync()
    {
        if (_current is not { } command)
        {
            return;
        }
        var recent = _request.Settings.RecentCommands;
        recent.RemoveAll(r => Same(r, command));
        recent.Insert(0, command);
        if (recent.Count > MaxRecent)
        {
            recent.RemoveRange(MaxRecent, recent.Count - MaxRecent);
        }
        _request.SaveSettings();
        Result = command;
        Close();
        await _request.Run(command);
    }

    private void Move(int delta)
    {
        if (_shown.Count == 0)
        {
            return;
        }
        var index = _shown.FindIndex(c => Same(c.Name, _current ?? string.Empty));
        var next = Math.Clamp(index + delta, 0, _shown.Count - 1);
        _current = _shown[next].Name;
        _recording = false;
        _conflict = null;
        var rows = (List<Row>)_list.ItemsSource!;
        var row = rows.FindIndex(r => r.Command is { } c && Same(c.Name, _current));
        _filling = true;
        _list.SelectedIndex = row;
        _filling = false;
        _list.ScrollIntoView(row);
        RenderDetails();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // recording a shortcut: the next key combination (Esc alone cancels)
        if (_recording)
        {
            e.Handled = true;
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                _recording = false;
                RenderDetails();
            }
            else if (HotkeyMap.Describe(e.Key, e.KeyModifiers) is { } gesture)
            {
                AssignShortcut(gesture);
            }
            return;
        }
        // the search by shortcut: the keys pressed go into the box
        if (ByShortcut && _search.IsFocused)
        {
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
            {
                ByShortcut = false;
            }
            else if (HotkeyMap.Describe(e.Key, e.KeyModifiers) is { } gesture)
            {
                _search.Text = gesture;
            }
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Down or Key.Up when e.KeyModifiers == KeyModifiers.None:
                Move(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;
            case Key.PageDown or Key.PageUp when e.KeyModifiers == KeyModifiers.None:
                Move(e.Key == Key.PageDown ? 10 : -10);
                e.Handled = true;
                break;
            case Key.Enter when e.Source is not Button:
                _ = RunAsync();
                e.Handled = true;
                break;
            case Key.F2:
                StartRecording();
                e.Handled = true;
                break;
            case Key.F when e.KeyModifiers == KeyModifiers.Control:
                _search.Focus();
                _search.SelectAll();
                e.Handled = true;
                break;
            case Key.Escape when _search.IsFocused && !string.IsNullOrEmpty(_search.Text):
                _search.Text = string.Empty;
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }
}
