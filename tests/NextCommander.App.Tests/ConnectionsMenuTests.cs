using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NextCommander.App.Controls;
using NextCommander.App.Services;
using NextCommander.Core.Config;
using NextCommander.Core.Plugins;
using NextCommander.Core.Vfs;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>
/// The menus of the button bar: the connection manager's button with the saved connections grouped by type, and the
/// plugins' button (only while plugins are loaded and the setting shows it).
/// </summary>
[Collection("Appearance")] // ApplySettings applies the (application-wide) appearance again
public sealed class ConnectionsMenuTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static Button BarButton(Views.MainWindow w, string command) =>
        w.GetVisualDescendants().OfType<ButtonBarControl>().Single(b => !b.IsVertical)
            .GetVisualDescendants().OfType<Button>().Single(b => b.Tag is ButtonDefinition d && d.Command == command);

    private static void Click(Avalonia.Interactivity.Interactive control, Avalonia.Interactivity.RoutedEvent click) =>
        control.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(click));

    [AvaloniaFact]
    public async Task The_connection_manager_button_lists_the_saved_connections_by_type()
    {
        var (w, vm) = await _fx.OpenAsync(new FakeEntryPoint("s3")); // the S3 connection opens without a server
        Assert.DoesNotContain(vm.Settings.ButtonBar, b => b.Command == "nc_ConnectionsMenu"); // no separate button any more
        var button = BarButton(w, "nc_RemoteConnection");
        Assert.DoesNotContain(button.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "▾");

        // without saved connections the button opens the manager at once
        Click(button, Button.ClickEvent);
        await WaitUntilAsync(() => _fx.Dialogs.LastManager is not null);

        vm.Settings.Connections.Add(new RemoteConnection { Name = "web", Protocol = RemoteProtocol.Sftp, Host = "web.example.com" });
        vm.Settings.Connections.Add(new RemoteConnection { Name = "backup", Protocol = RemoteProtocol.S3, Host = "s3.example.com" });
        vm.Settings.Connections.Add(new RemoteConnection { Name = "api", Protocol = RemoteProtocol.Sftp, Host = "api.example.com" });
        vm.SaveSettings();
        await SettleAsync();
        button = BarButton(w, "nc_RemoteConnection");
        Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "▾"); // a drop-down now

        // the manager first, then one submenu per type in the order of the manager's type list; the names sorted in each
        var menu = ConnectionMenus.Show(vm, button, belowButton: true);
        await SettleAsync();
        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(["Connection manager...", "SFTP (2)", "Amazon S3 (1)"], items.Select(g => (string)g.Header!));
        Assert.Equal(["api", "web"], items[1].Items.OfType<MenuItem>().Select(i => (string)i.Header!));
        menu.Hide();

        // a connection chosen opens in the active panel
        Click(items[2].Items.OfType<MenuItem>().Single(), MenuItem.ClickEvent);
        await WaitUntilAsync(() => vm.ActivePanel.CurrentPath?.Scheme == "s3");

        // the command (shortcut, command line) shows the same menu
        await vm.ExecuteAsync("nc_ConnectionsMenu");
        await SettleAsync();
    }

    [AvaloniaFact]
    public async Task The_plugins_button_shows_while_plugins_are_loaded_and_the_setting_is_on()
    {
        var (w, vm) = await _fx.OpenAsync();
        var button = BarButton(w, "nc_PluginsMenu");
        Assert.False(button.IsVisible); // no plugins

        var plugin = new LoadedPlugin { Folder = Path.Combine(_fx.Root, "plugins", "fake") };
        var entry = new FakeEntryPoint("fake");
        plugin.FileSystems.Add(entry);
        vm.Plugins.Add(plugin);
        vm.ApplySettings();
        await SettleAsync();
        button = BarButton(w, "nc_PluginsMenu");
        Assert.True(button.IsVisible);
        Assert.Empty(ConnectionMenus.Items(vm)); // the plugins are not among the connections

        // a group per plugin with its places, then the plugin list
        var menu = PluginMenus.Show(vm, button, belowButton: true);
        await SettleAsync();
        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(["fake", "Plugins..."], items.Select(i => (string)i.Header!));
        var place = Assert.IsType<MenuItem>(Assert.Single(items[0].Items));
        Assert.Equal("fake places", place.Header);
        menu.Hide();

        // a place that cannot open says why
        Click(place, MenuItem.ClickEvent);
        await WaitUntilAsync(() => _fx.Dialogs.Messages.Count > 0);
        Assert.Contains("not installed", _fx.Dialogs.Messages[^1]);

        // the setting hides it
        vm.Settings.ShowPluginsButton = false;
        vm.ApplySettings();
        await SettleAsync();
        Assert.False(BarButton(w, "nc_PluginsMenu").IsVisible); // (the bar may have been built again)
        Assert.False(vm.HasPluginMenu);
    }

    /// <summary>A plugin file system with an entry point whose program is missing.</summary>
    private sealed class FakeEntryPoint(string scheme) : IFileSystemProvider, IPluginEntryPoint
    {
        public string Scheme => scheme;

        public ProviderCapabilities Capabilities => ProviderCapabilities.Read;

        public string EntryName => "fake places";

        public string EntryGroup => "fake";

        public VfsPath EntryRoot => VfsPath.Create("fake", string.Empty, "/");

        public string? Unavailable => "fake is not installed.";

        public Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<VfsEntry>>([]);

        public Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken) =>
            Task.FromResult<VfsEntry?>(new VfsEntry { Name = path.Name, Path = path, IsDirectory = true });

        public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
