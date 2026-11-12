using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using NextCommander.Core.Config;

namespace NextCommander.App.Services;

/// <summary>
/// Configuration > Font: the dialog box font (all windows but the main one), the main window font, and dialog box scaling.
/// Fluent controls take their font from the ContentControlThemeFontFamily / ControlContentThemeFontSize resources, so the
/// dialog font is set on the application and the main window font on the main window itself.
/// </summary>
public static class UiFonts
{
    private const string FamilyKey = "ContentControlThemeFontFamily";
    private const string SizeKey = "ControlContentThemeFontSize";
    private static bool s_installed;

    /// <summary>Scale of every window except the main one (1 = 100 %).</summary>
    public static double DialogScale { get; private set; } = 1;

    /// <summary>Dialog font and scaling for the application.</summary>
    public static void Apply(Application app, AppSettings settings)
    {
        Set(app.Resources, settings.DialogFontFamily, settings.DialogFontSize);
        DialogScale = Math.Clamp(settings.DialogScalePercent, 50, 300) / 100.0;
        if (!s_installed)
        {
            s_installed = true;
            Control.LoadedEvent.AddClassHandler<Window>((window, _) => ScaleDialog(window), RoutingStrategies.Direct);
        }
    }

    /// <summary>Main window font (its own resources win over the application's).</summary>
    public static void ApplyMain(Window main, AppSettings settings) => Set(main.Resources, settings.MainFontFamily, settings.MainFontSize);

    private static void Set(IResourceDictionary resources, string? family, double size)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            resources.Remove(FamilyKey);
        }
        else
        {
            resources[FamilyKey] = new FontFamily(family);
        }
        if (size is > 0 and not 14)
        {
            resources[SizeKey] = Math.Clamp(size, 8, 32);
        }
        else
        {
            resources.Remove(SizeKey); // 14 = the Fluent default
        }
    }

    /// <summary>Wraps the content of a dialog in a scale transform (the main window is not scaled).</summary>
    private static void ScaleDialog(Window window)
    {
        if (DialogScale == 1 || window is Views.MainWindow || window.Content is LayoutTransformControl { Tag: "dialogscale" } || window.Content is not Control content)
        {
            return;
        }
        window.Content = null;
        window.Content = new LayoutTransformControl { Tag = "dialogscale", LayoutTransform = new ScaleTransform(DialogScale, DialogScale), Child = content };
        if (window.SizeToContent != SizeToContent.WidthAndHeight)
        {
            if (!double.IsNaN(window.Width) && window.SizeToContent != SizeToContent.Width)
            {
                window.Width *= DialogScale;
            }
            if (!double.IsNaN(window.Height) && window.SizeToContent != SizeToContent.Height)
            {
                window.Height *= DialogScale;
            }
        }
    }
}
