using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using NextCommander.App.ViewModels;
using NextCommander.App.Views.Connections;

namespace NextCommander.App.Services;

/// <summary>
/// The saved connections as a menu, grouped by their type (SFTP, FTP, Amazon S3, Google Drive ...) in the order of
/// the connection manager's type list, then the entry points of the file system plugins (e.g. "rclone remotes")
/// under their own group. Used by the connections button of the button bar (nc_ConnectionsMenu) and by
/// Net > Saved connections.
/// </summary>
public static class ConnectionMenus
{
    /// <summary>One submenu per type with the connections in it (by name); the plugin groups after them.</summary>
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
        foreach (var group in vm.PluginEntryPoints.GroupBy(e => e.EntryGroup, StringComparer.CurrentCultureIgnoreCase))
        {
            if (items.Count > 0 && items[^1] is not Separator)
            {
                items.Add(new Separator());
            }
            var menu = new MenuItem { Header = group.Key, Icon = MenuIcons.Create("nc_Plugins"), Classes = { "noloc" }, Tag = "connections.plugin" };
            foreach (var entry in group)
            {
                var item = new MenuItem { Header = entry.EntryName, Icon = MenuIcons.Create("nc_Plugins") };
                ToolTip.SetTip(item, entry.Unavailable ?? entry.EntryRoot.ToString());
                item.Click += (_, _) => _ = vm.OpenPluginEntryAsync(entry);
                menu.Items.Add(item);
            }
            items.Add(menu);
        }
        return items;
    }

    /// <summary>
    /// The connections menu (nc_ConnectionsMenu): below its button of the button bar, or at the top left of the file
    /// list when it is called without one (command line, shortcut); the connection manager at the end.
    /// </summary>
    public static MenuFlyout Show(MainWindowViewModel vm, Control anchor, bool belowButton)
    {
        var flyout = new MenuFlyout
        {
            Placement = belowButton ? PlacementMode.BottomEdgeAlignedLeft : PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.TopLeft,
            PlacementGravity = PopupGravity.BottomRight,
        };
        foreach (var item in Items(vm))
        {
            flyout.Items.Add(item);
        }
        if (flyout.Items.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
        }
        flyout.Items.Add(new Separator());
        var manager = new MenuItem { Header = "Connection manager...", Icon = MenuIcons.Create("nc_RemoteConnection"), InputGesture = new KeyGesture(Key.F, KeyModifiers.Control) };
        manager.Click += (_, _) => _ = vm.ExecuteAsync("nc_RemoteConnection");
        flyout.Items.Add(manager);
        flyout.ShowAt(anchor);
        return flyout;
    }
}
