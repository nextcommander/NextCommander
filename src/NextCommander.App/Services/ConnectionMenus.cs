using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using NextCommander.App.ViewModels;
using NextCommander.App.Views.Connections;

namespace NextCommander.App.Services;

/// <summary>
/// The saved connections as a menu, grouped by their type (SFTP, FTP, Amazon S3, Google Drive ...) in the order of
/// the connection manager's type list. Used by the connection manager's button of the button bar (a drop-down when
/// there are saved connections; nc_ConnectionsMenu opens it too) and by Net > Saved connections.
/// </summary>
public static class ConnectionMenus
{
    /// <summary>One submenu per type with the connections in it (by name).</summary>
    public static List<Control> Items(MainWindowViewModel vm)
    {
        var items = new List<Control>();
        var groups = vm.SavedConnections
            .GroupBy(c => c.Protocol)
            .OrderBy(g => ConnectionManagerView.TypeOrder(g.Key))
            .ToList();
        foreach (var group in groups)
        {
            var menu = new MenuItem
            {
                Header = $"{ConnectionManagerView.TypeName(group.Key)} ({group.Count()})",
                Icon = Icons.BrandIcons.Create(group.Key),
                Classes = { "noloc" },
                Tag = "connections.group",
            };
            foreach (var connection in group.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var item = new MenuItem
                {
                    Header = connection.Name,
                    Icon = Icons.BrandIcons.Create(connection.Protocol),
                    Classes = { "noloc" },
                };
                var name = connection.Name;
                item.Click += (_, _) => _ = vm.ExecuteAsync("nc_OpenConnection \"" + name + "\"");
                menu.Items.Add(item);
            }
            items.Add(menu);
        }
        return items;
    }

    /// <summary>
    /// The connection manager's menu: the manager first, then the saved connections by type; below its button of the
    /// button bar, or at the top left of the file list when it is called without one (command line, shortcut).
    /// </summary>
    public static MenuFlyout Show(MainWindowViewModel vm, Control anchor, bool belowButton)
    {
        var flyout = NewFlyout(belowButton);
        var manager = new MenuItem { Header = "Connection manager...", Icon = MenuIcons.Create("nc_RemoteConnection"), InputGesture = new KeyGesture(Key.F, KeyModifiers.Control) };
        manager.Click += (_, _) => _ = vm.ExecuteAsync("nc_RemoteConnection");
        flyout.Items.Add(manager);
        var groups = Items(vm);
        if (groups.Count > 0)
        {
            flyout.Items.Add(new Separator());
            foreach (var item in groups)
            {
                flyout.Items.Add(item);
            }
        }
        flyout.ShowAt(anchor);
        return flyout;
    }

    /// <summary>A menu below a button of the top bar, or opening down and to the right from the anchor's corner.</summary>
    internal static MenuFlyout NewFlyout(bool belowButton) => new()
    {
        Placement = belowButton ? PlacementMode.BottomEdgeAlignedLeft : PlacementMode.AnchorAndGravity,
        PlacementAnchor = PopupAnchor.TopLeft,
        PlacementGravity = PopupGravity.BottomRight,
    };
}

/// <summary>
/// The plugins' menu (nc_PluginsMenu, a button of the button bar shown while plugins are loaded and the setting
/// <see cref="Core.Config.AppSettings.ShowPluginsButton"/> is on): the places the file system plugins offer
/// (e.g. rclone remotes) grouped by plugin, then the list of the loaded plugins.
/// </summary>
public static class PluginMenus
{
    public static List<Control> Items(MainWindowViewModel vm)
    {
        var items = new List<Control>();
        // the places for the active panel's folder first (e.g. "Git history" while it is in a repository)
        foreach (var (entry, root) in vm.PluginFolderEntriesHere())
        {
            var item = new MenuItem { Header = entry.EntryName, Icon = MenuIcons.Create("nc_Plugins"), Tag = "plugins.folder" };
            ToolTip.SetTip(item, root.ToString());
            item.Click += (_, _) => _ = vm.ActivePanel.NavigateAsync(root);
            items.Add(item);
        }
        // the plugins' actions for the folder and its selection (Git: stage, commit, switch branch ...)
        if (vm.ActivePanel.CurrentPath is { } folder)
        {
            foreach (var action in vm.PluginFolderActionsFor(folder, vm.ActivePanel.GetSelectedEntries()))
            {
                var item = new MenuItem { Header = action.Name, Icon = MenuIcons.Create("plugin." + action.Icon), Tag = "plugins.action" };
                item.Click += (_, _) => _ = vm.ActivePanel.RunPluginActionAsync(action);
                items.Add(item);
            }
        }
        if (items.Count > 0)
        {
            items.Add(new Separator());
        }
        foreach (var group in vm.PluginEntryPoints.GroupBy(e => e.EntryGroup, StringComparer.CurrentCultureIgnoreCase))
        {
            var menu = new MenuItem { Header = group.Key, Icon = MenuIcons.Create("nc_Plugins"), Classes = { "noloc" }, Tag = "plugins.group" };
            foreach (var entry in group)
            {
                var item = new MenuItem { Header = entry.EntryName, Icon = MenuIcons.Create("nc_Plugins") };
                ToolTip.SetTip(item, entry.Unavailable ?? entry.EntryRoot.ToString());
                item.Click += (_, _) => _ = vm.OpenPluginEntryAsync(entry);
                menu.Items.Add(item);
            }
            items.Add(menu);
        }
        if (items.Count > 0 && items[^1] is Separator)
        {
            items.RemoveAt(items.Count - 1);
        }
        return items;
    }

    public static MenuFlyout Show(MainWindowViewModel vm, Control anchor, bool belowButton)
    {
        var flyout = ConnectionMenus.NewFlyout(belowButton);
        foreach (var item in Items(vm))
        {
            flyout.Items.Add(item);
        }
        if (flyout.Items.Count > 0)
        {
            flyout.Items.Add(new Separator());
        }
        var list = new MenuItem { Header = "Plugins...", Icon = MenuIcons.Create("nc_Plugins") };
        list.Click += (_, _) => _ = vm.ExecuteAsync("nc_Plugins");
        flyout.Items.Add(list);
        flyout.ShowAt(anchor);
        return flyout;
    }
}
