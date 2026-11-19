using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using NextCommander.App.Controls;
using NextCommander.App.Services;
using NextCommander.App.ViewModels;
using NextCommander.App.Views;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

public sealed class Phase2Tests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static VfsPath P(string path) => VfsPath.Local(path);

    // ------------------------------------------------------------------ tabs

    [AvaloniaFact]
    public async Task Tabs_NewSwitchCloseReopen()
    {
        var (w, vm) = await _fx.OpenAsync();
        Assert.True(vm.LeftSide.ShowTabStrip); // always shown by default
        vm.Settings.AlwaysShowTabs = false;
        vm.LeftSide.RefreshTabStrip();
        Assert.False(vm.LeftSide.ShowTabStrip);

        Press(w, Key.T, RawInputModifiers.Control);
        await SettleAsync();
        Assert.Equal(2, vm.LeftSide.Tabs.Count);
        Assert.True(vm.LeftSide.ShowTabStrip);
        Assert.Equal(1, vm.LeftSide.ActiveIndex);
        Assert.Equal(_fx.L(), vm.Left.CurrentPath!.Path);

        await vm.Left.NavigateAsync(P(_fx.L("sub")));
        Press(w, Key.Tab, RawInputModifiers.Control);
        await SettleAsync();
        Assert.Equal(0, vm.LeftSide.ActiveIndex);
        Assert.Equal(_fx.L(), vm.Left.CurrentPath!.Path);
        Assert.True(vm.Left.IsActive);
        Assert.False(vm.LeftSide.Tabs[1].IsActive);

        Press(w, Key.Tab, RawInputModifiers.Control);
        Press(w, Key.W, RawInputModifiers.Control);
        await SettleAsync();
        Assert.Single(vm.LeftSide.Tabs);

        Press(w, Key.T, RawInputModifiers.Control | RawInputModifiers.Shift);
        await SettleAsync();
        Assert.Equal(2, vm.LeftSide.Tabs.Count);
        Assert.Equal(_fx.L("sub"), vm.Left.CurrentPath!.Path);
    }

    [AvaloniaFact]
    public async Task Tabs_OpenDirUnderCursorInBackgroundTab_AndInOtherPanel()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Left.CursorItem = vm.Left.Items.Single(i => i.FullName == "sub");
        Press(w, Key.Up, RawInputModifiers.Control);
        await SettleAsync();
        Assert.Equal(2, vm.LeftSide.Tabs.Count);
        Assert.Equal(0, vm.LeftSide.ActiveIndex); // background
        Assert.Equal(_fx.L("sub"), vm.LeftSide.Tabs[1].CurrentPath!.Path);

        Press(w, Key.Up, RawInputModifiers.Control | RawInputModifiers.Shift);
        await SettleAsync();
        Assert.Equal(2, vm.RightSide.Tabs.Count);
        Assert.Equal(_fx.L("sub"), vm.Right.CurrentPath!.Path);
        Assert.True(vm.Left.IsActive); // focus stays in the left panel
    }

    [AvaloniaFact]
    public async Task LockedTab_OpensDirectoryChangesInNewTab()
    {
        var (w, vm) = await _fx.OpenAsync();
        await vm.ExecuteAsync("nc_LockTab");
        Assert.StartsWith("*", vm.Left.Title, StringComparison.Ordinal);
        var locked = vm.Left;

        await locked.NavigateAsync(P(_fx.L("sub")));
        await SettleAsync();
        Assert.Equal(2, vm.LeftSide.Tabs.Count);
        Assert.Equal(_fx.L(), locked.CurrentPath!.Path);
        Assert.Equal(_fx.L("sub"), vm.Left.CurrentPath!.Path);
        Assert.NotSame(locked, vm.Left);
        Assert.Equal(TabLockMode.None, vm.Left.LockMode);
    }

    [AvaloniaFact]
    public async Task ResettableLockedTab_ReturnsHomeWhenReactivated()
    {
        var (_, vm) = await _fx.OpenAsync();
        await vm.ExecuteAsync("nc_LockTabResettable");
        var tab = vm.Left;
        await tab.NavigateAsync(P(_fx.L("sub")));
        Assert.Equal(_fx.L("sub"), tab.CurrentPath!.Path); // allowed
        await vm.LeftSide.OpenTabAsync();
        vm.LeftSide.ActivateIndex(0);
        await SettleAsync();
        Assert.Equal(_fx.L(), tab.CurrentPath!.Path);
    }

    [AvaloniaFact]
    public async Task TabList_ActivatesTabOnOtherSide()
    {
        var (_, vm) = await _fx.OpenAsync();
        await vm.RightSide.OpenTabAsync(P(_fx.L("sub")));
        vm.RightSide.ActivateIndex(0);
        _fx.Dialogs.QuickListHandler = items => new QuickListResult(items.Single(i => i.Detail!.Contains("Right", StringComparison.Ordinal) && i.Text == "sub"), -1);
        await vm.ExecuteAsync("nc_ShowTabList");
        Assert.Same(vm.RightSide, vm.ActiveSide);
        Assert.Equal(_fx.L("sub"), vm.ActivePanel.CurrentPath!.Path);
        Assert.Equal(3, _fx.Dialogs.LastQuickList.Count); // left: 1 tab, right: 2 tabs
    }

    [AvaloniaFact]
    public async Task TabFile_SaveAndLoad_AndSettingsPersistTabs()
    {
        var (_, vm) = await _fx.OpenAsync();
        await vm.LeftSide.OpenTabAsync(P(_fx.L("sub")));
        vm.Left.CustomTitle = "İç";
        var file = Path.Combine(_fx.Root, "set.tab");
        await vm.ExecuteAsync($"SAVETABS {file}");
        vm.LeftSide.CloseOtherTabs();
        Assert.Single(vm.LeftSide.Tabs);

        await vm.ExecuteAsync($"OPENTABS {file}");
        Assert.Equal(2, vm.LeftSide.Tabs.Count);
        Assert.Equal("İç", vm.Left.Title);
        Assert.Equal(_fx.L("sub"), vm.Left.CurrentPath!.Path);

        vm.SaveSettings();
        Assert.Equal(2, vm.Settings.LeftTabs.Count);
        Assert.Equal(1, vm.Settings.LeftActiveTab);
        var store = new SettingsStore(Path.Combine(_fx.Root, "settings"));
        var reloaded = store.Load();
        Assert.Equal(2, reloaded.LeftTabs.Count);
        Assert.Equal("İç", reloaded.LeftTabs[1].Title);
    }

    // ------------------------------------------------------------------ history & hotlist

    [AvaloniaFact]
    public async Task History_ThinsPassThroughFolders_AndListNavigates()
    {
        Directory.CreateDirectory(_fx.L("sub", "deep"));
        var (_, vm) = await _fx.OpenAsync();
        var left = vm.Left;
        left.MarkWorkedHere();
        await left.NavigateAsync(P(_fx.L("sub")));          // passed through quickly
        await left.NavigateAsync(P(_fx.L("sub", "deep")));
        Assert.DoesNotContain(P(_fx.L("sub")), left.History);
        Assert.Contains(P(_fx.L()), left.History);          // worked there

        await vm.ExecuteAsync("nc_DirectoryHistory");
        var history = _fx.Dialogs.LastFolderHistory!;
        var entry = history.Entries.Single(e => e.Path.Path == _fx.L());
        Assert.True(entry.Visits > 0); // visited, with its time
        Assert.NotNull(entry.LastVisit);
        await history.Open(entry.Path);
        Assert.Equal(_fx.L(), left.CurrentPath!.Path);
    }

    [AvaloniaFact]
    public async Task CursorPosition_IsRememberedPerFolder()
    {
        File.WriteAllText(_fx.L("sub", "zz.txt"), "z");
        var (_, vm) = await _fx.OpenAsync();
        await vm.Left.NavigateAsync(P(_fx.L("sub")));
        vm.Left.CursorItem = vm.Left.Items.Single(i => i.FullName == "zz.txt");
        await vm.Left.GoUpAsync();
        Assert.Equal("sub", vm.Left.CursorItem?.FullName);
        await vm.Left.NavigateAsync(P(_fx.L("sub")));
        Assert.Equal("zz.txt", vm.Left.CursorItem?.FullName);
    }

    [AvaloniaFact]
    public async Task FrequentDirectories_RecordVisits()
    {
        var (_, vm) = await _fx.OpenAsync();
        for (var i = 0; i < 3; i++)
        {
            await vm.Left.NavigateAsync(P(_fx.L("sub")));
            await vm.Left.GoUpAsync();
        }
        var top = vm.Frequent.Top(5, DateTime.Now);
        Assert.Equal(P(_fx.L()).ToString(), top[0].Path);
        Assert.Contains(top, t => t.Path == P(_fx.L("sub")).ToString());
    }

    [AvaloniaFact]
    public async Task Hotlist_AddAndNavigate()
    {
        var (_, vm) = await _fx.OpenAsync();
        await vm.Left.NavigateAsync(P(_fx.L("sub")));
        _fx.Dialogs.NextPrompt = "Proje/Alt";
        await vm.ExecuteAsync("nc_AddToHotlist");
        var entry = Assert.Single(vm.Settings.Hotlist);
        Assert.Equal("Proje/Alt", entry.Name);

        await vm.Left.GoUpAsync();
        await vm.NavigateToHotlistEntryAsync(entry);
        Assert.Equal(_fx.L("sub"), vm.Left.CurrentPath!.Path);
    }

    // ------------------------------------------------------------------ quick search / filter

    [AvaloniaFact]
    public async Task QuickSearch_CtrlAltLetter_RejectsNonMatching_DownFindsNext()
    {
        var (w, vm) = await _fx.OpenAsync();
        Press(w, Key.F, RawInputModifiers.Control | RawInputModifiers.Alt);
        await SettleAsync(4);
        var panel = ((MainWindow)w).LeftPanel;
        Assert.True(panel.IsQuickSearchOpen);
        Assert.Equal("file2.txt", vm.Left.CursorItem?.FullName);

        w.KeyTextInput("x"); // "fx" matches nothing → rejected
        await SettleAsync(2);
        var box = panel.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "QuickText");
        Assert.Equal("f", box.Text);

        Press(w, Key.Down);
        Assert.Equal("file10.txt", vm.Left.CursorItem?.FullName);
        Press(w, Key.Escape);
        Assert.False(panel.IsQuickSearchOpen);
    }

    [AvaloniaFact]
    public async Task QuickFilter_ShowsOnlyMatches_EscClears_PerTab()
    {
        var (w, vm) = await _fx.OpenAsync();
        Press(w, Key.S, RawInputModifiers.Control);
        await SettleAsync(4);
        w.KeyTextInput("log");
        await SettleAsync(2);
        Assert.Equal(["..", "b.log"], vm.Left.Items.Select(i => i.FullName).ToArray());
        Assert.Contains("[Filter: log]", vm.Left.StatusText, StringComparison.Ordinal);
        Press(w, Key.Enter); // keep the filter
        Assert.Equal("log", vm.Left.State.Filter);

        await vm.LeftSide.OpenTabAsync();
        Assert.Null(vm.Left.Filter); // new tab: no filter
        vm.LeftSide.ActivateIndex(0);

        Press(w, Key.Y, RawInputModifiers.Control);
        Assert.Equal(6, vm.Left.Items.Count);
    }

    // ------------------------------------------------------------------ branch view, LOADLIST, sync

    [AvaloniaFact]
    public async Task BranchView_ListsAllFiles_CopyKeepsRelativePaths_BackspaceLeaves()
    {
        var (w, vm) = await _fx.OpenAsync();
        Press(w, Key.B, RawInputModifiers.Control);
        await SettleAsync();
        Assert.True(vm.Left.IsVirtual);
        var inner = Path.Combine("sub", "inner.txt");
        Assert.Contains(vm.Left.Items, i => i.FullName == inner);
        Assert.DoesNotContain(vm.Left.Items, i => i.FullName == "sub");

        vm.Left.CursorItem = vm.Left.Items.Single(i => i.FullName == inner);
        Press(w, Key.F5);
        await WaitUntilAsync(() => File.Exists(_fx.R("sub", "inner.txt")));
        Assert.True(File.Exists(_fx.R("sub", "inner.txt")));

        Press(w, Key.Back);
        await SettleAsync();
        Assert.False(vm.Left.IsVirtual);
        Assert.Contains(vm.Left.Items, i => i.FullName == "sub");
    }

    [AvaloniaFact]
    public async Task LoadList_ShowsListedFiles()
    {
        var list = Path.Combine(_fx.Root, "list.txt");
        File.WriteAllLines(list, [_fx.L("a.txt"), _fx.L("sub", "inner.txt"), _fx.L("missing.txt")]);
        var (_, vm) = await _fx.OpenAsync();
        await vm.ExecuteAsync($"LOADLIST {list}");
        Assert.True(vm.Left.IsVirtual);
        Assert.Equal(["..", "a.txt", Path.Combine("sub", "inner.txt")], vm.Left.Items.Select(i => i.FullName).ToArray());
    }

    [AvaloniaFact]
    public async Task SyncChangeDir_OtherPanelFollowsAndSuspendsWhenMissing()
    {
        Directory.CreateDirectory(_fx.R("sub"));
        Directory.CreateDirectory(_fx.L("sub", "only-left"));
        var (_, vm) = await _fx.OpenAsync();
        await vm.ExecuteAsync("nc_SyncChangeDir");

        await vm.Left.NavigateAsync(P(_fx.L("sub")));
        await SettleAsync();
        Assert.Equal(_fx.R("sub"), vm.Right.CurrentPath!.Path);

        await vm.Left.NavigateAsync(P(_fx.L("sub", "only-left")));
        await SettleAsync();
        Assert.Equal(_fx.R("sub"), vm.Right.CurrentPath!.Path);
        Assert.Contains("suspended", vm.ModeText, StringComparison.Ordinal);

        await vm.Left.NavigateAsync(P(_fx.L()));
        await SettleAsync();
        Assert.Equal(_fx.R(), vm.Right.CurrentPath!.Path);
    }

    // ------------------------------------------------------------------ command line

    [AvaloniaFact]
    public async Task CommandLine_MdCdColonAndTabCompletion()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.CommandLineText = "md new" + Path.DirectorySeparatorChar + "deeper";
        await vm.ExecuteCommandLineAsync(false);
        Assert.True(Directory.Exists(_fx.L("new", "deeper")));
        Assert.Equal("new", vm.Left.CursorItem?.FullName);

        vm.CommandLineText = "cd " + _fx.L("sub") + Path.DirectorySeparatorChar + ":";
        await vm.ExecuteCommandLineAsync(false);
        Assert.Equal(_fx.L(), vm.Left.CurrentPath!.Path);
        Assert.Equal("sub", vm.Left.CursorItem?.FullName);

        vm.CommandLineText = "notepad fi";
        await vm.CompleteCommandLineAsync();
        Assert.Equal("notepad file2.txt", vm.CommandLineText);
        await vm.CompleteCommandLineAsync(); // cycles
        Assert.Equal("notepad file10.txt", vm.CommandLineText);
        vm.CommandLineText = "cd su";
        await vm.CompleteCommandLineAsync();
        Assert.Equal("cd sub" + Path.DirectorySeparatorChar, vm.CommandLineText);
    }

    // ------------------------------------------------------------------ buttons, user commands, hotkeys

    [AvaloniaFact]
    public async Task ButtonBar_IsBuilt_AndButtonsRunInternalCommandsWithParameters()
    {
        var (w, vm) = await _fx.OpenAsync();
        var bars = w.GetVisualDescendants().OfType<ButtonBarControl>().ToList();
        var top = bars.First(b => b.IsEffectivelyVisible);
        var buttons = top.GetVisualDescendants().OfType<Button>().Count();
        Assert.Equal(vm.Settings.ButtonBar.Count(b => !b.IsSeparator), buttons);

        await vm.ExecuteButtonAsync(new ButtonDefinition { Command = "nc_SrcThumbs" });
        Assert.True(vm.Left.IsThumbnails);
        await vm.ExecuteButtonAsync(new ButtonDefinition { Command = "nc_CdTree", Parameters = "%P%N" });
        Assert.Equal(_fx.L(), vm.Left.CurrentPath!.Path); // cursor on ".." → %N empty → stays

        vm.Left.CursorItem = vm.Left.Items.Single(i => i.FullName == "sub");
        await vm.ExecuteButtonAsync(new ButtonDefinition { Command = "nc_CdTree", Parameters = "%P%N" });
        Assert.Equal(_fx.L("sub"), vm.Left.CurrentPath!.Path);
    }

    [AvaloniaFact]
    public async Task UserCommand_EmName_RunsDefinition()
    {
        var (_, vm) = await _fx.OpenAsync();
        _fx.Dialogs.NextPrompt = "uc_brief";
        _fx.Dialogs.NextButton = new ButtonDefinition { Command = "nc_SrcShort,nc_SrcBySize" };
        await vm.ExecuteAsync("nc_AddUserCommand");
        Assert.True(vm.Settings.UserCommands.ContainsKey("UC_BRIEF"));
        await vm.ExecuteAsync("uc_brief");
        Assert.True(vm.Left.IsBrief);
        Assert.Equal(SortColumn.Size, vm.Left.SortColumn);
    }

    [AvaloniaFact]
    public async Task CommandBrowser_AssignsHotkey()
    {
        var (w, vm) = await _fx.OpenAsync();
        await vm.ExecuteAsync("nc_CommandBrowser");
        var browser = _fx.Track(new NextCommander.App.Views.Dialogs.CommandBrowserWindow(_fx.Dialogs.LastCommandBrowser!));
        browser.Show();
        browser.Select("nc_SrcThumbs");
        browser.AssignShortcut("Ctrl+Shift+K");
        Assert.Equal("nc_SrcThumbs", vm.Settings.Hotkeys["Ctrl+Shift+K"]);
        browser.Close();

        Press(w, Key.K, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.True(vm.Left.IsThumbnails);
    }

    // ------------------------------------------------------------------ thumbnails, tree, layout

    private static void WriteTestPng(string path, int width, int height)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bitmap.Lock())
        {
            unsafe
            {
                var p = (uint*)fb.Address;
                for (var i = 0; i < width * height; i++)
                {
                    p[i] = 0xFF2080E0;
                }
            }
        }
        bitmap.SavePng(path);
    }

    [AvaloniaFact]
    public async Task Thumbnails_LoadImages_ArrowKeysMoveInGrid()
    {
        for (var i = 0; i < 6; i++)
        {
            WriteTestPng(_fx.L($"img{i}.png"), 200, 120);
        }
        var (w, vm) = await _fx.OpenAsync();
        Press(w, Key.F1, RawInputModifiers.Control | RawInputModifiers.Shift);
        await SettleAsync();
        Assert.True(vm.Left.IsThumbnails);

        var thumbs = w.GetVisualDescendants().OfType<ThumbnailListControl>().First(t => t.IsEffectivelyVisible);
        w.CaptureRenderedFrame(); // requests the visible thumbnails
        var img = vm.Left.Items.Single(i => i.FullName == "img0.png").Entry!;
        Thumbnail? thumb = null;
        await WaitUntilAsync(() => (thumb = vm.Left.Thumbnails!.Get(img, thumbs.TileSize, () => { })) is not null);
        Assert.True(thumb is not null, "thumbnail not loaded: " + vm.Left.Thumbnails!.LastError);
        Assert.Equal(128, thumb!.Bitmap.PixelSize.Width); // size bucket

        Press(w, Key.Home);
        Press(w, Key.Right);
        Assert.Equal(1, vm.Left.IndexOf(vm.Left.CursorItem!));
        Press(w, Key.Down);
        Assert.Equal(1 + thumbs.Columns, vm.Left.IndexOf(vm.Left.CursorItem!));
    }

    [AvaloniaFact]
    public async Task TreePane_FollowsPanel_AndTreeWindowNavigates()
    {
        var (w, vm) = await _fx.OpenAsync();
        Press(w, Key.F8, RawInputModifiers.Control);
        await SettleAsync();
        Assert.True(((MainWindow)w).LeftPanel.IsTreeVisible);
        await vm.Left.NavigateAsync(P(_fx.L("sub")));
        await SettleAsync();
        var tree = ((MainWindow)w).LeftPanel.GetVisualDescendants().OfType<DirectoryTreeView>().First();
        Assert.Equal(P(_fx.L("sub")), tree.SelectedPath);

        _fx.Dialogs.NextTreePath = P(_fx.R());
        Press(w, Key.F10, RawInputModifiers.Alt);
        await SettleAsync();
        Assert.Equal(_fx.R(), vm.Left.CurrentPath!.Path);
    }

    [AvaloniaFact]
    public async Task SinglePanelMode_HidesOtherSide()
    {
        var (w, vm) = await _fx.OpenAsync();
        await vm.ExecuteAsync("nc_100Percent");
        var main = (MainWindow)w;
        Assert.False(main.GetVisualDescendants().OfType<PanelSideView>().Last().IsVisible);
        await vm.ExecuteAsync("nc_PanelsSplitPercent 30");
        Assert.False(vm.IsSinglePanel);
        Assert.Equal(30, vm.LeftPanelPercent);
        Assert.True(main.GetVisualDescendants().OfType<PanelSideView>().Last().IsVisible);
    }

    [AvaloniaFact]
    public async Task Screenshots()
    {
        for (var i = 0; i < 4; i++)
        {
            WriteTestPng(_fx.L($"photo{i}.png"), 160 + (i * 30), 120);
        }
        var (w, vm) = await _fx.OpenAsync();
        await vm.LeftSide.OpenTabAsync(P(_fx.L("sub")));
        vm.Left.CustomTitle = "İç klasör";
        vm.LeftSide.ActivateIndex(0);
        await vm.ExecuteAsync("nc_ShowTreePane");
        await vm.RightSide.OpenTabAsync(P(_fx.L()));
        vm.Right.ViewMode = PanelViewMode.Thumbnails;
        await SettleAsync();
        vm.Right.CursorItem = vm.Right.Items.SingleOrDefault(i => i.FullName == "photo1.png")
            ?? throw new Xunit.Sdk.XunitException($"photo1.png missing; right panel is at {vm.Right.CurrentPath}");
        vm.Right.MoveCursorBy(0);
        await SettleAsync();
        w.CaptureRenderedFrame();
        await SettleAsync(30);
        var dir = Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR");
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            frame!.SavePng(Path.Combine(dir, "phase2-light.png"));
        }
    }
}
