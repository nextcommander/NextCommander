using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace NextCommander.App.Services;

/// <summary>
/// Windows: the native title bar of every window follows the application's light / dark theme (DWM immersive dark mode),
/// so the bar above the menu is not left light under a dark theme (or the other way round).
/// </summary>
public static class TitleBarTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private static bool s_hooked;

    /// <summary>Follows the theme in every window opened from now on and in the windows open now.</summary>
    public static void Install(Application app)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        if (!s_hooked)
        {
            s_hooked = true;
            Control.LoadedEvent.AddClassHandler<Window>((window, _) => Apply(window), RoutingStrategies.Direct);
            app.ActualThemeVariantChanged += (_, _) => ApplyAll(app);
        }
        ApplyAll(app);
    }

    private static void ApplyAll(Application app)
    {
        if (app.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var window in desktop.Windows)
            {
                Apply(window);
            }
        }
    }

    /// <summary>Sets the title bar of one window to the theme's mode.</summary>
    public static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle() is not { } handle || handle.Handle == IntPtr.Zero)
        {
            return;
        }
        var dark = (Application.Current?.ActualThemeVariant ?? ThemeVariant.Light) == ThemeVariant.Dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle.Handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
#pragma warning disable SYSLIB1054 // ref int: classic marshalling is fine here
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
#pragma warning restore SYSLIB1054
}
