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
    public async Task Duplicate_files_open_as_a_tab()
    {
        var (w, vm) = await _fx.OpenAsync();
        var shownFile = 0;
        var request = new Services.DuplicatesRequest(vm.Tools, [_fx.L()], (_, _) => Task.CompletedTask, _ =>
        {
            shownFile++;
            return Task.CompletedTask;
        });
        var view = new Views.Tools.DuplicatesView(request);
        w.ShowDuplicates(view);
        await SettleAsync(4);
        Assert.Equal([WorkspacePage.Files, WorkspacePage.Duplicates], w.OpenPages);
        Assert.Equal(WorkspacePage.Duplicates, w.CurrentPage);
        Assert.Same(view, w.Duplicates);
        Assert.True(view.IsEffectivelyVisible);
        Assert.Equal(2, Tabs(w).Count);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(w)!.SavePng(Path.Combine(dir, "duplicates-tab.png"));
        }

        // the files tab and back; Esc closes the page
        Click(Tabs(w)[0]);
        await SettleAsync();
        Assert.False(view.IsEffectivelyVisible);
        Click(Tabs(w)[1]);
        await SettleAsync();
        view.Focus();
        Press(w, Avalonia.Input.Key.Escape);
        await SettleAsync();
        Assert.True(view.IsClosed);
        Assert.Null(w.Duplicates);
        Assert.Equal(WorkspacePage.Files, w.CurrentPage);
        Assert.Equal(0, shownFile);
    }

    [AvaloniaFact]
    public async Task Another_page_turns_off_what_works_on_the_panels()
    {
        var (w, vm) = await _fx.OpenAsync();
        var menu = w.FindControl<Menu>("MainMenu")!;
        MenuItem Top(string header) => menu.Items.OfType<MenuItem>().Single(m => (string?)m.Header == header);
        static IEnumerable<MenuItem> All(MenuItem item) => item.Items.OfType<MenuItem>().SelectMany(m => All(m).Prepend(m));
        MenuItem Item(MenuItem top, string command) => All(top).First(m => (string?)m.CommandParameter == command);
        var functionKeys = w.FindControl<Grid>("FunctionKeys")!;
        var commandLine = w.FindControl<Grid>("CommandLineRow")!;
        Assert.True(Top("_Files").IsEnabled);
        Assert.True(w.RunsOnThisPage("nc_Copy"));

        var settings = NewSettings(vm);
        var shown = w.ShowSettingsAsync(settings);
        await SettleAsync(4);
        Assert.Equal(WorkspacePage.Options, w.CurrentPage);
        // panel menus off; the menus and commands that do not need the panels stay on
        Assert.True(Top("_Files").IsEnabled); // Exit is in it
        Assert.False(Item(Top("_Files"), "nc_PackFiles").IsEnabled);
        Assert.True(Item(Top("_Files"), "nc_Exit").IsEnabled);
        Assert.False(Top("_Edit").IsEnabled);
        Assert.False(Top("_Mark").IsEnabled);
        Assert.False(Item(Top("_Show"), "nc_SrcThumbs").IsEnabled);
        Assert.True(Item(Top("_Show"), "nc_EditColumnSets").IsEnabled); // also in Configuration
        Assert.True(Top("_Commands").IsEnabled);
        Assert.False(Item(Top("_Commands"), "nc_DirTree").IsEnabled);
        Assert.True(Item(Top("_Commands"), "nc_SysInfo").IsEnabled);
        Assert.True(Top("C_onfiguration").IsEnabled);
        Assert.True(Top("_Help").IsEnabled);
        Assert.False(w.RunsOnThisPage("nc_Copy"));
        Assert.True(w.RunsOnThisPage("nc_About"));
        Assert.True(w.RunsOnThisPage("nc_SwitchWorkspaceTab"));
        Assert.False(functionKeys.IsEnabled);
        Assert.False(commandLine.IsEnabled);
        var bar = w.FindControl<Border>("TopBarHost")!.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Tag is NextCommander.Core.Config.ButtonDefinition).ToList();
        Assert.Contains(bar, b => !b.IsEnabled);

        // back on the files: everything on again
        Click(Tabs(w)[0]);
        await SettleAsync();
        Assert.True(Item(Top("_Files"), "nc_PackFiles").IsEnabled);
        Assert.True(Top("_Edit").IsEnabled);
        Assert.True(Item(Top("_Commands"), "nc_DirTree").IsEnabled);
        Assert.True(functionKeys.IsEnabled);
        Assert.True(commandLine.IsEnabled);
        Assert.All(bar, b => Assert.True(b.IsEnabled));
        w.Settings!.Close();
        await shown;
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
    public async Task Synchronize_directories_is_a_page_with_a_tab_and_close_buttons()
    {
        var (w, vm) = await _fx.OpenAsync();
        var panels = w.FindControl<Grid>("PanelsGrid")!;
        var sync = new Views.Tools.SyncDirsView(VfsPath.Local(_fx.L()), VfsPath.Local(_fx.R()), vm.Tools);
        w.ShowSync(sync);
        await SettleAsync(4);
        Assert.Equal([WorkspacePage.Files, WorkspacePage.Sync], w.OpenPages);
        Assert.Equal(WorkspacePage.Sync, w.CurrentPage);
        Assert.True(sync.IsEffectivelyVisible);
        Assert.Contains(Tabs(w)[1].GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Synchronize");
        await sync.CompareAsync(); // compares at once like the window did
        Assert.NotEmpty(sync.AllRows);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(w)!.SavePng(Path.Combine(dir, "sync-tab.png"));
        }

        // the files: the comparison stays behind its tab
        Click(Tabs(w)[0]);
        await SettleAsync();
        Assert.True(panels.IsVisible);
        Assert.False(sync.IsEffectivelyVisible);
        Assert.Same(sync, w.Sync);

        // the tab's ×
        Click(Tabs(w)[1].GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("tabclose")));
        await SettleAsync();
        Assert.True(sync.IsClosed);
        Assert.Null(w.Sync);
        Assert.Equal([WorkspacePage.Files], w.OpenPages);

        // its own × at the top right
        var again = new Views.Tools.SyncDirsView(VfsPath.Local(_fx.L()), VfsPath.Local(_fx.R()), vm.Tools);
        w.ShowSync(again);
        await SettleAsync(4);
        Click(again.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("pageclose")));
        await SettleAsync();
        Assert.Null(w.Sync);

        // Esc in it (the main window's shortcuts leave its keys alone)
        var third = new Views.Tools.SyncDirsView(VfsPath.Local(_fx.L()), VfsPath.Local(_fx.R()), vm.Tools);
        w.ShowSync(third);
        await SettleAsync(4);
        third.Focus();
        Press(w, Avalonia.Input.Key.Escape);
        await SettleAsync();
        Assert.Null(w.Sync);
        Assert.Equal(WorkspacePage.Files, w.CurrentPage);
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

    [AvaloniaFact]
    public async Task A_remote_desktop_opens_in_a_tab_of_its_own()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.CanEmbedRemoteDesktop = () => true;
        var office = new Core.Config.RemoteConnection { Name = "office pc", Protocol = Core.Config.RemoteProtocol.Rdp, Host = "pc.corp", User = "ali" };
        var lab = new Core.Config.RemoteConnection { Name = "lab", Protocol = Core.Config.RemoteProtocol.Rdp, Host = "lab.corp" };
        _fx.Dialogs.Logins.Enqueue(("secret", false)); // no saved password: asked first
        await vm.OpenSessionAsync(office);
        await SettleAsync(2);
        Assert.Empty(_fx.Dialogs.Logins);
        var desktop = Assert.Single(w.RemoteDesktops);
        Assert.Same(office, desktop.Connection);
        Assert.Equal(WorkspacePage.RemoteDesktop, w.CurrentPage);
        Assert.True(desktop.IsEffectivelyVisible);
        Assert.Equal(RemoteDesktopState.Connecting, desktop.State);
        Assert.Contains(Tabs(w), t => t.GetVisualDescendants().OfType<TextBlock>().Any(b => b.Text == "office pc"));
        // its title bar: the session's options (full screen and fitting where the control can: Windows; only while connected)
        Button Option(string text) => desktop.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, text));
        var full = Option("⛶ Full screen");
        Assert.Equal(OperatingSystem.IsWindows(), full.IsVisible);
        Assert.False(full.IsEnabled); // connecting
        Assert.False(((Avalonia.Controls.Primitives.ToggleButton)Option("⤢ Fit to the tab")).IsChecked); // the tab's own size: not scaled
        Assert.True(Option("⟳ Reconnect").IsEffectivelyVisible);

        _fx.Dialogs.Logins.Enqueue(("other", false));
        await vm.OpenSessionAsync(lab);
        await SettleAsync(2);
        Assert.Equal([WorkspacePage.Files, WorkspacePage.RemoteDesktop, WorkspacePage.RemoteDesktop], w.OpenPages);
        Assert.Same(w.RemoteDesktops[1], w.CurrentRemoteDesktop);
        Assert.False(desktop.IsEffectivelyVisible); // the first one keeps its session, hidden
        w.SelectNextPage(); // around: the files
        Assert.Equal(WorkspacePage.Files, w.CurrentPage);
        w.SelectNextPage();
        Assert.Same(desktop, w.CurrentRemoteDesktop);
        Assert.True(desktop.IsEffectivelyVisible);

        // the session ends: the tab says so and offers to connect again
        desktop.Ended("The remote desktop session ended.");
        await SettleAsync();
        Assert.Equal(RemoteDesktopState.Ended, desktop.State);
        Assert.Equal("The remote desktop session ended.", desktop.EndedText);
        desktop.Start();
        Assert.Equal(RemoteDesktopState.Connecting, desktop.State);
        Assert.Null(desktop.EndedText);

        // its tab's ×: the session ends, the next one is shown; the last one closed: the files
        w.ClosePage(WorkspacePage.RemoteDesktop);
        await SettleAsync();
        Assert.Same(w.RemoteDesktops.Single(), w.CurrentRemoteDesktop);
        Assert.Equal(WorkspacePage.RemoteDesktop, w.CurrentPage);
        w.CurrentRemoteDesktop!.Close();
        await SettleAsync();
        Assert.Empty(w.RemoteDesktops);
        Assert.Equal([WorkspacePage.Files], w.OpenPages);
        Assert.Equal(WorkspacePage.Files, w.CurrentPage);

        // its own window asked for: not in a tab (the system's client starts; none here is fine)
        vm.CanEmbedRemoteDesktop = () => false;
        Assert.False(Core.Net.RemoteDesktop.OpensInTab(new Core.Config.RemoteConnection { Protocol = Core.Config.RemoteProtocol.Rdp, RdpOwnWindow = true }));
    }
}