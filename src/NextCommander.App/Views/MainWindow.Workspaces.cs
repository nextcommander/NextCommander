using Avalonia.Controls;
using NextCommander.Core.Config;

namespace NextCommander.App.Views;

/// <summary>
/// The window side of the file management additions of 23 November 2026: the internal terminal's place for saved
/// workspaces, and the menus filled when they open (Show > Workspaces, Mark > Tags, Commands > Saved searches, Commands > Scripts).
/// </summary>
public partial class MainWindow
{
    private void WireWorkspaces()
    {
        if (_vm is null)
        {
            return;
        }
        _vm.TerminalLayout = CurrentTerminalLayout;
        _vm.WorkspaceTerminalRequested += (_, workspace) => ApplyWorkspaceTerminal(workspace);
        WorkspacesMenu.SubmenuOpened += (_, _) => FillWorkspacesMenu();
        TagsMenu.SubmenuOpened += (_, _) => FillTagsMenu();
        SavedSearchesMenu.SubmenuOpened += (_, _) => FillSavedSearchesMenu();
        ScriptsMenu.SubmenuOpened += (_, _) => FillScriptsMenu();
    }

    /// <summary>Commands > Scripts: each script of the scripts folder, then new script / new column / the folder.</summary>
    internal void FillScriptsMenu()
    {
        if (_vm is null)
        {
            return;
        }
        var items = ScriptsMenu.Items;
        items.Clear();
        foreach (var name in _vm.ScriptNames())
        {
            var item = new MenuItem { Header = name, Icon = Services.MenuIcons.Create("nc_RunScript") };
            item.Classes.Add("noloc"); // file names
            item.Click += (_, _) => _ = _vm.ExecuteAsync("nc_RunScript \"" + name + "\"");
            items.Add(item);
        }
        if (items.Count == 0)
        {
            items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
        }
        items.Add(new Separator());
        foreach (var (header, command) in new[] { ("New script...", "nc_NewScript"), ("New column script...", "nc_NewColumnScript"), ("Open the scripts folder", "nc_OpenScriptsFolder") })
        {
            var item = new MenuItem { Header = header, Icon = Services.MenuIcons.Create(command) };
            item.Click += (_, _) => _ = _vm.ExecuteAsync(command);
            items.Add(item);
        }
        if (!_vm.Settings.ShowMenuIcons)
        {
            Services.MenuIcons.Clear(items);
        }
    }

    /// <summary>Commands > Saved searches: each saved search (opened as a folder of its results), then Find files.</summary>
    internal void FillSavedSearchesMenu()
    {
        if (_vm is null)
        {
            return;
        }
        var items = SavedSearchesMenu.Items;
        items.Clear();
        foreach (var name in _vm.Settings.SavedSearches.Keys.Order(StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new MenuItem { Header = name, Icon = Services.MenuIcons.Create("nc_OpenSavedSearch") };
            item.Classes.Add("noloc"); // the user's own names
            item.Click += (_, _) => _ = _vm.ExecuteAsync("nc_OpenSavedSearch " + name);
            items.Add(item);
        }
        if (items.Count == 0)
        {
            items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
        }
        items.Add(new Separator());
        var find = new MenuItem { Header = "Find files...", Icon = Services.MenuIcons.Create("nc_SearchFor") };
        find.Click += (_, _) => _ = _vm.ExecuteAsync("nc_SearchFor");
        items.Add(find);
        if (!_vm.Settings.ShowMenuIcons)
        {
            Services.MenuIcons.Clear(items);
        }
    }

    /// <summary>Mark > Tags: each tag with its color (checked when all the selected files have it), then the tag commands.</summary>
    internal void FillTagsMenu()
    {
        if (_vm is null)
        {
            return;
        }
        var items = TagsMenu.Items;
        items.Clear();
        var paths = _vm.ActivePanel.GetSelectedEntries().Select(e => e.Path).ToList();
        foreach (var tag in _vm.Settings.Tags)
        {
            var name = tag.Name;
            var swatch = new Border
            {
                Width = 12, Height = 12, CornerRadius = new Avalonia.CornerRadius(6),
                Background = Avalonia.Media.Color.TryParse(tag.Color, out var color) ? new Avalonia.Media.SolidColorBrush(color) : null,
            };
            var item = new MenuItem
            {
                Header = name, Icon = swatch, ToggleType = MenuItemToggleType.CheckBox, IsEnabled = paths.Count > 0,
                IsChecked = paths.Count > 0 && paths.All(p => _vm.Tags.TagsOf(p).Contains(name, StringComparer.OrdinalIgnoreCase)),
            };
            item.Classes.Add("noloc"); // the user's own names
            item.Click += (_, _) => _ = _vm.ExecuteAsync("nc_Tag " + name);
            items.Add(item);
        }
        items.Add(new Separator());
        MenuItem Command(string header, string command)
        {
            var item = new MenuItem { Header = header, Icon = Services.MenuIcons.Create(command.Split(' ')[0]) };
            item.Click += (_, _) => _ = _vm.ExecuteAsync(command);
            return item;
        }
        items.Add(Command("Tags...", "nc_Tags"));
        items.Add(Command("Take every tag off", "nc_ClearTags"));
        items.Add(_vm.ActivePanel.TagFilter is { } shown
            ? Command("Show all files again", "nc_TagFilter " + shown) // the same tag again: off
            : Command("Show only the files with a tag...", "nc_TagFilter"));
        items.Add(new Separator());
        items.Add(Command("Edit tags...", "nc_EditTags"));
        if (!_vm.Settings.ShowMenuIcons)
        {
            Services.MenuIcons.Clear(items.Skip(_vm.Settings.Tags.Count));
        }
    }

    /// <summary>Where the terminal is now and the shells of its tabs (remote sessions are not kept).</summary>
    private (WorkspaceTerminal Layout, List<string> Shells) CurrentTerminalLayout()
    {
        var shells = _terminal is null
            ? _vm?.Settings.TerminalTabs.ToList() ?? []
            : _terminal.Consoles.Where(c => !c.IsRemote).Select(c => c.Shell.Name).ToList();
        if (_vm?.IsTerminalOpen != true && _terminalSide is null)
        {
            return (WorkspaceTerminal.Closed, shells);
        }
        return (IsTerminalMaximizedPage ? WorkspaceTerminal.Maximized : WorkspaceTerminal.Below, shells);
    }

    /// <summary>
    /// The terminal where the workspace has it. Running shells are never closed: the workspace's shells open only in a
    /// terminal without tabs.
    /// </summary>
    internal void ApplyWorkspaceTerminal(Workspace workspace)
    {
        if (_vm is null)
        {
            return;
        }
        if (workspace.Terminal == WorkspaceTerminal.Closed)
        {
            if (_vm.IsTerminalOpen || _terminalSide is not null)
            {
                HideTerminal(); // the shells keep running
            }
            SelectPage(WorkspacePage.Files);
            return;
        }
        EnsureTerminal();
        if (_terminal!.Consoles.Count == 0 && workspace.TerminalTabs.Count > 0)
        {
            _terminal.RestoreTabs = workspace.TerminalTabs.ToList();
        }
        if (workspace.Terminal == WorkspaceTerminal.Maximized)
        {
            ShowTerminalPage();
            return;
        }
        ReleaseTerminalFromSide();
        RestoreTerminalSize();
        SelectPage(WorkspacePage.Files);
        ShowTerminal();
    }

    /// <summary>Show > Workspaces: save, the saved ones (each with its key), the list and editing.</summary>
    internal void FillWorkspacesMenu()
    {
        if (_vm is null)
        {
            return;
        }
        var items = WorkspacesMenu.Items;
        items.Clear();
        MenuItem Command(string header, string command, Avalonia.Input.KeyGesture? gesture = null)
        {
            var item = new MenuItem { Header = header, InputGesture = gesture };
            item.Icon = Services.MenuIcons.Create(command.Split(' ')[0]);
            item.Click += (_, _) => _ = _vm.ExecuteAsync(command);
            return item;
        }
        items.Add(Command("Save workspace...", "nc_SaveWorkspace"));
        items.Add(Command("Open workspace...", "nc_OpenWorkspace"));
        if (_vm.Settings.Workspaces.Count > 0)
        {
            items.Add(new Separator());
            foreach (var workspace in _vm.Settings.Workspaces)
            {
                Avalonia.Input.KeyGesture? gesture = null;
                try
                {
                    gesture = workspace.Hotkey.Length > 0 ? Avalonia.Input.KeyGesture.Parse(workspace.Hotkey) : null;
                }
                catch (ArgumentException)
                {
                    // a key the settings window refused: shown without it
                }
                var item = Command(workspace.Name, Workspace.CommandFor(workspace.Name), gesture);
                item.Classes.Add("noloc"); // a name of the user
                items.Add(item);
            }
        }
        items.Add(new Separator());
        items.Add(Command("Edit workspaces...", "nc_EditWorkspaces"));
        if (!_vm.Settings.ShowMenuIcons)
        {
            Services.MenuIcons.Clear(items);
        }
    }
}
