using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using NextCommander.App.Services.Icons;
using NextCommander.Core.Config;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Configuration > Icons (changes application resources: runs in the Appearance collection).</summary>
[Collection("Appearance")]
public sealed class ConfigIconsTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose()
    {
        App.ApplyIconSettings(new AppSettings());
        Services.UiFonts.Apply(App.Current!, new AppSettings());
        _fx.Dispose();
    }

    [AvaloniaFact]
    public async Task Icon_mode_hidden_marks_and_size()
    {
        File.WriteAllText(_fx.L("gizli.txt"), "g");
        File.SetAttributes(_fx.L("gizli.txt"), FileAttributes.Hidden);
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.ShowHidden = true;
        vm.Left.Reapply();
        var hidden = vm.Left.Items.First(i => i.FullName == "gizli.txt");
        Assert.True(hidden.IsDimmed);
        Assert.False(hidden.ShowHiddenMark);
        Assert.True(hidden.ShowIcon);

        vm.Settings.HiddenMark = true;
        vm.Settings.DimHiddenFiles = false;
        vm.Settings.IconMode = IconMode.None;
        vm.Settings.IconSize = 32;
        vm.ApplySettings();
        hidden = vm.Left.Items.First(i => i.FullName == "gizli.txt");
        Assert.False(hidden.IsDimmed);
        Assert.True(hidden.ShowHiddenMark);
        Assert.False(hidden.ShowIcon);
        Assert.False(vm.Left.Items[0].ShowIcon);
        Assert.Equal(32d, App.Current!.Resources["FmIconSize"]);
        Assert.Equal(36d, App.Current.Resources["FmRowHeight"]); // 32 + 2 x 1 + 2
        Assert.Equal(32d, Controls.BriefListControl.IconSize);
        Assert.False(IconSettings.UseSystemIcons);

        Assert.True(IconSettings.PerFileAllowed(_fx.L("a.txt")) || IconSettings.Mode != IconMode.AllWithExe);
    }

    [AvaloniaFact]
    public async Task Menu_icons_frame_and_pressed_checks()
    {
        var (w, vm) = await _fx.OpenAsync();
        var vertical = w.GetLogicalDescendants().OfType<MenuItem>().First(m => m.CommandParameter as string == "nc_VerticalPanels");
        Assert.Contains("pressedcheck", vertical.Classes);
        Assert.IsType<Border>(vertical.Icon);

        vm.Settings.MenuIconFrame = true;
        vm.Settings.MenuChecksAsPressed = false;
        vm.Settings.MenuIconSize = 24;
        vm.ApplySettings();
        await SettleAsync();
        Assert.DoesNotContain("pressedcheck", vertical.Classes);
        Assert.Contains("framed", ((Border)vertical.Icon!).Classes);
        Assert.Equal(24d, App.Current!.Resources["FmMenuIconSize"]);

        var dir = Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            vm.Settings.MenuChecksAsPressed = true;
            vm.Settings.MenuIconFrame = false;
            vm.ApplySettings();
            await vm.ExecuteAsync("nc_VerticalPanels");
            var show = w.GetLogicalDescendants().OfType<MenuItem>().First(m => m.Header as string == "_Show");
            show.IsSubMenuOpen = true;
            await SettleAsync();
            var popup = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(show).OfType<Avalonia.Controls.Primitives.Popup>().First();
            TopLevel.GetTopLevel(popup.Child)!.CaptureRenderedFrame()!.Save(Path.Combine(dir, "menu-show-icons24.png"));
            show.IsSubMenuOpen = false;
            await vm.ExecuteAsync("nc_VerticalPanels");
        }
    }

    [AvaloniaFact]
    public async Task Fonts_and_dialog_scaling()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.DialogFontSize = 18;
        vm.Settings.MainFontSize = 12;
        vm.Settings.DialogScalePercent = 150;
        vm.ApplySettings();
        await SettleAsync();
        Assert.Equal(18d, App.Current!.Resources["ControlContentThemeFontSize"]);
        Assert.Equal(12d, w.Resources["ControlContentThemeFontSize"]);
        Assert.Equal(12d, w.FontSize);

        var dialog = _fx.Track(new Views.Dialogs.CopyDialog(false, "2 dosya kopyalanacak:", _fx.R(), false));
        dialog.Show();
        await SettleAsync();
        Assert.Equal(18d, dialog.FontSize);
        Assert.IsType<LayoutTransformControl>(dialog.Content);
        var dir = Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            dialog.CaptureRenderedFrame()!.Save(Path.Combine(dir, "dialog-scaled.png"));
            w.CaptureRenderedFrame()!.Save(Path.Combine(dir, "main-font12.png"));
        }
        dialog.Close();
    }
}

