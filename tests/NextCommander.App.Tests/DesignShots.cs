using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>
/// Pictures of the windows built after the templates, light and dark: with <c>FM_DESIGN_SHOTS=&lt;folder&gt;</c>
/// <see cref="CaptureAsync"/> writes <c>&lt;name&gt;-light.png</c> and <c>&lt;name&gt;-dark.png</c> there (otherwise
/// nothing). The theme variant is application-wide: callers belong to the "Appearance" collection.
/// </summary>
internal static class DesignShots
{
    public static async Task CaptureAsync(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("FM_DESIGN_SHOTS") is not { Length: > 0 } dir)
        {
            return;
        }
        Directory.CreateDirectory(dir);
        var app = Avalonia.Application.Current!;
        var before = app.RequestedThemeVariant;
        try
        {
            foreach (var (variant, suffix) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                app.RequestedThemeVariant = variant;
                await SettleAsync(8);
                window.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, $"{name}-{suffix}.png"));
            }
        }
        finally
        {
            app.RequestedThemeVariant = before;
        }
    }
}
