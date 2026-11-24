using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using NextCommander.App.Services;
using NextCommander.App.ViewModels;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>Saved workspaces (Show > Workspaces; user's request, 23 November 2026).</summary>
public sealed class WorkspaceTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task A_saved_workspace_brings_back_the_tabs_and_the_active_panel()
    {
        Directory.CreateDirectory(_fx.L("one"));
        Directory.CreateDirectory(_fx.L("two"));
        var (_, vm) = await _fx.OpenAsync();
        await vm.Left.NavigateAsync(VfsPath.Local(_fx.L("one")));
        await vm.LeftSide.OpenTabAsync(VfsPath.Local(_fx.L("two")), activate: true);
        vm.Left.ViewMode = PanelViewMode.Brief;
        vm.ActivateSide(vm.RightSide);

        _fx.Dialogs.NextPrompt = "Project X";
        await vm.ExecuteAsync("nc_SaveWorkspace");
        var saved = Assert.Single(vm.Settings.Workspaces);
        Assert.Equal("Project X", saved.Name);
        Assert.Equal(2, saved.Left.Count);
        Assert.Equal(1, saved.LeftActive);
        Assert.False(saved.LeftSideActive);
        Assert.Equal("Ctrl+Shift+D1", saved.Hotkey); // the first free key, told once
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("Ctrl+Shift+1", StringComparison.Ordinal));

        // somewhere else entirely, then back
        await vm.LeftSide.ReplaceTabsAsync([new PanelSettings { Path = _fx.R() }], 0);
        vm.ActivateSide(vm.LeftSide);
        Assert.Single(vm.LeftSide.Tabs);

        await vm.ExecuteAsync("nc_OpenWorkspace project x"); // case does not matter
        Assert.Equal(2, vm.LeftSide.Tabs.Count);
        Assert.Equal(VfsPath.Local(_fx.L("one")), vm.LeftSide.Tabs[0].CurrentPath);
        Assert.Equal(VfsPath.Local(_fx.L("two")), vm.Left.CurrentPath);
        Assert.Equal(PanelViewMode.Brief, vm.Left.ViewMode);
        Assert.Same(vm.RightSide, vm.ActiveSide);

        // the key opens it too
        Assert.Equal("nc_OpenWorkspace Project X", vm.Hotkeys.CommandFor("Ctrl+Shift+D1"));
        await vm.ExecuteAsync("nc_OpenWorkspace Nothing");
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("Nothing", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Saving_under_a_used_name_replaces_it_and_keeps_its_key()
    {
        var (_, vm) = await _fx.OpenAsync();
        vm.Settings.Workspaces.Add(new Workspace { Name = "Server", Hotkey = "Ctrl+Shift+D5" });
        _fx.Dialogs.NextPrompt = "server";
        await vm.ExecuteAsync("nc_SaveWorkspace");
        var saved = Assert.Single(vm.Settings.Workspaces);
        Assert.Equal("Ctrl+Shift+D5", saved.Hotkey);
        Assert.NotEmpty(saved.Left);
        Assert.Single(_fx.Dialogs.ConfirmMessages);

        _fx.Dialogs.NextPrompt = "a, b"; // a comma would split the command line
        await vm.ExecuteAsync("nc_SaveWorkspace");
        Assert.Single(vm.Settings.Workspaces);
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("comma", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Workspace_keys_yield_to_the_users_own_shortcuts()
    {
        var (_, vm) = await _fx.OpenAsync();
        vm.Settings.Workspaces.Add(new Workspace { Name = "A", Hotkey = "Ctrl+Shift+D1" });
        vm.Settings.Hotkeys["Ctrl+Shift+D2"] = "nc_Config";
        vm.Hotkeys.Rebuild();
        Assert.Equal("Ctrl+Shift+D3", vm.FreeWorkspaceKey());
        vm.Settings.Workspaces.Add(new Workspace { Name = "B", Hotkey = "Ctrl+Shift+D2" });
        vm.Hotkeys.Rebuild();
        Assert.Equal("nc_Config", vm.Hotkeys.CommandFor("Ctrl+Shift+D2"));

        Assert.Equal("Ctrl+Shift+1", HotkeyMap.ShownGesture("Ctrl+Shift+D1"));
        Assert.Equal("Ctrl+Shift+D1", HotkeyMap.StoredGesture(" ctrl + shift + 1 "));
        Assert.Equal("Alt+F5", HotkeyMap.StoredGesture("alt+F5"));
        Assert.Equal(string.Empty, HotkeyMap.StoredGesture(""));
        Assert.Null(Views.Dialogs.SettingsView.WorkspaceProblem(new Workspace { Name = "x", Hotkey = "Ctrl+Shift+D1" }));
        Assert.NotNull(Views.Dialogs.SettingsView.WorkspaceProblem(new Workspace { Name = "x", Hotkey = "Ctrl+Nonsense" }));
        Assert.NotNull(Views.Dialogs.SettingsView.WorkspaceProblem(new Workspace { Name = " " }));
        Assert.Equal("2 + 1 tabs · terminal below · Ctrl+Shift+1", MainWindowViewModel.WorkspaceDetail(new Workspace
        {
            Left = [new(), new()], Right = [new()], Terminal = WorkspaceTerminal.Below, Hotkey = "Ctrl+Shift+D1",
        }));
    }

    [AvaloniaFact]
    public async Task The_terminal_goes_where_the_workspace_has_it()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.TerminalEmulation = false;
        vm.Settings.TerminalTabs = [];
        Assert.Equal(WorkspaceTerminal.Closed, vm.CaptureWorkspace("x").Terminal);

        w.ToggleTerminal(); // below the panels
        await SettleAsync();
        var below = vm.CaptureWorkspace("below");
        Assert.Equal(WorkspaceTerminal.Below, below.Terminal);
        Assert.NotEmpty(below.TerminalTabs);

        await vm.OpenWorkspaceAsync(new Workspace { Name = "closed", Terminal = WorkspaceTerminal.Closed });
        await SettleAsync();
        Assert.False(vm.IsTerminalOpen);

        await vm.OpenWorkspaceAsync(new Workspace { Name = "max", Terminal = WorkspaceTerminal.Maximized });
        await SettleAsync();
        Assert.True(vm.IsTerminalOpen);
        Assert.True(w.Terminal!.IsMaximized);
        Assert.Equal(WorkspaceTerminal.Maximized, vm.CaptureWorkspace("x").Terminal);

        await vm.OpenWorkspaceAsync(below);
        await SettleAsync();
        Assert.True(vm.IsTerminalOpen);
        Assert.False(w.Terminal!.IsMaximized);
        Assert.Equal(Views.WorkspacePage.Files, w.CurrentPage);
    }

    [AvaloniaFact]
    public async Task The_menu_lists_the_saved_workspaces()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.Workspaces.Add(new Workspace { Name = "Photos", Hotkey = "Ctrl+Shift+D4" });
        var menu = w.FindControl<MenuItem>("WorkspacesMenu")!;
        w.FillWorkspacesMenu();
        var item = menu.Items.OfType<MenuItem>().Single(m => m.Header as string == "Photos");
        Assert.Contains("noloc", item.Classes);
        Assert.NotNull(item.InputGesture);
        Assert.Contains(menu.Items.OfType<MenuItem>(), m => m.Header as string == "Edit workspaces...");
    }
}
