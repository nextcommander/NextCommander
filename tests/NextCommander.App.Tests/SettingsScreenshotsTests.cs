using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>
/// Pictures of the configuration: every page of the settings window (FM_SETTINGS_SCREENSHOTS=&lt;folder&gt;, the window
/// as tall as the page so nothing is cut off) and the button bar editors (FM_SCREENSHOT_DIR).
/// </summary>
[Collection("Appearance")]
public sealed class SettingsScreenshotsTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Every_settings_page()
    {
        if (Environment.GetEnvironmentVariable("FM_SETTINGS_SCREENSHOTS") is not { Length: > 0 } dir)
        {
            return;
        }
        Directory.CreateDirectory(dir);
        var (_, vm) = await _fx.OpenAsync();
        var probe = _fx.Track(new SettingsWindow(vm.Settings, () => { }, []));
        probe.Show();
        await SettleAsync();
        var pages = probe.Pages;
        probe.Close();
        Assert.NotEmpty(pages);

        var number = 0;
        foreach (var page in pages)
        {
            var window = _fx.Track(new SettingsWindow(vm.Settings, () => { }, []) { Width = 1100, Height = 780 });
            window.Show();
            window.SelectPage(page);
            await SettleAsync(6);
            // as tall as the page: the scrolled part of the settings is in the picture too
            var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().OrderByDescending(s => s.Extent.Height * s.Viewport.Width).First();
            var hidden = scroll.Extent.Height - scroll.Viewport.Height;
            if (hidden > 1)
            {
                window.Height = Math.Min(6000, window.Height + hidden + 8);
                await SettleAsync(6);
            }
            var name = $"{++number:00} {string.Join(" - ", page.Split('/'))}";
            foreach (var bad in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(bad, '_');
            }
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, name + ".png"));
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Button_bar_editors()
    {
        var (_, vm) = await _fx.OpenAsync();
        var bar = _fx.Track(new ButtonBarEditorDialog("Change button bar", vm.Settings.ButtonBar, 24, false,
            (b, size) => Controls.ButtonBarControl.CreateIcon(b, size, vm), _ => Task.FromResult<string?>(null)));
        bar.Show();
        await SettleAsync(4);
        Assert.Equal(24, bar.SelectedSize);

        // "Run as administrator" is the '*' in front of the command
        bar.Select(bar.Buttons.ToList().FindIndex(b => b.Command == "nc_ExecuteTerminal"));
        await SettleAsync();
        Assert.False(bar.Fields.RunAsAdministrator.IsChecked);
        bar.Fields.RunAsAdministrator.IsChecked = true;
        await SettleAsync();
        Assert.Equal("*nc_ExecuteTerminal", bar.Buttons.Single(b => b.Command.EndsWith("nc_ExecuteTerminal", StringComparison.Ordinal)).Command);
        bar.Fields.Command.Text = "nc_ExecuteTerminal";
        await SettleAsync();
        Assert.False(bar.Fields.RunAsAdministrator.IsChecked);

        // dragging moves a button
        var first = bar.Buttons[0];
        bar.MoveTo(0, 3);
        Assert.Same(first, bar.Buttons[3]);
        Shot(bar, "button-bar-editor");

        var single = _fx.Track(new ButtonEditDialog(new ButtonDefinition { Command = "notepad.exe", Tooltip = "Notepad" }, () => Task.FromResult<string?>(null)));
        single.Show();
        await SettleAsync(4);
        Assert.Equal("notepad.exe", single.Fields.Command.Text);
        single.Fields.Label.Text = "Not";
        single.Finish();
        Assert.Equal(("notepad.exe", "Not", "Notepad"), (single.Result!.Command, single.Result.Label, single.Result.Tooltip));
        var empty = _fx.Track(new ButtonEditDialog(null, () => Task.FromResult<string?>(null)));
        empty.Show();
        await SettleAsync(4);
        Shot(empty, "button-edit");
    }

    private static void Shot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, name + ".png"));
        }
    }
}
