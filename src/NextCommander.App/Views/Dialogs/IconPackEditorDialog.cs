using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NextCommander.App.Services;
using NextCommander.App.Services.Icons;
using NextCommander.Core.Config;

namespace NextCommander.App.Views.Dialogs;

/// <summary>
/// The icon pack editor (Configuration > Icons): the name, how the icons are drawn (colored, one color, outline), the
/// corner marks, and the colors of the menu icons and of the file type icons, with a preview. The shapes of a pack
/// started from another one are kept. Saved into the iconpacks folder of the settings; a shipped pack as a copy there.
/// </summary>
public sealed class IconPackEditorDialog : Window
{
    /// <summary>The menu and button bar icons of the preview.</summary>
    private static readonly string[] s_preview =
    [
        "nc_Copy", "nc_RenMov", "nc_Delete", "nc_MkDir", "nc_PackFiles", "nc_UnpackFiles", "nc_SearchFor", "nc_List", "nc_Edit",
        "nc_OpenNewTab", "nc_DirectoryHotlist", "nc_DirectoryHistory", "nc_ShowQuickFilter", "nc_ExecuteTerminal", "nc_Config", "nc_SyncDirs",
    ];

    /// <summary>The file types whose icon color a pack can change ("File.Folder" ...).</summary>
    private static readonly (FileKind Kind, string Text)[] s_fileKinds =
    [
        (FileKind.Folder, "Folders"), (FileKind.File, "Other files"), (FileKind.Archive, "Archives"), (FileKind.Image, "Images"),
        (FileKind.Audio, "Audio"), (FileKind.Video, "Video"), (FileKind.Code, "Source code"), (FileKind.Document, "Documents"),
        (FileKind.Pdf, "PDF"), (FileKind.Executable, "Programs"), (FileKind.Parent, "Parent folder [..]"), (FileKind.Error, "Unreadable"),
    ];

    private readonly IconPack _pack;
    private readonly TextBox _name = new() { Watermark = "Icon pack name", Width = 260, Classes = { "noloc" } };
    private readonly ComboBox _style = new() { Width = 240 };
    private readonly ColorPicker _monoColor = new() { Width = 70 };
    private readonly CheckBox _themeColor = new() { Content = "The icon color of the color theme" };
    private readonly CheckBox _badges = new() { Content = "Small marks at the corner of menu icons (plus, cross, arrow)" };
    private readonly WrapPanel _previewIcons = new() { ItemSpacing = 6, LineSpacing = 6 };
    private readonly TextBlock _error = new() { Foreground = Brushes.IndianRed };
    private readonly StackPanel _monoRow = new() { Orientation = Orientation.Horizontal, Spacing = 10 };

    public IconPackEditorDialog(IconPack pack)
    {
        _pack = pack.Clone();
        Title = "Icon pack";
        Width = 720;
        Height = 700;
        MinWidth = 520;
        MinHeight = 420;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _name.Text = _pack.Name;
        var styles = new (string Text, IconPackStyle Style)[]
        {
            ("Colored (each icon in its color)", IconPackStyle.Colored),
            ("One color", IconPackStyle.Monochrome),
            ("Outline (Fluent)", IconPackStyle.Outline),
        };
        _style.ItemsSource = styles.Select(s => s.Text).ToList();
        _style.SelectedIndex = Array.FindIndex(styles, s => s.Style == _pack.Style);
        _style.SelectionChanged += (_, _) =>
        {
            _pack.Style = styles[Math.Max(0, _style.SelectedIndex)].Style;
            Changed();
        };
        _themeColor.IsChecked = string.IsNullOrWhiteSpace(_pack.Color);
        _monoColor.Color = ColorThemes.Parse(_pack.Color) ?? Color.Parse("#607080");
        _themeColor.IsCheckedChanged += (_, _) =>
        {
            _pack.Color = _themeColor.IsChecked == true ? null : ColorThemes.Hex(_monoColor.Color);
            Changed();
        };
        _monoColor.ColorChanged += (_, e) =>
        {
            _themeColor.IsChecked = false;
            _pack.Color = ColorThemes.Hex(e.NewColor);
            Changed();
        };
        _badges.IsChecked = _pack.Badges;
        _badges.IsCheckedChanged += (_, _) =>
        {
            _pack.Badges = _badges.IsChecked == true;
            Changed();
        };

        var body = new StackPanel { Spacing = 10, Margin = new Thickness(16, 14, 16, 8) };
        var nameRow = new WrapPanel { ItemSpacing = 10, LineSpacing = 6 };
        nameRow.Children.AddRange([new TextBlock { Text = "Name:", VerticalAlignment = VerticalAlignment.Center }, _name,
            new TextBlock { Text = "Icons:", VerticalAlignment = VerticalAlignment.Center }, _style]);
        _monoRow.Children.AddRange([new TextBlock { Text = "One color:", VerticalAlignment = VerticalAlignment.Center }, _monoColor, _themeColor]);
        body.Children.AddRange([nameRow, _monoRow, _badges, Heading("Preview"), _previewIcons]);

        body.Children.Add(Heading("Colors of the menu and button bar icons"));
        var palette = new WrapPanel { ItemSpacing = 14, LineSpacing = 8 };
        foreach (var name in IconPack.PaletteNames)
        {
            palette.Children.Add(ColorChoice(name, name, MenuIcons.DefaultPaletteColor(name)));
        }
        body.Children.Add(palette);

        body.Children.Add(Heading("Colors of the file type icons"));
        var kinds = new WrapPanel { ItemSpacing = 14, LineSpacing = 8 };
        foreach (var (kind, text) in s_fileKinds)
        {
            kinds.Children.Add(ColorChoice("File." + kind, text, FileKinds.DefaultColor(kind)));
        }
        body.Children.Add(kinds);
        if (_pack.Glyphs.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, "The pack keeps the {0} icon shapes of the pack it was made from.", _pack.Glyphs.Count),
                Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            });
        }
        body.Children.Add(_error);

        var save = new Button { Content = "Save", IsDefault = true, Classes = { "dialog" } };
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "Cancel", IsCancel = true, Classes = { "dialog" } };
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel { Classes = { "buttonrow" }, Margin = new Thickness(16, 4, 16, 14) };
        buttons.Children.AddRange([save, cancel]);
        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.AddRange([buttons, new ScrollViewer { Content = body }]);
        Content = root;
        Changed();
    }

    /// <summary>The pack as saved; null when cancelled.</summary>
    public IconPack? Result { get; private set; }

    /// <summary>The pack as edited now (tests).</summary>
    public IconPack EditedPack => _pack;

    public string PackName
    {
        get => _name.Text ?? string.Empty;
        set => _name.Text = value;
    }

    /// <summary>The icons of the preview (tests).</summary>
    public IReadOnlyList<Control> PreviewIcons => [.. _previewIcons.Children];

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };

    /// <summary>A color of the pack: a picker with its name; "Default" leaves the built-in color.</summary>
    private Control ColorChoice(string key, string text, Color builtIn)
    {
        var picker = new ColorPicker { Color = ColorThemes.Parse(_pack.Colors.GetValueOrDefault(key)) ?? builtIn, Width = 70 };
        var reset = new Button { Content = "Default", Padding = new Thickness(6, 1), FontSize = 12 };
        picker.ColorChanged += (_, e) =>
        {
            _pack.Colors[key] = ColorThemes.Hex(e.NewColor);
            Changed();
        };
        reset.Click += (_, _) =>
        {
            _pack.Colors.Remove(key);
            picker.Color = builtIn;
            _pack.Colors.Remove(key); // the picker's change put it back
            Changed();
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Tag = key };
        panel.Children.AddRange([picker, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, MinWidth = 90 }, reset]);
        return panel;
    }

    /// <summary>The preview and the one-color row follow every change.</summary>
    private void Changed()
    {
        _monoRow.IsVisible = _pack.Style == IconPackStyle.Monochrome;
        _previewIcons.Children.Clear();
        foreach (var command in s_preview)
        {
            if (MenuIcons.Create(command, _pack) is { } icon)
            {
                _previewIcons.Children.Add(new Viewbox { Width = 24, Height = 24, Child = icon });
            }
        }
    }

    public void Save()
    {
        var name = (_name.Text ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            _error.Text = "Give the icon pack a name.";
            return;
        }
        _pack.Name = name;
        var dir = IconPacks.UserDirectory ?? IconPacks.BuiltInDirectory;
        var path = _pack.FilePath is { } own && IsIn(own, IconPacks.UserDirectory) ? own : Path.Combine(dir, IconPack.FileNameFor(name));
        try
        {
            _pack.Save(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _error.Text = ex.Message;
            return;
        }
        Result = _pack;
        Close();
    }

    private static bool IsIn(string path, string? folder) =>
        folder is not null
        && Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
