using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Views;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>
/// The pages of the main window: the files, the options and the maximized terminal; tabs above the panels while more
/// than one is open, the files tab with the folder of the active panel and the icon of its place.
/// </summary>
public sealed class WorkspaceTabsTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static List<Button> Tabs(MainWindow w) =>
        [.. w.FindControl<StackPanel>("WorkspaceTabs")!.Children.OfType<Button>()];

    private static void Click(Button button) => button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    private static Views.Dialogs.SettingsView NewSettings(ViewModels.MainWindowViewModel vm) => new(vm.Settings, () => { }, []);

    [AvaloniaFact]
    public async Task Options_open_as_a_tab_next_to_the_files()
    {
        var (w, vm) = await _fx.OpenAsync();
        var host = w.FindControl<Border>("WorkspaceTabsHost")!;
        var panels = w.FindControl<Grid>("PanelsGrid")!;
        Assert.False(host.IsVisible); // the files alone
        Assert.Equal([WorkspacePage.Files], w.OpenPages);

        var settings = NewSettings(vm);
        var shown = w.ShowSettingsAsync(settings);
        await SettleAsync(4);
        Assert.True(host.IsVisible);
        Assert.Equal([WorkspacePage.Files, WorkspacePage.Options], w.OpenPages);
        Assert.Equal(WorkspacePage.Options, w.CurrentPage);
        Assert.True(settings.IsEffectivelyVisible);
        var tabs = Tabs(w);
        Assert.Equal(2, tabs.Count);
        Assert.Contains("current", tabs[1].Classes);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(w)!.SavePng(Path.Combine(dir, "workspace-tabs.png"));
        }

        // back to the files: the options stay open behind their tab
        Click(tabs[0]);
        await SettleAsync();
        Assert.Equal(WorkspacePage.Files, w.CurrentPage);
        Assert.False(settings.IsEffectivelyVisible);
        Assert.True(panels.IsVisible);
        Assert.Same(settings, w.Settings);
        Assert.Contains("current", Tabs(w)[0].Classes);

        // and to the options again
        Click(Tabs(w)[1]);
        await SettleAsync();
        Assert.True(settings.IsEffectivelyVisible);

        // the × of the options closes them: the files alone, no tabs
        var close = Tabs(w)[1].GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tabclose"));
        Click(close);
        await shown;
        await SettleAsync();
        Assert.Null(w.Settings);
        Assert.False(host.IsVisible);
        Assert.Equal(WorkspacePage.Files, w.CurrentPage);
    }

    [AvaloniaFact]
    public async Task Maximized_terminal_and_options_are_pages_and_the_files_tab_follows_the_active_panel()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.TerminalEmulation = false;
        vm.Settings.TerminalTabs = [];
        var panels = w.FindControl<Grid>("PanelsGrid")!;
        var terminalHost = w.FindControl<Border>("TerminalHost")!;

        await vm.ExecuteAsync("nc_MaximizeTerminal");
        await SettleAsync();
        Assert.Equal([WorkspacePage.Files, WorkspacePage.Terminal], w.OpenPages);
        Assert.Equal(WorkspacePage.Terminal, w.CurrentPage);
        Assert.False(panels.IsVisible);

        // the files tab: the folder of the active panel
        var filesTab = Tabs(w)[0];
        Assert.Contains(filesTab.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.ActivePanel.Title);

        // the files page: the panels, the maximized terminal waits behind its tab (its shells keep running)
        Click(filesTab);
        await SettleAsync();
        Assert.True(panels.IsVisible);
        Assert.False(terminalHost.IsVisible);
        Assert.True(w.Terminal!.IsMaximized);

        // another folder in the active panel: the files tab follows it
        await vm.ActivePanel.NavigateAsync(VfsPath.Local(_fx.R()));
        await SettleAsync();
        Assert.Contains(Tabs(w)[0].GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.ActivePanel.Title);

        // the terminal command brings its page back (not a second terminal)
        await vm.ExecuteAsync("nc_ToggleTerminal");
        await SettleAsync();
        Assert.Equal(WorkspacePage.Terminal, w.CurrentPage);
        Assert.True(terminalHost.IsVisible);

        // the options too: three pages, nc_SwitchWorkspaceTab goes round them
        var shown = w.ShowSettingsAsync(NewSettings(vm));
        await SettleAsync(4);
        Assert.Equal([WorkspacePage.Files, WorkspacePage.Options, WorkspacePage.Terminal], w.OpenPages);
        Assert.Equal(WorkspacePage.Options, w.CurrentPage);
        Assert.False(terminalHost.IsVisible);
        await vm.ExecuteAsync("nc_SwitchWorkspaceTab");
        Assert.Equal(WorkspacePage.Terminal, w.CurrentPage);
        await vm.ExecuteAsync("nc_SwitchWorkspaceTab");
        Assert.Equal(WorkspacePage.Files, w.CurrentPage);

        // the × of the terminal: back below the panels, its page goes
        w.ClosePage(WorkspacePage.Terminal);
        await SettleAsync();
        Assert.False(w.Terminal.IsMaximized);
        Assert.True(terminalHost.IsVisible);
        Assert.Equal([WorkspacePage.Files, WorkspacePage.Options], w.OpenPages);
        w.Settings!.Close();
        await shown;
        w.Terminal.Stop();
    }

    [AvaloniaFact]
    public void The_files_tab_shows_the_icon_of_the_place()
    {
        // a server: the product mark of the connection type; an archive: the archive icon; a plugin: the plugin icon
        Assert.IsNotType<PathIcon>(MainWindow.LocationIcon(VfsPath.Create("sftp", "server", "/home")));
        var archive = Assert.IsType<PathIcon>(MainWindow.LocationIcon(VfsPath.Create(VfsPath.ArchiveScheme, @"C:\a.zip", "/")));
        Assert.Same(Services.Icons.FileKinds.GeometryFor(Services.Icons.FileKind.Archive), archive.Data);
        Assert.NotNull(MainWindow.LocationIcon(VfsPath.Create("rclone", "remote", "/")));
        Assert.NotNull(MainWindow.LocationIcon(null));

        // a user folder of the system (downloads): the icon the system gives it, else its own glyph; never the plain folder
        var downloads = NextCommander.Platform.KnownFolders.PathOf(NextCommander.Platform.UserFolder.Downloads);
        if (downloads is not null && Directory.Exists(downloads))
        {
            var icon = MainWindow.LocationIcon(VfsPath.Local(downloads));
            var plain = Services.Icons.IconPacks.Glyph("MiFolderOpen");
            Assert.False(icon is PathIcon p && ReferenceEquals(p.Data, plain));
        }
    }
}
