using NextCommander.App.Services;
using NextCommander.Core.Commands;
using NextCommander.Core.Config;

namespace NextCommander.App.ViewModels;

/// <summary>
/// Saved workspaces (Show > Workspaces; user's request, 23 November 2026): the tabs of both panels, the active panel and
/// the internal terminal's place saved under a name and opened again with one key.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The terminal's place and the shells of its tabs; set by the window (null: no terminal, as in tests).</summary>
    public Func<(WorkspaceTerminal Layout, List<string> Shells)>? TerminalLayout { get; set; }

    /// <summary>A workspace is opened: the window puts the terminal where the workspace has it.</summary>
    public event EventHandler<Workspace>? WorkspaceTerminalRequested;

    private void RegisterWorkspaceCommands(CommandRegistry c)
    {
        c.Register("nc_SaveWorkspace", "Save workspace (tabs of both panels and the terminal) under a name", _ => SaveWorkspaceAsync());
        c.Register("nc_OpenWorkspace", "Open a saved workspace (parameter: its name; none: a list)", OpenWorkspaceAsync);
        c.Register("nc_EditWorkspaces", "Edit saved workspaces", _ => ShowSettingsAsync("Saved workspaces"));
    }

    /// <summary>The current tabs, active panel and terminal as a workspace named <paramref name="name"/>.</summary>
    public Workspace CaptureWorkspace(string name)
    {
        var (layout, shells) = TerminalLayout?.Invoke() ?? (WorkspaceTerminal.Closed, []);
        return new Workspace
        {
            Name = name,
            Left = LeftSide.SnapshotStates(),
            Right = RightSide.SnapshotStates(),
            LeftActive = LeftSide.ActiveIndex,
            RightActive = RightSide.ActiveIndex,
            LeftSideActive = ReferenceEquals(ActiveSide, LeftSide),
            Terminal = layout,
            TerminalTabs = shells,
        };
    }

    private async Task SaveWorkspaceAsync()
    {
        var suggested = ActivePanel.CurrentPath is { } path ? (path.IsRoot ? path.ToDisplayString() : path.Name) : "Workspace";
        var name = await _dialogs.PromptAsync("Save workspace", "Name of the workspace (the tabs of both panels and the terminal):", suggested);
        if (name is null)
        {
            return;
        }
        name = name.Trim();
        if (Workspace.NameProblem(name) is { } problem)
        {
            await _dialogs.ShowMessageAsync("Save workspace", problem);
            return;
        }
        var existing = Workspace.Find(_settings.Workspaces, name);
        if (existing is not null && !await _dialogs.ConfirmAsync("Save workspace", $"A workspace named \"{existing.Name}\" exists. Replace it?", "Replace"))
        {
            return;
        }
        var workspace = CaptureWorkspace(name);
        if (existing is not null)
        {
            workspace.Hotkey = existing.Hotkey; // its key stays
            _settings.Workspaces[_settings.Workspaces.IndexOf(existing)] = workspace;
        }
        else
        {
            workspace.Hotkey = FreeWorkspaceKey() ?? string.Empty;
            _settings.Workspaces.Add(workspace);
        }
        Hotkeys.Rebuild();
        SaveSettings();
        if (existing is null && workspace.Hotkey.Length > 0)
        {
            await _dialogs.ShowMessageAsync("Save workspace", $"The workspace \"{workspace.Name}\" opens with {HotkeyMap.ShownGesture(workspace.Hotkey)} (Show > Workspaces > Edit workspaces... changes it).");
        }
    }

    /// <summary>The first of Ctrl+Shift+1 ... 9 that nothing uses (a default, the settings or another workspace).</summary>
    internal string? FreeWorkspaceKey()
    {
        for (var digit = 1; digit <= 9; digit++)
        {
            var gesture = $"Ctrl+Shift+D{digit}";
            if (Hotkeys.CommandFor(gesture) is null)
            {
                return gesture;
            }
        }
        return null;
    }

    private async Task OpenWorkspaceAsync(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            if (_settings.Workspaces.Count == 0)
            {
                await _dialogs.ShowMessageAsync("Workspaces", "No workspace is saved yet. Show > Workspaces > Save workspace... saves the tabs of both panels and the terminal under a name.");
                return;
            }
            var items = _settings.Workspaces.Select(w => new QuickListItem(w.Name, WorkspaceDetail(w), w)).ToList();
            var result = await _dialogs.ShowQuickListAsync("Workspaces", items, ["Delete"]);
            if (result?.Item.Value is not Workspace chosen)
            {
                return;
            }
            if (result.Action == 0)
            {
                if (await _dialogs.ConfirmAsync("Workspaces", $"Delete the workspace \"{chosen.Name}\"?", "Delete"))
                {
                    _settings.Workspaces.Remove(chosen);
                    Hotkeys.Rebuild();
                    SaveSettings();
                }
                return;
            }
            await OpenWorkspaceAsync(chosen);
            return;
        }
        if (Workspace.Find(_settings.Workspaces, name) is not { } workspace)
        {
            await _dialogs.ShowMessageAsync("Workspaces", $"There is no workspace named \"{name.Trim()}\".");
            return;
        }
        await OpenWorkspaceAsync(workspace);
    }

    /// <summary>The panels' tabs, the active panel and the terminal as the workspace has them.</summary>
    public async Task OpenWorkspaceAsync(Workspace workspace)
    {
        if (workspace.Left.Count > 0)
        {
            await LeftSide.ReplaceTabsAsync(workspace.Left, workspace.LeftActive);
        }
        if (workspace.Right.Count > 0)
        {
            await RightSide.ReplaceTabsAsync(workspace.Right, workspace.RightActive);
        }
        ActivateSide(workspace.LeftSideActive ? LeftSide : RightSide);
        WorkspaceTerminalRequested?.Invoke(this, workspace);
    }

    /// <summary>"3 + 2 tabs · terminal below · Ctrl+Shift+1" for the list of workspaces.</summary>
    internal static string WorkspaceDetail(Workspace workspace)
    {
        var parts = new List<string> { $"{workspace.Left.Count} + {workspace.Right.Count} tabs" };
        if (workspace.Terminal != WorkspaceTerminal.Closed)
        {
            parts.Add(workspace.Terminal == WorkspaceTerminal.Below ? "terminal below" : "terminal maximized");
        }
        if (workspace.Hotkey.Length > 0)
        {
            parts.Add(HotkeyMap.ShownGesture(workspace.Hotkey));
        }
        return string.Join(" · ", parts);
    }
}
