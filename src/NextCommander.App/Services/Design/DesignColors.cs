using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace NextCommander.App.Services.Design;

/// <summary>
/// The colors of the tool windows that are made from the color theme's accent: its soft tint (selection, pressed chips),
/// the focus ring of the fields and the text on accent buttons (dark on a light accent). Called whenever a theme is
/// applied (<see cref="ColorThemes.Apply"/>); the other Nc* colors follow the theme's palette by themselves
/// (Styles/DesignTokens.axaml).
/// </summary>
public static class DesignColors
{
    private static bool s_watching;

    public static void Update(Application app)
    {
        if (!s_watching)
        {
            s_watching = true;
            app.ActualThemeVariantChanged += (_, _) => Update(app);
        }
        var variant = app.ActualThemeVariant;
        var dark = variant == ThemeVariant.Dark;
        if (!app.TryGetResource("SystemAccentColor", variant, out var value) || value is not Color accent)
        {
            return;
        }
        var soft = Color.FromArgb(dark ? (byte)0x29 : (byte)0x1A, accent.R, accent.G, accent.B);
        app.Resources["NcAccentSoftBrush"] = new SolidColorBrush(soft);
        app.Resources["NcFocusRing"] = new BoxShadows(new BoxShadow { Spread = 3, Color = soft });
        app.Resources["NcOnAccentBrush"] = new SolidColorBrush(OnAccent(accent));

        // the main window (the user's template, 22 November 2026): from the theme's panel accent (Panel.ActiveAccent)
        var panelAccent = app.TryGetResource("FmActiveAccentBrush", variant, out var brush) && brush is ISolidColorBrush solid ? solid.Color : accent;
        Color Alpha(byte a) => Color.FromArgb(a, panelAccent.R, panelAccent.G, panelAccent.B);
        app.Resources["NcMwCursorBrush"] = new SolidColorBrush(Alpha(dark ? (byte)0x24 : (byte)0x1F));
        app.Resources["NcMwCursorLineBrush"] = new SolidColorBrush(Alpha(0x80));
        // the cursor of the inactive panel: the same color, faded
        app.Resources["NcMwCursorInactiveBrush"] = new SolidColorBrush(Alpha(dark ? (byte)0x14 : (byte)0x10));
        app.Resources["NcMwCursorInactiveLineBrush"] = new SolidColorBrush(Alpha(0x40));
        app.Resources["NcMwAccentSoftBrush"] = new SolidColorBrush(Alpha(dark ? (byte)0x21 : (byte)0x1A));
    }

    /// <summary>White on a dark accent, near black on a light one (relative luminance).</summary>
    public static Color OnAccent(Color accent)
    {
        static double Channel(byte c)
        {
            var v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Channel(accent.R) + 0.7152 * Channel(accent.G) + 0.0722 * Channel(accent.B);
        return luminance > 0.45 ? Color.Parse("#1A1E27") : Colors.White;
    }
}
