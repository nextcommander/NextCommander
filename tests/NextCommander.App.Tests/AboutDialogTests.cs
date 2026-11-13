using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using NextCommander.App.Views.Dialogs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Help > About: logo, version, system details, components and the buttons.</summary>
public sealed class AboutDialogTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task About_shows_the_logo_the_version_and_the_details()
    {
        var (_, vm) = await _fx.OpenAsync();
        await vm.ExecuteAsync("nc_About");
        var info = _fx.Dialogs.LastAbout;
        Assert.NotNull(info);
        Assert.Matches(@"^\d+\.\d+\.\d+$", info.Version);
        Assert.StartsWith("©", info.Copyright, StringComparison.Ordinal);
        Assert.Contains(info.Components, c => c.Name == "Avalonia");
        Assert.Contains($"NextCommander {info.Version}", info.Text, StringComparison.Ordinal);

        var dialog = _fx.Track(new AboutDialog(info));
        dialog.Show();
        await SettleAsync();
        var texts = dialog.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("NextCommander", texts);
        Assert.Contains($"Version {info.Version}", texts);
        Assert.Contains(info.SettingsFolder, texts);
        var logo = dialog.GetLogicalDescendants().OfType<Image>().Single();
        Assert.NotNull(logo.Source);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } shots)
        {
            Directory.CreateDirectory(shots);
            dialog.CaptureRenderedFrame()!.Save(Path.Combine(shots, "about.png"));
        }

        // "Open" next to the settings folder shows it in the active panel
        Directory.CreateDirectory(info.SettingsFolder);
        var open = dialog.GetLogicalDescendants().OfType<Button>().Where(b => b.Content as string == "Open").Last();
        open.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync();
        Assert.Equal(Path.TrimEndingDirectorySeparator(info.SettingsFolder), Path.TrimEndingDirectorySeparator(vm.ActivePanel.CurrentPath!.Path));
    }
}
