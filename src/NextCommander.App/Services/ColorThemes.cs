using System.Reflection;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using NextCommander.Core.Config;

namespace NextCommander.App.Services;

/// <summary>
/// Application colors. Without a theme the application shows the system colors (Fluent, light, the system's accent).
/// A color theme file (<see cref="ColorTheme"/>) sets colors by area key ("FileList.Text", "Window.Background" ...);
/// the shipped ones are in &lt;program&gt;/Themes (among them the dark theme "dark"), the user's own in &lt;settings&gt;/themes.
/// </summary>
public static class ColorThemes
{
    /// <summary>An area key of a theme file: what it colors and where the color goes.</summary>
    /// <param name="Targets">
    /// Fluent palette colors ("Palette:RegionColor", with an alpha "Palette:AltLow@33") or application resources
    /// (brushes; keys ending in "Color" are colors).
    /// </param>
    public sealed record ThemeKey(string Key, string Description, params string[] Targets);

    /// <summary>The keys a theme file can use, in the order of a saved file.</summary>
    public static readonly IReadOnlyList<ThemeKey> Keys =
    [
        new("Accent", "Accent color: selected items, focus frame, check boxes", "Palette:Accent"),
        new("Window.Background", "Background of windows and dialog boxes",
            "Palette:RegionColor", "Palette:AltHigh", "Palette:AltMediumHigh@CC", "Palette:AltMedium@99", "Palette:AltMediumLow@66", "Palette:AltLow@33"),
        new("Window.Text", "General text color",
            "Palette:BaseHigh", "Palette:BaseMediumHigh@B4", "Palette:BaseMedium@99", "Palette:BaseMediumLow@66", "Palette:BaseLow@3C", "Palette:ChromeAltLow"),
        new("Window.ErrorText", "Error messages", "Palette:ErrorText"),
        new("Controls.Background", "Background of drop-down menus, input boxes and buttons", "Palette:ChromeLow", "Palette:ChromeMediumLow"),
        new("Controls.HoverBackground", "Background of controls under the mouse", "Palette:ChromeMedium"),
        new("Controls.Border", "Borders of controls", "Palette:ChromeHigh"),
        new("Controls.SecondaryText", "Secondary texts and icons", "Palette:ChromeGray"),
        new("Controls.DisabledBackground", "Background of disabled controls", "Palette:ChromeDisabledHigh"),
        new("Controls.DisabledText", "Text of disabled controls", "Palette:ChromeDisabledLow"),
        new("Lists.HoverBackground", "Row under the mouse in lists (settings, dialog boxes)", "Palette:ListLow"),
        new("Lists.SelectedBackground", "Selected row in lists", "Palette:ListMedium"),
        new("MenuBar.Background", "Background of the main menu bar", "FmMenuBarBrush"),
        new("MenuBar.Text", "Text of the main menu bar", "FmMenuBarForegroundBrush"),
        new("Menu.Background", "Background of drop-down and context menus", "MenuFlyoutPresenterBackground"),
        new("Menu.Border", "Frame of drop-down and context menus", "MenuFlyoutPresenterBorderBrush"),
        new("Menu.Text", "Text of menu items", "MenuFlyoutItemForeground", "MenuFlyoutSubItemChevron"),
        new("Menu.HoverBackground", "Menu item under the mouse (and an open submenu)", "MenuFlyoutItemBackgroundPointerOver", "MenuFlyoutItemBackgroundPressed"),
        new("Menu.HoverText", "Text of the menu item under the mouse", "MenuFlyoutItemForegroundPointerOver", "MenuFlyoutItemForegroundPressed",
            "MenuFlyoutSubItemChevronPointerOver", "MenuFlyoutSubItemChevronPressed", "MenuFlyoutSubItemChevronSubMenuOpened",
            "MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver", "MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed"),
        new("Menu.GestureText", "Shortcut keys shown in menus", "MenuFlyoutItemKeyboardAcceleratorTextForeground"),
        new("Menu.DisabledText", "Disabled menu items", "MenuFlyoutItemForegroundDisabled", "MenuFlyoutItemKeyboardAcceleratorTextForegroundDisabled",
            "MenuFlyoutSubItemChevronDisabled"),
        new("ButtonBar.Background", "Background of the button bar", "FmToolBarBrush"),
        new("Drives.Icon", "Icons on the drive buttons", "FmDriveIconBrush"),
        new("Drives.Background", "Line of the drive box above the panel", "FmDriveBarBrush"),
        new("Bars.Line", "Line under the menu bar, the button bar and the drive buttons", "FmBarLineBrush"),
        new("Tabs.Background", "Folder tab strip", "FmTabStripBrush"),
        new("Tabs.ActiveBackground", "Active tab", "FmTabActiveBrush"),
        new("Tabs.PassiveLine", "Faint underline of the selected tab in the inactive panel", "FmTabPassiveLineBrush"),
        new("Panel.HeaderBackground", "Panel header (free space line) and thumbnail boxes", "FmPanelHeaderBrush"),
        new("Panel.ActiveAccent", "Marker color of the active panel", "FmActiveAccentBrush"),
        new("Panel.ActivePathBackground", "Path line of the active panel", "FmActivePathBrush"),
        new("Panel.StatusBackground", "Status line of the panel", "FmStatusBrush"),
        new("FileList.Background", "Background of the file list", "FmListBackgroundBrush"),
        new("FileList.AlternateBackground", "Alternate rows of the file list", "FmListBackground2Brush"),
        new("FileList.Text", "Text of the file list", "FmListTextBrush"),
        new("FileList.MarkedText", "Marked (selected) files", "FmMarkedBrush"),
        new("FileList.HiddenText", "Hidden and system files", "FmHiddenBrush"),
        new("FileList.FolderIcon", "Folder icon", "FmFolderBrush"),
        new("FileList.FileIcon", "File icon", "FmFileBrush"),
        new("FileList.CursorBackground", "Cursor bar (active panel)", "DataGridRowSelectedBackgroundBrush", "DataGridRowSelectedHoveredBackgroundBrush"),
        new("FileList.CursorText", "Text on the cursor bar", "FmCursorTextBrush"),
        new("FileList.InactiveCursorBackground", "Cursor bar (inactive panel)",
            "DataGridRowSelectedUnfocusedBackgroundBrush", "DataGridRowSelectedHoveredUnfocusedBackgroundBrush"),
        new("FileList.HoverBackground", "Row under the mouse", "DataGridRowHoveredBackgroundColor"),
        new("FileList.HeaderBackground", "Column headers", "DataGridColumnHeaderBackgroundBrush"),
        new("FileList.HeaderHoverBackground", "Column header under the mouse", "DataGridColumnHeaderHoveredBackgroundBrush", "DataGridColumnHeaderPressedBackgroundBrush"),
        new("FileList.HeaderText", "Text of the column headers", "DataGridColumnHeaderForegroundBrush"),
        new("FileList.GridLines", "Grid lines", "DataGridGridLinesBrush"),
        new("FunctionKeys.Border", "Line between the function keys (F3 ... F8); the keys take the window's background", "FmFunctionKeyBorderBrush"),
        new("Lister.Background", "Background of Lister (F3) and the editor", "FmListerBackgroundBrush"),
        new("Lister.Text", "Text of Lister (F3) and the editor", "FmListerTextBrush"),
        new("Compare.Added", "Compare: background of added lines", "FmCompareAddedBrush"),
        new("Compare.AddedMark", "Compare: added characters inside a changed line", "FmCompareAddedMarkBrush"),
        new("Compare.AddedText", "Compare: + signs and the tag of the right file", "FmCompareAddedTextBrush"),
        new("Compare.Removed", "Compare: background of removed lines", "FmCompareRemovedBrush"),
        new("Compare.RemovedMark", "Compare: removed characters inside a changed line", "FmCompareRemovedMarkBrush"),
        new("Compare.RemovedText", "Compare: − signs and the tag of the left file", "FmCompareRemovedTextBrush"),
        new("Compare.Changed", "Compare: changed differences in the overview strip", "FmCompareChangedBrush"),
        new("Compare.Moved", "Compare: background of moved lines", "FmCompareMovedBrush"),
        new("Compare.MovedText", "Compare: ↕ signs of moved lines", "FmCompareMovedTextBrush"),
        new("Compare.Current", "Compare: frame of the current difference", "FmCompareCurrentBrush"),
        new("Compare.Conflict", "Merge: conflicts in the three files", "FmCompareConflictBrush"),
    ];

    /// <summary>A theme was applied (windows that draw with theme colors themselves draw again).</summary>
    public static event EventHandler? Applied;

    private const string PalettePrefix = "Palette:";

    /// <summary>
    /// Keys a theme may leave out: their color then comes from these keys (the first one the theme has), so menus follow
    /// every theme even when it says nothing about them.
    /// </summary>
    private static readonly Dictionary<string, string[]> s_fallbacks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Menu.Background"] = ["Controls.Background", "Window.Background"],
        ["Menu.Border"] = ["Controls.Border"],
        ["Menu.Text"] = ["Window.Text"],
        ["Menu.HoverBackground"] = ["Tabs.ActiveBackground", "Lists.HoverBackground", "Controls.HoverBackground"],
        ["Menu.HoverText"] = ["Window.Text"],
        ["Menu.GestureText"] = ["Controls.SecondaryText"],
        ["Menu.DisabledText"] = ["Controls.DisabledText"],
        ["Drives.Background"] = ["Panel.HeaderBackground"],
        ["Bars.Line"] = ["Panel.HeaderBackground"],
    };

    /// <summary>The keys whose color a key takes while a theme leaves it out (the first one the theme has).</summary>
    public static IReadOnlyList<string> FallbacksOf(string key) => s_fallbacks.GetValueOrDefault(key) ?? [];

    private static FluentTheme? s_defaultFluent;

    /// <summary>
    /// The color a key has without a theme color, in the light or the dark look: the Fluent palette's own color or the
    /// application's default brush (null when it has none, e.g. colors the controls take from elsewhere).
    /// </summary>
    public static Color? DefaultColor(ThemeKey key, bool dark)
    {
        var variant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var target = key.Targets[0];
        static Color? ColorOfValue(object? value) => value switch
        {
            Color c => c,
            ISolidColorBrush b => b.Color,
            _ => null,
        };
        if (target.StartsWith(PalettePrefix, StringComparison.Ordinal))
        {
            // a theme of its own (not the one in use, whose palettes a theme may have replaced)
            s_defaultFluent ??= new FluentTheme();
            if (s_defaultFluent.TryGetResource(ResourceOf(target), variant, out var value) && ColorOfValue(value) is { } color)
            {
                return SplitPalette(target).Alpha is { } alpha ? Color.FromArgb(alpha, color.R, color.G, color.B) : color;
            }
            return null;
        }
        if (Application.Current is not { } app)
        {
            return null;
        }
        // the defaults of App.axaml (a theme's colors go to the root of the resources, not into these dictionaries)
        if (app.Resources.ThemeDictionaries.TryGetValue(variant, out var dictionary)
            && dictionary.TryGetResource(target, variant, out var own) && ColorOfValue(own) is { } ownColor)
        {
            return ownColor;
        }
        foreach (var style in app.Styles)
        {
            if (style.TryGetResource(target, variant, out var value) && ColorOfValue(value) is { } color)
            {
                return color;
            }
        }
        return null;
    }

    /// <summary>The color of a key in a theme: its own, else the first fallback the theme has.</summary>
    public static Color? ColorOf(ColorTheme theme, string key)
    {
        if (theme.Colors.TryGetValue(key, out var text) && Parse(text) is { } own)
        {
            return own;
        }
        foreach (var fallback in s_fallbacks.GetValueOrDefault(key) ?? [])
        {
            if (theme.Colors.TryGetValue(fallback, out var other) && Parse(other) is { } color)
            {
                return color;
            }
        }
        return null;
    }
    private static readonly HashSet<string> s_applied = new(StringComparer.Ordinal);
    private static bool s_paletteSet;

    /// <summary>&lt;program&gt;/Themes (shipped themes).</summary>
    public static string BuiltInDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Themes");

    /// <summary>&lt;settings&gt;/themes (the user's themes; set at start, null in tests).</summary>
    public static string? UserDirectory { get; set; }

    /// <summary>The theme in effect (null: system colors).</summary>
    public static ColorTheme? Current { get; private set; }

    public static bool IsDark => Current?.IsDark == true;

    /// <summary>The theme follows the operating system's light / dark mode (<see cref="ColorTheme.SystemThemeId"/>).</summary>
    public static bool FollowsSystem { get; private set; }

    private static bool s_watchingSystem;

    /// <summary>Tests: the system's mode instead of asking the platform.</summary>
    public static bool? SystemIsDarkOverride { get; set; }

    /// <summary>Whether the operating system is in dark mode.</summary>
    public static bool SystemIsDark(Application app) =>
        SystemIsDarkOverride ?? app.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Dark;

    public static IReadOnlyList<ColorTheme> Available() => ColorTheme.LoadAll([BuiltInDirectory, UserDirectory]);

    public static ColorTheme? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : Available().FirstOrDefault(t => string.Equals(t.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>"#RRGGBB", "#AARRGGBB" or a color name; null for empty or unreadable text.</summary>
    public static Color? Parse(string? text) =>
        !string.IsNullOrWhiteSpace(text) && Color.TryParse(text.Trim(), out var color) ? color : null;

    /// <summary>Applies a theme file by id; empty or unknown: the system colors.</summary>
    public static void Apply(Application app, string? themeId)
    {
        ApplyTheme(app, themeId);
        // the colors of the tool windows that are made from the theme's (accent tints, text on the accent)
        Design.DesignColors.Update(app);
        Applied?.Invoke(null, EventArgs.Empty);
    }

    private static void ApplyTheme(Application app, string? themeId)
    {
        // "system": light system colors or the dark theme, as the operating system is set (and again when it switches)
        FollowsSystem = string.Equals(themeId?.Trim(), ColorTheme.SystemThemeId, StringComparison.OrdinalIgnoreCase);
        if (FollowsSystem)
        {
            if (!s_watchingSystem && app.PlatformSettings is { } platform)
            {
                s_watchingSystem = true;
                platform.ColorValuesChanged += (_, _) =>
                {
                    if (FollowsSystem)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => Apply(app, ColorTheme.SystemThemeId));
                    }
                };
            }
            themeId = SystemIsDark(app) ? ColorTheme.DarkThemeId : null;
        }
        foreach (var key in s_applied)
        {
            app.Resources.Remove(key);
        }
        s_applied.Clear();
        var theme = Find(themeId);
        Current = theme;

        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent && (s_paletteSet || theme is not null))
        {
            // palettes are always new objects: a palette removed from the theme keeps its owner and cannot be added again
            fluent.Palettes.Remove(ThemeVariant.Light);
            fluent.Palettes.Remove(ThemeVariant.Dark);
            s_paletteSet = false;
            if (theme is not null && Palette(theme) is { } palette)
            {
                fluent.Palettes[theme.IsDark ? ThemeVariant.Dark : ThemeVariant.Light] = palette;
                s_paletteSet = true;
            }
        }
        app.RequestedThemeVariant = theme?.IsDark == true ? ThemeVariant.Dark : ThemeVariant.Light;
        if (theme is null)
        {
            return;
        }
        foreach (var key in Keys)
        {
            if (ColorOf(theme, key.Key) is not { } color)
            {
                continue;
            }
            foreach (var target in key.Targets.Where(t => !t.StartsWith(PalettePrefix, StringComparison.Ordinal)))
            {
                app.Resources[target] = target.EndsWith("Color", StringComparison.Ordinal) ? color : new SolidColorBrush(color);
                s_applied.Add(target);
            }
        }
    }

    /// <summary>The Fluent palette of a theme (null when it sets no palette color).</summary>
    private static ColorPaletteResources? Palette(ColorTheme theme)
    {
        ColorPaletteResources? palette = null;
        foreach (var key in Keys)
        {
            if (!theme.Colors.TryGetValue(key.Key, out var text) || Parse(text) is not { } color)
            {
                continue;
            }
            foreach (var target in key.Targets.Where(t => t.StartsWith(PalettePrefix, StringComparison.Ordinal)))
            {
                var (name, alpha) = SplitPalette(target);
                if (typeof(ColorPaletteResources).GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is { CanWrite: true } property
                    && property.PropertyType == typeof(Color))
                {
                    palette ??= new ColorPaletteResources();
                    property.SetValue(palette, alpha is { } a ? Color.FromArgb(a, color.R, color.G, color.B) : color);
                }
            }
        }
        return palette;
    }

    private static (string Name, byte? Alpha) SplitPalette(string target)
    {
        var name = target[PalettePrefix.Length..];
        var at = name.IndexOf('@', StringComparison.Ordinal);
        return at < 0 ? (name, null) : (name[..at], Convert.ToByte(name[(at + 1)..], 16));
    }

    /// <summary>The resource holding the color of a target (the palette colors are System*Color resources).</summary>
    private static string ResourceOf(string target)
    {
        if (!target.StartsWith(PalettePrefix, StringComparison.Ordinal))
        {
            return target;
        }
        var name = SplitPalette(target).Name;
        return name switch
        {
            "Accent" => "SystemAccentColor",
            "RegionColor" => "SystemRegionColor",
            _ => $"System{name}Color",
        };
    }

    /// <summary>
    /// Writes the colors in effect as a new theme file in the user's theme folder (a starting point for an own theme),
    /// every key with a comment saying what it colors. Returns the saved theme.
    /// </summary>
    public static ColorTheme Export(Application app, string name, string? directory = null)
    {
        var theme = Capture(app, name);
        var dir = directory ?? UserDirectory ?? BuiltInDirectory;
        var path = Path.Combine(dir, ColorTheme.FileNameFor(theme.Name));
        for (var i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(dir, Path.GetFileNameWithoutExtension(ColorTheme.FileNameFor(theme.Name)) + "-" + i + ColorTheme.Extension);
        }
        theme.Save(path, Keys.Select(k => (k.Key, NextCommander.Core.Localization.Localizer.T(k.Description))).ToList()); // comments in the UI language
        return theme;
    }

    /// <summary>The colors in effect as a theme (not saved): the start of a new theme in the theme editor.</summary>
    public static ColorTheme Capture(Application app, string name)
    {
        var variant = app.ActualThemeVariant;
        var theme = new ColorTheme { Name = name.Trim(), Base = variant == ThemeVariant.Dark ? "Dark" : "Light" };
        foreach (var key in Keys)
        {
            if (app.TryGetResource(ResourceOf(key.Targets[0]), variant, out var value))
            {
                if (value is ISolidColorBrush brush)
                {
                    theme.Colors[key.Key] = Hex(brush.Color);
                }
                else if (value is Color color)
                {
                    theme.Colors[key.Key] = Hex(color);
                }
            }
        }
        return theme;
    }

    /// <summary>Writes a theme into the user's themes folder (its own file when it has one there), with the comments of the keys.</summary>
    public static void SaveUserTheme(ColorTheme theme)
    {
        var dir = UserDirectory ?? BuiltInDirectory;
        var path = theme.FilePath is { } own && IsUserFile(own) ? own : Path.Combine(dir, ColorTheme.FileNameFor(theme.Name));
        theme.Save(path, Keys.Select(k => (k.Key, NextCommander.Core.Localization.Localizer.T(k.Description))).ToList());
    }

    /// <summary>Whether a file is in the user's folder (a shipped theme or pack is saved as a copy there).</summary>
    public static bool IsUserFile(string? path) =>
        path is not null && UserDirectory is { } user
        && Path.GetFullPath(path).StartsWith(Path.GetFullPath(user).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string Hex(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
}
