using Avalonia.LogicalTree;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NextCommander.App.Views.Dialogs;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

[Collection("Appearance")]
public sealed class Phase7Tests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Settings_window_searches_all_settings()
    {
        var (_, vm) = await _fx.OpenAsync();
        var window = _fx.Track(new SettingsWindow(vm.Settings, () => { }, []));
        window.Show();
        await SettleAsync(4);
        Assert.Equal("Layout", window.PageTitle);
        Assert.Contains("Show command line", window.VisibleLabels);
        window.SelectPage("Display/Color");
        await SettleAsync(2);
        Assert.Contains("Color theme", window.VisibleLabels);
        window.Search("recycle");
        await SettleAsync(2);
        Assert.Contains("Delete to Recycle Bin", window.VisibleLabels);
        Assert.DoesNotContain("Color theme", window.VisibleLabels);
        window.Search("editor");
        await SettleAsync(2);
        Assert.Contains("External editor", window.VisibleLabels); // the label of the editor setting
        window.Search("yokboyleayar");
        await SettleAsync(2);
        Assert.Empty(window.VisibleLabels);
    }

    [AvaloniaFact]
    public async Task Settings_changes_apply_at_once()
    {
        var (_, vm) = await _fx.OpenAsync();
        File.WriteAllText(_fx.L(".hidden"), "h");
        File.SetAttributes(_fx.L(".hidden"), FileAttributes.Hidden);
        await vm.Left.RefreshAsync();
        Assert.DoesNotContain(vm.Left.Items, i => i.FullName == ".hidden");

        _fx.Dialogs.SettingsEditor = (settings, changed, extras) =>
        {
            settings.PanelFontSize = 16;
            settings.ShowHidden = true;
            settings.AlwaysShowTabs = true;
            changed();
            Assert.Contains(extras, e => e.Text.StartsWith("Shortcuts", StringComparison.Ordinal));
        };
        await vm.ExecuteAsync("nc_Config");
        Assert.Equal(16d, App.Current!.Resources["FmPanelFontSize"]);
        Assert.Contains(vm.Left.Items, i => i.FullName == ".hidden");
        Assert.True(vm.LeftSide.ShowTabStrip);
        App.Current.Resources["FmPanelFontSize"] = 13d;
        vm.Settings.PanelFontSize = 13;
        App.ApplyPanelFont(vm.Settings);
    }

    [AvaloniaFact]
    public async Task Arguments_open_tabs_files_and_tools()
    {
        var (w, vm) = await _fx.OpenAsync();
        await vm.HandleArgumentsAsync([_fx.R(), _fx.L("sub", "inner.txt"), "--lister", _fx.L("a.txt"), _fx.L("does-not-exist")]);
        Assert.Equal(3, vm.LeftSide.Tabs.Count);
        Assert.Equal(_fx.R(), vm.LeftSide.Tabs[1].CurrentPath!.Path);
        Assert.Equal(_fx.L("sub"), vm.Left.CurrentPath!.Path);
        Assert.Equal("inner.txt", vm.Left.CursorItem!.FullName);
        Assert.Equal("a.txt", _fx.Dialogs.LastLister!.Files[0].Name);

        Assert.Null(vm.JobsProgress);
        w.UpdateTitle();
        Assert.Equal("NextCommander", w.Title);
    }

    [Fact]
    public void Pipe_names_differ_per_settings_folder()
    {
        Assert.NotEqual(Services.SingleInstance.PipeName("C:\\a"), Services.SingleInstance.PipeName("C:\\b"));
        Assert.Equal(Services.SingleInstance.PipeName("C:\\A"), Services.SingleInstance.PipeName("c:\\a"));
        Assert.Equal([Path.GetFullPath("x"), "--edit", "sftp://h/p"], Services.SingleInstance.Normalize(["x", "--edit", "sftp://h/p"]));
        Assert.NotNull(VfsPath.Local(Path.GetTempPath()));
    }

    [AvaloniaFact]
    public async Task Settings_screenshot()
    {
        var (_, vm) = await _fx.OpenAsync();
        var window = _fx.Track(new SettingsWindow(vm.Settings, () => { }, [("Shortcuts and commands...", () => Task.CompletedTask), ("Hotlist...", () => Task.CompletedTask)]));
        window.Show();
        await SettleAsync();
        var dir = Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR");
        Assert.NotNull(Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!.SavePng(Path.Combine(dir, "phase7-settings.png"));
            foreach (var (page, file) in new[] { ("Display", "settings-display"), ("Operation/Copy and delete", "settings-copy"), ("Misc.", "settings-misc") })
            {
                window.SelectPage(page);
                await SettleAsync();
                Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!.SavePng(Path.Combine(dir, file + ".png"));
            }
        }
    }

    [AvaloniaFact]
    public async Task Settings_apply_ok_and_cancel()
    {
        var (w, vm) = await _fx.OpenAsync();
        var applied = 0;
        var window = _fx.Track(new SettingsWindow(vm.Settings, () =>
        {
            applied++;
            vm.ApplySettings();
        }, []));
        window.Show();
        await SettleAsync(4);
        CheckBox Box(string label) => window.GetLogicalDescendants().OfType<CheckBox>().First(c => c.Content as string == label);

        // Changes stay in the window until Apply
        Box("Show command line").IsChecked = false;
        Box("Show status bar").IsChecked = false;
        Box("Show drive combobox").IsChecked = false;
        Assert.True(window.IsDirty);
        Assert.True(vm.Settings.ShowCommandLine);
        window.Apply();
        await SettleAsync();
        Assert.Equal(1, applied);
        Assert.False(window.IsDirty);
        Assert.False(vm.Settings.ShowCommandLine);
        Assert.False(w.FindControl<Grid>("CommandLineRow")!.IsVisible);
        Assert.False(vm.Left.ShowStatusBar);
        Assert.False(vm.Left.ShowDriveCombo);
        Assert.Equal(vm.Settings.Left.Path, vm.Left.State.Path); // panel state untouched by Apply

        // Cancel drops what was not applied
        Box("Show command line").IsChecked = true;
        window.Close();
        await SettleAsync();
        Assert.False(vm.Settings.ShowCommandLine);

        // Breadcrumbs off: plain path text
        vm.Settings.ShowBreadcrumbs = false;
        vm.ApplySettings();
        Assert.EndsWith(Path.DirectorySeparatorChar.ToString(), vm.Left.PlainPathText, StringComparison.Ordinal);
        Assert.Contains("left", vm.Left.PlainPathText, StringComparison.Ordinal);
    }
}
