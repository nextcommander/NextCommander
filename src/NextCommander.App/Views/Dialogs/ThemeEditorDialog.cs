using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NextCommander.App.Services;
using NextCommander.Core.Config;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// The color theme editor (Configuration > Color): the name, light or dark, and a color picker for every area of the
/// window. Saved into the themes folder of the settings; a shipped theme is saved as a copy there.
/// </summary>
public sealed class ThemeEditorDialog : Window
{
    private readonly ColorTheme _theme;
    private readonly TextBox _name = new() { Watermark = "Theme name", Width = 260, Classes = { "noloc" } };
    private readonly ComboBox _base = new() { ItemsSource = new[] { "Light", "Dark" }, Width = 140 };
    private readonly TextBox _filter = new() { Watermark = "Search", Width = 220 };
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed };
    private readonly Dictionary<string, Control> _rowOf = new(StringComparer.OrdinalIgnoreCase);

    public ThemeEditorDialog(ColorTheme theme)
    {
        _theme = theme.Clone();
        Title = "Color theme";
        Width = 760;
        Height = 680;
        MinWidth = 520;
        MinHeight = 400;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _name.Text = _theme.Name;
        _base.SelectedIndex = _theme.IsDark ? 1 : 0;
        _base.SelectionChanged += (_, _) => _theme.Base = _base.SelectedIndex == 1 ? "Dark" : "Light";
        _filter.TextChanged += (_, _) => Filter();

        foreach (var key in ColorThemes.Keys)
        {
            var row = Row(key);
            _rowOf[key.Key] = row;
            _rows.Children.Add(row);
        }

        var top = new WrapPanel { ItemSpacing = 10, LineSpacing = 6 };
        top.Children.AddRange([
            new TextBlock { Text = "Name:", VerticalAlignment = VerticalAlignment.Center }, _name,
            new TextBlock { Text = "Look:", VerticalAlignment = VerticalAlignment.Center }, _base, _filter,
        ]);
        var hint = new TextBlock
        {
            Text = "An area left at \"default\" takes its color from the light or dark look (or from a related area).",
            Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };

        var save = new Button { Content = "Save", IsDefault = true, Classes = { "dialog" } };
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "dialog" } };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Classes = { "buttonrow" }, Margin = new Thickness(16, 4, 16, 14) };
        buttons.Children.AddRange([save, cancel]);

        var head = new StackPanel { Spacing = 8, Margin = new Thickness(16, 14, 16, 8) };
        head.Children.AddRange([top, hint, _error]);
        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.AddRange([head, buttons, new ScrollViewer { Content = _rows, Margin = new Thickness(16, 0, 8, 0) }]);
        Content = root;
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
    public ColorPicker? Picker(string key) => _rowOf.TryGetValue(key, out var row) ? ((Grid)row).Children.OfType<ColorPicker>().FirstOrDefault() : null;

    /// <summary>One area: what it colors, its color (picker and #RRGGBB), and "default" to leave it to the look.</summary>
    private Control Row(ColorThemes.ThemeKey key)
    {
        var current = ColorThemes.Parse(_theme.Colors.GetValueOrDefault(key.Key));
        var picker = new ColorPicker { Color = current ?? Colors.Gray, IsAlphaEnabled = true, Width = 70 };
        var hex = new TextBox { Text = current is { } c ? ColorThemes.Hex(c) : string.Empty, Watermark = "default", Width = 110, Classes = { "noloc" } };
        var reset = new Button { Content = "Default", Padding = new Thickness(8, 2) };
        ToolTip.SetTip(reset, "Leave this area to the light or dark look");
        var updating = false;
        picker.ColorChanged += (_, e) =>
        {
            if (updating)
            {
                return;
            }
            updating = true;
            hex.Text = ColorThemes.Hex(e.NewColor);
            _theme.Colors[key.Key] = hex.Text;
            updating = false;
        };
        hex.TextChanged += (_, _) =>
        {
            if (updating)
            {
                return;
            }
            var text = hex.Text?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                _theme.Colors.Remove(key.Key);
                return;
            }
            if (ColorThemes.Parse(text) is { } color)
            {
                updating = true;
                picker.Color = color;
                _theme.Colors[key.Key] = text;
                updating = false;
            }
        };
        reset.Click += (_, _) => hex.Text = string.Empty;

        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = key.Description, TextWrapping = TextWrapping.Wrap });
        label.Children.Add(new TextBlock { Text = key.Key, FontSize = 11, Opacity = 0.55, Classes = { "noloc" } });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 3), Tag = key };
        Grid.SetColumn(picker, 1);
        Grid.SetColumn(hex, 2);
        Grid.SetColumn(reset, 3);
        grid.Children.AddRange([label, picker, hex, reset]);
        return grid;
    }

    private void Filter()
    {
        var text = (_filter.Text ?? string.Empty).Trim();
        foreach (var row in _rows.Children)
        {
            var key = (ColorThemes.ThemeKey)row.Tag!;
            row.IsVisible = text.Length == 0
                || key.Key.Contains(text, StringComparison.OrdinalIgnoreCase)
                || Core.Localization.Localizer.T(key.Description).Contains(text, StringComparison.CurrentCultureIgnoreCase);
        }
    }

    public void Save()
    {
        var name = (_name.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            _error.Text = "Give the theme a name.";
            return;
        }
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
