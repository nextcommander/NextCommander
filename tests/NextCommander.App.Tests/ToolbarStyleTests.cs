using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Controls;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The looks of the button bar at the top (Classic, Mica, Graphite, Aurora), its switches and the ⋯ menu.</summary>
public sealed class ToolbarStyleTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static async Task ApplyAsync(ViewModels.MainWindowViewModel vm)
    {
        // the bars are built again as after the options
        await vm.ExecuteAsync("nc_SwitchButtonBar");
        await vm.ExecuteAsync("nc_SwitchButtonBar");
        await SettleAsync(3);
    }

    [AvaloniaFact]
    public async Task The_style_and_the_names_are_on_the_layout_page()
    {
        await _fx.OpenAsync();
        var window = _fx.Track(new Views.Dialogs.SettingsWindow(new AppSettings(), () => { }, []));
        window.Show();
        window.SelectPage("Layout");
        await SettleAsync(3);
        Assert.Contains("Button bar style", window.VisibleLabels);
        Assert.Contains("Names next to the icons", window.VisibleLabels);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            window.Height = 1600;
            window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Button bar style").BringIntoView();
            await SettleAsync(3);
            Directory.CreateDirectory(dir);
            window.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "toolbar-setting.png"));
        }
    }

    [AvaloniaFact]
    public async Task Each_look_keeps_the_buttons_and_shows_what_is_on()
    {
        var (w, vm) = await _fx.OpenAsync();
        w.Width = 1400;
        w.Height = 600;
        ButtonBarControl Bar() => w.GetVisualDescendants().OfType<ButtonBarControl>().First(b => !b.IsVertical);
        var count = Bar().Buttons.Count();

        foreach (var style in new[] { ToolbarStyle.Classic, ToolbarStyle.Mica, ToolbarStyle.Graphite, ToolbarStyle.Aurora })
        {
            vm.Settings.ToolbarStyle = style;
            await ApplyAsync(vm);
            var bar = Bar();
            Assert.Equal(style, bar.Style);
            Assert.Equal(count, bar.Buttons.Count()); // the same buttons in every look
            Assert.Empty(bar.Overflow); // a wide window: all fit
            if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
                w.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "toolbar-" + style.ToString().ToLowerInvariant() + ".png"));
            }
        }

        // the view the panel shows is on; another view moves the mark
        Button Of(string command) => Bar().Buttons.First(b => ((ButtonDefinition)b.Tag!).Command == command);
        vm.ActivePanel.ViewMode = PanelViewMode.Full;
        await SettleAsync();
        Assert.Contains("on", Of("nc_SrcLong").Classes);
        Assert.DoesNotContain("on", Of("nc_SrcShort").Classes);
        await vm.ExecuteAsync("nc_SrcShort");
        await SettleAsync();
        Assert.Contains("on", Of("nc_SrcShort").Classes);
        Assert.DoesNotContain("on", Of("nc_SrcLong").Classes);
        Assert.Equal(true, Bar().IsOn((ButtonDefinition)Of("nc_SrcShort").Tag!));
        Assert.Null(Bar().IsOn((ButtonDefinition)Of("nc_RereadSource").Tag!)); // switches nothing

        // hidden files: on and off
        var hidden = Of("nc_SwitchHidSys");
        var before = hidden.Classes.Contains("on");
        await vm.ExecuteAsync("nc_SwitchHidSys");
        await SettleAsync();
        Assert.NotEqual(before, Of("nc_SwitchHidSys").Classes.Contains("on"));

        // a narrow window: whole groups go into the ⋯ menu, from the right
        w.Width = 420;
        await SettleAsync(4);
        Bar().Fit();
        await SettleAsync();
        var overflow = Bar().Overflow;
        Assert.NotEmpty(overflow);
        Assert.DoesNotContain(overflow, d => d.Command == "nc_GoBack"); // the first group stays

        // names next to the icons
        vm.Settings.ToolbarLabels = true;
        w.Width = 1600;
        await ApplyAsync(vm);
        Assert.Contains(Bar().GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Refresh" && t.IsEffectivelyVisible);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } labels)
        {
            w.CaptureRenderedFrame()!.SavePng(Path.Combine(labels, "toolbar-labels.png"));
        }
    }
}
