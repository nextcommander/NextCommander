using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using NextCommander.App.Services;
using NextCommander.Core.Config;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// The color theme editor (Configuration > Color).
/// <list type="bullet">
/// <item>Above: the name, the light or dark look and a search box.</item>
/// <item>Left: the areas in groups (window, controls, menus, file list ...); each row has its color (a picker and
/// #RRGGBB), what it colors, its key, a contrast check where text sits on it, and "Default" to leave it to the look.</item>
/// <item>Right: a live preview (a click on an area finds its row), accent colors in one click, and how many contrast
/// checks pass.</item>
/// <item>Below: Reset all, how many colors the theme sets, Cancel and Save.</item>
/// </list>
/// Saved into the themes folder of the settings; a shipped theme is saved as a copy there.
/// </summary>
public sealed class ThemeEditorDialog : Window
{
    /// <summary>The groups of the list: the first part of the keys and its title.</summary>
    private static readonly (string Prefix, string Title)[] s_groups =
    [
        ("Accent", "Accent"), ("Window", "Window"), ("Controls", "Controls"), ("Lists", "Lists"), ("MenuBar", "Main menu bar"),
        ("Menu", "Menus"), ("ButtonBar", "Button bar"), ("Drives", "Drive buttons"), ("Tabs", "Folder tabs"), ("Panel", "Panels"),
        ("FileList", "File list"), ("FunctionKeys", "Function keys"), ("Lister", "Lister and editor"),
        ("Compare", "Compare"), ("Merge", "Merge"),
    ];

    /// <summary>Readability: the key, the background it is read on, the minimum contrast (WCAG: 4.5 text, 3 large / UI).</summary>
    private static readonly (string Key, string On, double Minimum)[] s_checks =
    [
        ("Accent", "Window.Background", 3), ("Window.Text", "Window.Background", 4.5), ("Window.ErrorText", "Window.Background", 4.5),
        ("Controls.Border", "Controls.Background", 1.5), ("Controls.SecondaryText", "Window.Background", 4.5),
        ("Window.Text", "Lists.SelectedBackground", 4.5), ("MenuBar.Text", "MenuBar.Background", 4.5), ("Menu.Text", "Menu.Background", 4.5),
        ("Menu.HoverText", "Menu.HoverBackground", 4.5), ("FileList.Text", "FileList.Background", 4.5), ("FileList.MarkedText", "FileList.Background", 3),
        ("FileList.CursorText", "FileList.CursorBackground", 4.5), ("FileList.HeaderText", "FileList.HeaderBackground", 4.5),
        ("Lister.Text", "Lister.Background", 4.5),
    ];

    private static readonly string[] s_accents = ["#0A84FF", "#005FB8", "#30D158", "#00A6A6", "#BF5AF2", "#FF9F0A", "#FF375F", "#8E8E93"];

    private readonly ColorTheme _theme;
    private readonly string _initial;
    private readonly TextBox _name = new() { PlaceholderText = "Theme name", Width = 220, Classes = { "noloc" } };
    private readonly ToggleButton _light = new() { Content = "Light" };
    private readonly ToggleButton _dark = new() { Content = "Dark" };
    private readonly TextBox _filter = new() { PlaceholderText = "Search colors", Width = 220 };
    private readonly StackPanel _groups = new() { Margin = new Thickness(20, 4, 20, 24) };
    private readonly ScrollViewer _listScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Border _preview = new() { CornerRadius = new CornerRadius(8), ClipToBounds = true, BoxShadow = BoxShadows.Parse("0 8 24 0 #24000000") };
    private readonly Border _filePreview = new() { CornerRadius = new CornerRadius(8), ClipToBounds = true, BoxShadow = BoxShadows.Parse("0 8 24 0 #24000000"), Margin = new Thickness(0, 14, 0, 0) };
    private readonly WrapPanel _accents = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly Grid _stats = new() { ColumnDefinitions = new ColumnDefinitions("*,8,*"), Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _info = new() { FontSize = 13, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _unsaved = new() { Text = "· unsaved", FontSize = 13, FontWeight = FontWeight.SemiBold, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly Dictionary<string, Border> _rowOf = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ColorPicker> _pickerOf = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Action> _rowUpdates = [];
    private readonly List<(StackPanel Section, TextBlock Count, string Prefix)> _sections = [];

    public ThemeEditorDialog(ColorTheme theme)
    {
        _theme = theme.Clone();
        LeaveDefaultsToTheLook();
        _initial = Snapshot();
        Title = "Color theme";
        Width = 1100;
        Height = 780;
        MinWidth = 760;
        MinHeight = 480;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // ---- above: name, look, search
        _name.Text = _theme.Name;
        _name.TextChanged += (_, _) => UpdateFooter();
        var look = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (button, first) in new[] { (_light, true), (_dark, false) })
        {
            button.FontSize = 13;
            button.Padding = new Thickness(12, 4);
            button.CornerRadius = new CornerRadius(first ? 5 : 0, first ? 0 : 5, first ? 0 : 5, first ? 5 : 0);
            button.Margin = new Thickness(first ? 0 : -1, 0, 0, 0);
            look.Children.Add(button);
        }
        _light.IsChecked = !_theme.IsDark;
        _dark.IsChecked = _theme.IsDark;
        _light.Click += (_, _) => SetLook(dark: false);
        _dark.Click += (_, _) => SetLook(dark: true);
        _filter.InnerLeftContent = new Border { Child = DialogLook.Glyph("MiSearch", 14), Margin = new Thickness(10, 0, 2, 0), Opacity = 0.6 };
        _filter.TextChanged += (_, _) => Filter();
        var head = new DockPanel { Margin = new Thickness(20, 14) };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        left.Children.AddRange([Labeled("Name", _name), Labeled("Look", look)]);
        DockPanel.SetDock(_filter, Dock.Right);
        head.Children.AddRange([_filter, left]);
        var headBorder = Bordered(head, new Thickness(0, 0, 0, 1));
        var hint = new TextBlock
        {
            Text = "An area left at “default” takes its color from the light or dark look, or from a related area.",
            FontSize = 12,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 10, 20, 0),
        };

        // ---- the colors, in groups
        foreach (var (prefix, title) in s_groups)
        {
            var keys = ColorThemes.Keys.Where(k => k.Key.Split('.')[0] == prefix).ToList();
            if (keys.Count == 0)
            {
                continue;
            }
            var count = new TextBlock { FontSize = 11, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
            var badge = new Border { Child = count, CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 0), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
            badge[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(2, 18, 2, 8) };
            heading.Children.AddRange([new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold }, badge]);
            var card = new StackPanel();
            var first = true;
            foreach (var key in keys)
            {
                var row = Row(key, first);
                first = false;
                _rowOf[key.Key] = row;
                card.Children.Add(row);
            }
            var cardBorder = new Border { Child = card, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true };
            cardBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
            cardBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
            var section = new StackPanel();
            section.Children.AddRange([heading, cardBorder]);
            _sections.Add((section, count, prefix));
            _groups.Children.Add(section);
        }
        _listScroll.Content = _groups;

        // ---- the preview, the accents, readability
        var side = new StackPanel { Margin = new Thickness(18) };
        side.Children.AddRange([
            SideTitle("Live preview"), SideText("Click an area to find its color."), _preview, _filePreview,
            SideTitle("Accent", top: 22), SideText("One click sets the accent color."), _accents,
            SideTitle("Readability", top: 22), _stats,
        ]);
        var sideBorder = new Border { Child = new ScrollViewer { Content = side }, Width = 340, BorderThickness = new Thickness(1, 0, 0, 0) };
        sideBorder[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        sideBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("FmPanelHeaderBrush");

        // ---- below: reset all, what the theme sets, Cancel / Save
        var resetAll = new Button { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        var resetContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        resetContent.Children.AddRange([DialogLook.Glyph("MiRefresh", 14), new TextBlock { Text = "Reset all", VerticalAlignment = VerticalAlignment.Center }]);
        resetAll.Content = resetContent;
        ToolTip.SetTip(resetAll, "Every area back to the light or dark look");
        resetAll.Click += (_, _) => ResetAll();
        var save = new Button { Content = "Save", IsDefault = true, Classes = { "dialog" } };
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "dialog" } };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.AddRange([cancel, save]);
        var foot = new DockPanel { Margin = new Thickness(20, 12) };
        DockPanel.SetDock(resetAll, Dock.Left);
        DockPanel.SetDock(buttons, Dock.Right);
        resetAll.Margin = new Thickness(0, 0, 12, 0);
        var info = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        info.Children.AddRange([_info, _unsaved, _error]);
        foot.Children.AddRange([resetAll, buttons, info]);
        var footBorder = Bordered(foot, new Thickness(0, 1, 0, 0));
        footBorder[!Border.BackgroundProperty] = new DynamicResourceExtension("FmPanelHeaderBrush");

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        body.Children.Add(_listScroll);
        Grid.SetColumn(sideBorder, 1);
        body.Children.Add(sideBorder);
        var main = new DockPanel();
        DockPanel.SetDock(headBorder, Dock.Top);
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(footBorder, Dock.Bottom);
        main.Children.AddRange([headBorder, hint, footBorder, body]);
        Content = main;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
            {
                _filter.Focus();
                _filter.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _filter.IsFocused && !string.IsNullOrEmpty(_filter.Text))
            {
                _filter.Text = string.Empty;
                e.Handled = true;
            }
        };
        Refresh();
    }

    /// <summary>The theme as saved; null when cancelled.</summary>
    public ColorTheme? Result { get; private set; }

    /// <summary>The theme as edited now (tests).</summary>
    public ColorTheme EditedTheme => _theme;

    /// <summary>Sets the name (tests).</summary>
    public string ThemeName
    {
        get => _name.Text ?? string.Empty;
        set => _name.Text = value;
    }

    /// <summary>The color picker of an area (tests).</summary>
    public ColorPicker? Picker(string key) => _pickerOf.GetValueOrDefault(key);

    /// <summary>The contrast checks of the theme as edited: how many pass, of how many (tests).</summary>
    public (int Passed, int Total) ContrastChecks
    {
        get
        {
            var results = s_checks.Select(c => Contrast(c.Key, c.On)).Where(r => r is not null).Select(r => r!.Value).ToList();
            return (s_checks.Where(c => Contrast(c.Key, c.On) is { } r && r >= c.Minimum).Count(), results.Count);
        }
    }

    /// <summary>Gives the accent color (the circles under the preview).</summary>
    public void SetAccent(string hex) => SetColor("Accent", hex);

    // ---------------------------------------------------------------- colors

    private string Snapshot() => _theme.Base + "|" + string.Join(";", _theme.Colors.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Select(c => c.Key + "=" + c.Value));

    private static ColorThemes.ThemeKey KeyOf(string key) => ColorThemes.Keys.First(k => k.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    private bool IsOwn(string key) => _theme.Colors.TryGetValue(key, out var text) && ColorThemes.Parse(text) is not null;

    /// <summary>The color an area gets: its own, a related area's, or the default of the look.</summary>
    private Color? Effective(string key) => ColorThemes.ColorOf(_theme, key) ?? ColorThemes.DefaultColor(KeyOf(key), _theme.IsDark);

    /// <summary>A color as seen: a (partly) transparent one over the window background.</summary>
    private Color Seen(string key, Color? under = null)
    {
        var color = Effective(key) ?? (_theme.IsDark ? Color.Parse("#2A2A2C") : Color.Parse("#F3F3F3"));
        if (color.A == 255)
        {
            return color;
        }
        var back = under ?? (key == "Window.Background" ? (_theme.IsDark ? Colors.Black : Colors.White) : Seen("Window.Background"));
        var a = color.A / 255.0;
        return Color.FromRgb((byte)(color.R * a + back.R * (1 - a)), (byte)(color.G * a + back.G * (1 - a)), (byte)(color.B * a + back.B * (1 - a)));
    }

    private IBrush BrushOf(string key) => new SolidColorBrush(Seen(key));

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Ratio(Color a, Color b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>The contrast of a key on the area it is read on; null while one of them has no color.</summary>
    private double? Contrast(string key, string on) =>
        Effective(key) is null || Effective(on) is null ? null : Ratio(Seen(key, Seen(on)), Seen(on));

    /// <summary>
    /// A color that is the look's own default is not kept as the theme's (a new theme starts from the colors in effect):
    /// the area then follows the look, and switching between light and dark changes it. Areas that take another area's
    /// color when left out keep theirs (leaving it out would change it).
    /// </summary>
    private void LeaveDefaultsToTheLook()
    {
        foreach (var key in ColorThemes.Keys)
        {
            if (IsDefaultOfTheLook(key.Key, _theme.Colors.GetValueOrDefault(key.Key)))
            {
                _theme.Colors.Remove(key.Key);
            }
        }
    }

    private bool IsDefaultOfTheLook(string key, string? hex) =>
        ColorThemes.FallbacksOf(key).Count == 0 && ColorThemes.Parse(hex) is { } color
        && ColorThemes.DefaultColor(KeyOf(key), _theme.IsDark) is { } fallback && fallback == color;

    private void SetColor(string key, string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || IsDefaultOfTheLook(key, hex))
        {
            _theme.Colors.Remove(key);
        }
        else
        {
            _theme.Colors[key] = hex.Trim().ToUpperInvariant();
        }
        if (_pickerOf.TryGetValue(key, out var picker))
        {
            // the row shows it (its own update ignores this change)
            _syncing = true;
            picker.Color = Effective(key) ?? Colors.Gray;
            _syncing = false;
        }
        Refresh();
    }

    private bool _syncing;

    public void SetLook(bool dark)
    {
        _theme.Base = dark ? "Dark" : "Light";
        _light.IsChecked = !dark;
        _dark.IsChecked = dark;
        Refresh();
    }

    private void ResetAll()
    {
        _theme.Colors.Clear();
        Refresh();
    }

    /// <summary>Everything that shows the colors: the rows, the group counts, the preview, readability, the footer.</summary>
    private void Refresh()
    {
        foreach (var update in _rowUpdates)
        {
            update();
        }
        foreach (var (_, count, prefix) in _sections)
        {
            var changed = ColorThemes.Keys.Count(k => k.Key.Split('.')[0] == prefix && IsOwn(k.Key));
            count.Text = changed == 0 ? string.Empty : $"{changed} changed";
            if (count.Parent is Border badge)
            {
                badge.IsVisible = changed > 0;
            }
        }
        BuildPreview();
        BuildAccents();
        BuildStats();
        UpdateFooter();
    }

    private void UpdateFooter()
    {
        var own = ColorThemes.Keys.Count(k => IsOwn(k.Key));
        var total = ColorThemes.Keys.Count;
        _info.Text = (own, _theme.IsDark) switch
        {
            (0, true) => "All colors follow the dark look",
            (0, false) => "All colors follow the light look",
            (_, true) => $"{own} of {total} colors set · the rest follow the dark look",
            _ => $"{own} of {total} colors set · the rest follow the light look",
        };
        _unsaved.IsVisible = Snapshot() != _initial || (_name.Text ?? string.Empty).Trim() != _theme.Name;
    }

    // ---------------------------------------------------------------- rows

    /// <summary>One area: its color (picker and #RRGGBB), what it colors, its key, the contrast check and "Default".</summary>
    private Border Row(ColorThemes.ThemeKey key, bool first)
    {
        var picker = new ColorPicker { Color = Effective(key.Key) ?? Colors.Gray, IsAlphaEnabled = true, Width = 64, VerticalAlignment = VerticalAlignment.Center };
        var hex = new TextBox
        {
            Width = 104,
            PlaceholderText = "default",
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Classes = { "noloc" },
        };
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        dot[!Avalonia.Controls.Shapes.Shape.FillProperty] = new DynamicResourceExtension("SystemControlHighlightAccentBrush");
        ToolTip.SetTip(dot, "Set by the theme");
        var contrast = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1), VerticalAlignment = VerticalAlignment.Center };
        var contrastText = new TextBlock { FontSize = 11, Classes = { "noloc" } };
        contrast.Child = contrastText;
        // the name, the "set" dot and the contrast: the dot and the contrast go to the next line when the name is long
        var nameLine = new WrapPanel { ItemSpacing = 8, LineSpacing = 2 };
        dot.Margin = new Thickness(0, 6, 0, 0);
        dot.VerticalAlignment = VerticalAlignment.Top;
        nameLine.Children.AddRange([new TextBlock { Text = key.Description, FontSize = 13.5 }, dot, contrast]);
        var fallbacks = ColorThemes.FallbacksOf(key.Key);
        var keyLine = new TextBlock
        {
            Text = fallbacks.Count > 0 ? $"{key.Key} · default follows {fallbacks[0]}" : key.Key,
            FontSize = 11,
            Opacity = 0.55,
            FontFamily = hex.FontFamily,
            Classes = { "noloc" },
        };
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.AddRange([nameLine, keyLine]);
        var reset = new Button { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(8, 4) };
        var resetContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        resetContent.Children.AddRange([DialogLook.Glyph("MiArrowLeft", 12), new TextBlock { Text = "Default", VerticalAlignment = VerticalAlignment.Center }]);
        reset.Content = resetContent;
        ToolTip.SetTip(reset, "Leave this area to the light or dark look");
        var defaultLabel = new TextBlock { Text = "Default", FontSize = 12, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var state = new Panel { Width = 96 };
        state.Children.AddRange([reset, defaultLabel]);
        reset.HorizontalAlignment = HorizontalAlignment.Right;

        var updating = false;
        picker.ColorChanged += (_, e) =>
        {
            if (updating || _syncing)
            {
                return;
            }
            updating = true;
            hex.Text = ColorThemes.Hex(e.NewColor);
            SetColor(key.Key, hex.Text);
            updating = false;
        };
        hex.TextChanged += (_, _) =>
        {
            if (updating)
            {
                return;
            }
            var text = hex.Text?.Trim() ?? string.Empty;
            if (text.Length > 0 && text[0] != '#')
            {
                text = "#" + text;
            }
            var valid = text.Length == 1 || ColorThemes.Parse(text) is not null;
            if (valid || text.Length < 7)
            {
                hex.ClearValue(ForegroundProperty);
            }
            else
            {
                hex.Foreground = Brushes.IndianRed;
            }
            if (text.Length <= 1)
            {
                updating = true;
                SetColor(key.Key, null);
                updating = false;
            }
            else if (ColorThemes.Parse(text) is not null && (text.Length == 7 || text.Length == 9))
            {
                updating = true;
                SetColor(key.Key, text);
                updating = false;
            }
        };
        reset.Click += (_, _) => SetColor(key.Key, null);
        _pickerOf[key.Key] = picker;
        _rowUpdates.Add(() =>
        {
            var own = IsOwn(key.Key);
            dot.IsVisible = own;
            reset.IsVisible = own;
            defaultLabel.IsVisible = !own;
            if (!updating)
            {
                updating = true;
                hex.Text = own ? _theme.Colors[key.Key].ToUpperInvariant() : string.Empty;
                _syncing = true;
                picker.Color = Effective(key.Key) ?? Colors.Gray;
                _syncing = false;
                updating = false;
            }
            var check = s_checks.FirstOrDefault(c => c.Key == key.Key);
            if (check.Key is null)
            {
                check = s_checks.FirstOrDefault(c => c.On == key.Key && key.Key == "Lists.SelectedBackground");
            }
            if (check.Key is not null && Contrast(check.Key, check.On) is { } ratio)
            {
                var ok = ratio >= check.Minimum;
                contrast.IsVisible = true;
                contrastText.Text = ratio.ToString("0.0", CultureInfo.InvariantCulture) + ":1" + (ok ? string.Empty : " · low");
                contrast.Background = new SolidColorBrush(Color.Parse(ok ? (_theme.IsDark ? "#393D1B" : "#DFF6DD") : (_theme.IsDark ? "#442726" : "#FDE7E9")));
                contrastText.Foreground = new SolidColorBrush(Color.Parse(ok ? (_theme.IsDark ? "#6CCB5F" : "#0F7B0F") : (_theme.IsDark ? "#FF99A4" : "#C42B1C")));
                ToolTip.SetTip(contrast, $"Contrast with {check.On} (minimum {check.Minimum.ToString(CultureInfo.InvariantCulture)}:1)");
            }
            else
            {
                contrast.IsVisible = false;
            }
        });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 14 };
        grid.Children.Add(picker);
        Grid.SetColumn(label, 1);
        Grid.SetColumn(hex, 2);
        Grid.SetColumn(state, 3);
        grid.Children.AddRange([label, hex, state]);
        var row = new Border { Child = grid, Padding = new Thickness(14, 10), BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Tag = key, Background = Brushes.Transparent };
        row[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return row;
    }

    private void Filter()
    {
        var text = (_filter.Text ?? string.Empty).Trim();
        foreach (var (section, _, prefix) in _sections)
        {
            var title = s_groups.First(g => g.Prefix == prefix).Title;
            var any = false;
            foreach (var row in _rowOf.Values.Where(r => ((ColorThemes.ThemeKey)r.Tag!).Key.Split('.')[0] == prefix))
            {
                var key = (ColorThemes.ThemeKey)row.Tag!;
                row.IsVisible = text.Length == 0
                    || key.Key.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || Core.Localization.Localizer.T(key.Description).Contains(text, StringComparison.CurrentCultureIgnoreCase)
                    || Core.Localization.Localizer.T(title).Contains(text, StringComparison.CurrentCultureIgnoreCase);
                any |= row.IsVisible;
            }
            section.IsVisible = any;
        }
    }

    /// <summary>Shows the row of an area (a click in the preview): scrolled into view and lit up for a moment.</summary>
    public void Reveal(string key)
    {
        if (!_rowOf.TryGetValue(key, out var row))
        {
            return;
        }
        if (!string.IsNullOrEmpty(_filter.Text))
        {
            _filter.Text = string.Empty;
        }
        row.BringIntoView();
        row[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlHighlightListLowBrush");
        DispatcherTimer.RunOnce(() => row.Background = Brushes.Transparent, TimeSpan.FromSeconds(1.5));
        if (row.Child is Grid grid && grid.Children.OfType<TextBox>().FirstOrDefault() is { } hex)
        {
            hex.Focus();
        }
    }

    // ---------------------------------------------------------------- the side: preview, accents, readability

    /// <summary>An area of the preview: a click finds its row.</summary>
    private Border Zone(string key, Control? child, IBrush? background = null)
    {
        var zone = new Border { Child = child, Background = background ?? Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        ToolTip.SetTip(zone, Core.Localization.Localizer.T(KeyOf(key).Description));
        zone.PointerPressed += (_, e) =>
        {
            Reveal(key);
            e.Handled = true;
        };
        return zone;
    }

    private TextBlock PreviewText(string text, string colorKey, double size = 12, bool bold = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
        Foreground = BrushOf(colorKey),
        Classes = { "noloc" },
    };

    private void BuildPreview()
    {
        // a copy dialog under the main menu
        var menuTexts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(12, 7) };
        foreach (var text in new[] { "Files", "Mark", "Commands", "Net" })
        {
            menuTexts.Children.Add(PreviewText(text, "MenuBar.Text"));
        }
        var menuBar = Zone("MenuBar.Background", menuTexts, BrushOf("MenuBar.Background"));

        var window = new StackPanel { Spacing = 9, Margin = new Thickness(14) };
        window.Children.Add(Zone("Window.Text", PreviewText("Copy 3 files", "Window.Text", 14, bold: true)));
        window.Children.Add(Zone("Controls.SecondaryText", PreviewText(@"from C:\Projects to D:\Backup", "Controls.SecondaryText")));
        var input = PreviewText(@"D:\Backup\2026\", "Window.Text", 11.5);
        input.FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace");
        var inputBox = Zone("Controls.Background", input, BrushOf("Controls.Background"));
        inputBox.BorderBrush = BrushOf("Controls.Border");
        inputBox.BorderThickness = new Thickness(1);
        inputBox.CornerRadius = new CornerRadius(4);
        inputBox.Padding = new Thickness(9, 6);
        window.Children.Add(inputBox);
        var check = Zone("Accent", new TextBlock { Text = "✓", FontSize = 10, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Classes = { "noloc" } }, BrushOf("Accent"));
        check.Width = 15;
        check.Height = 15;
        check.CornerRadius = new CornerRadius(3);
        var checkRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        checkRow.Children.AddRange([check, PreviewText("Verify after copying", "Window.Text")]);
        window.Children.Add(checkRow);
        var list = new StackPanel();
        list.Children.Add(Zone("Lists.SelectedBackground", Padded(PreviewText("report.pdf", "Window.Text")), BrushOf("Lists.SelectedBackground")));
        list.Children.Add(Zone("Lists.HoverBackground", Padded(PreviewText("photo.jpg", "Window.Text")), BrushOf("Lists.HoverBackground")));
        list.Children.Add(Padded(PreviewText("notes.txt", "Window.Text")));
        window.Children.Add(new Border { Child = list, BorderBrush = BrushOf("Controls.Border"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ClipToBounds = true });
        window.Children.Add(Zone("Window.ErrorText", PreviewText("1 file could not be read.", "Window.ErrorText")));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(PreviewButton("Controls.DisabledText", "Queue", "Controls.DisabledText", "Controls.DisabledBackground"));
        buttons.Children.Add(PreviewButton("Controls.HoverBackground", "Cancel", "Window.Text", "Controls.HoverBackground", border: true));
        var copy = PreviewButton("Accent", "Copy", null, "Accent");
        buttons.Children.Add(copy);
        window.Children.Add(buttons);
        var windowZone = Zone("Window.Background", window, BrushOf("Window.Background"));
        var stack = new StackPanel();
        stack.Children.AddRange([menuBar, windowZone]);
        _preview.Child = stack;

        // a file panel: header, folder, cursor, marked and hidden files
        var panel = new StackPanel();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(9, 4) };
        header.Children.Add(PreviewText("Name", "FileList.HeaderText"));
        var size = PreviewText("Size", "FileList.HeaderText");
        Grid.SetColumn(size, 1);
        header.Children.Add(size);
        panel.Children.Add(Zone("FileList.HeaderBackground", header, BrushOf("FileList.HeaderBackground")));
        panel.Children.Add(Zone("FileList.Text", Padded(PreviewText("[Documents]", "FileList.Text"))));
        panel.Children.Add(Zone("FileList.CursorBackground", Padded(PreviewText("report.pdf", "FileList.CursorText")), new SolidColorBrush(Seen("FileList.CursorBackground", Seen("FileList.Background")))));
        panel.Children.Add(Zone("FileList.MarkedText", Padded(PreviewText("photo.jpg", "FileList.MarkedText")), BrushOf("FileList.AlternateBackground")));
        panel.Children.Add(Zone("FileList.HiddenText", Padded(PreviewText(".git", "FileList.HiddenText"))));
        var status = Zone("Panel.StatusBackground", Padded(PreviewText("1 of 4 files", "Controls.SecondaryText", 11)), BrushOf("Panel.StatusBackground"));
        panel.Children.Add(status);
        _filePreview.Child = Zone("FileList.Background", panel, BrushOf("FileList.Background"));
        _filePreview.Background = BrushOf("Window.Background");
    }

    private static Border Padded(Control child) => new() { Child = child, Padding = new Thickness(9, 5) };

    private Border PreviewButton(string zoneKey, string text, string? foreground, string background, bool border = false)
    {
        var label = foreground is null
            ? new TextBlock { Text = text, FontSize = 12, Foreground = Brushes.White, Classes = { "noloc" } }
            : PreviewText(text, foreground);
        var button = Zone(zoneKey, label, BrushOf(background));
        button.CornerRadius = new CornerRadius(4);
        button.Padding = new Thickness(12, 5);
        if (border)
        {
            button.BorderBrush = BrushOf("Controls.Border");
            button.BorderThickness = new Thickness(1);
        }
        return button;
    }

    private void BuildAccents()
    {
        _accents.Children.Clear();
        var current = Effective("Accent") is { } c ? ColorThemes.Hex(c) : string.Empty;
        foreach (var hex in s_accents)
        {
            var on = string.Equals(current, hex, StringComparison.OrdinalIgnoreCase);
            var circle = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(Color.Parse(hex)),
                BorderThickness = new Thickness(2),
                Margin = new Thickness(0, 0, 8, 8),
                Classes = { "noloc" },
            };
            circle[!Button.BorderBrushProperty] = new DynamicResourceExtension(on ? "SystemControlForegroundBaseHighBrush" : "SystemControlForegroundBaseLowBrush");
            ToolTip.SetTip(circle, hex);
            Avalonia.Automation.AutomationProperties.SetName(circle, "Accent " + hex);
            var value = hex;
            circle.Click += (_, _) => SetAccent(value);
            _accents.Children.Add(circle);
        }
    }

    private void BuildStats()
    {
        _stats.Children.Clear();
        var (passed, total) = ContrastChecks;
        var text = Contrast("Window.Text", "Window.Background") is { } ratio ? ratio.ToString("0.0", CultureInfo.InvariantCulture) + ":1" : "–";
        var first = Stat($"{passed}/{total}", "Contrast checks passed");
        var second = Stat(text, "Text on background");
        Grid.SetColumn(second, 2);
        _stats.Children.AddRange([first, second]);
    }

    private static Border Stat(string value, string text)
    {
        var stack = new StackPanel();
        stack.Children.AddRange([
            new TextBlock { Text = value, FontSize = 18, FontWeight = FontWeight.SemiBold, Classes = { "noloc" } },
            new TextBlock { Text = text, FontSize = 11.5, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
        ]);
        var border = new Border { Child = stack, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(10, 8) };
        border[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        border[!Border.BackgroundProperty] = new DynamicResourceExtension("SystemControlBackgroundAltHighBrush");
        return border;
    }

    private static TextBlock SideTitle(string text, double top = 0) => new() { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, top, 0, 4) };

    private static TextBlock SideText(string text) => new() { Text = text, FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };

    private static Control Labeled(string label, Control control)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.AddRange([new TextBlock { Text = label, FontSize = 13, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center }, control]);
        return row;
    }

    private static Border Bordered(Control child, Thickness border)
    {
        var box = new Border { Child = child, BorderThickness = border };
        box[!Border.BorderBrushProperty] = new DynamicResourceExtension("SystemControlForegroundBaseLowBrush");
        return box;
    }

    public void Save()
    {
        var name = (_name.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            _error.Text = "Give the theme a name.";
            _name.Focus();
            return;
        }
        _error.Text = string.Empty;
        _theme.Name = name;
        try
        {
            ColorThemes.SaveUserTheme(_theme);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _error.Text = ex.Message;
            return;
        }
        Result = _theme;
        Close();
    }
}
