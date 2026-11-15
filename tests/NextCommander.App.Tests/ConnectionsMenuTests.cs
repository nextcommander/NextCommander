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

/// <summary>The saved connections as a drop-down of the button bar (next to the connection manager), grouped by type.</summary>
public sealed class ConnectionsMenuTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static Button ConnectionsButton(Views.MainWindow w) =>
        w.GetVisualDescendants().OfType<ButtonBarControl>().Single(b => !b.IsVertical)
            .GetVisualDescendants().OfType<Button>().Single(b => b.Tag is ButtonDefinition { Command: "nc_ConnectionsMenu" });

    [AvaloniaFact]
    public async Task The_button_shows_only_with_saved_connections_and_lists_them_by_type()
    {
        var (w, vm) = await _fx.OpenAsync(new FakeEntryPoint("s3")); // the S3 connection opens without a server
        var button = ConnectionsButton(w);
        Assert.False(button.IsVisible); // nothing to offer yet

        vm.Settings.Connections.Add(new RemoteConnection { Name = "web", Protocol = RemoteProtocol.Sftp, Host = "web.example.com" });
        vm.Settings.Connections.Add(new RemoteConnection { Name = "backup", Protocol = RemoteProtocol.S3, Host = "s3.example.com" });
        vm.Settings.Connections.Add(new RemoteConnection { Name = "api", Protocol = RemoteProtocol.Sftp, Host = "api.example.com" });
        vm.SaveSettings();
        await SettleAsync();
        Assert.True(button.IsVisible);

        // one submenu per type, in the order of the connection manager's type list; the names sorted in each
        var groups = ConnectionMenus.Items(vm).OfType<MenuItem>().ToList();
        Assert.Equal(["SFTP (2)", "Amazon S3 (1)"], groups.Select(g => (string)g.Header!));
        Assert.Equal(["api", "web"], groups[0].Items.OfType<MenuItem>().Select(i => (string)i.Header!));

        // a connection chosen opens in the active panel
        groups[1].Items.OfType<MenuItem>().Single().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        await WaitUntilAsync(() => vm.ActivePanel.CurrentPath?.Scheme == "s3");

        vm.Settings.Connections.Clear();
        vm.SaveSettings();
        await SettleAsync();
        Assert.False(button.IsVisible);
    }

    [AvaloniaFact]
    public async Task Plugin_entry_points_have_their_own_group_and_say_why_they_cannot_open()
    {
        var (w, vm) = await _fx.OpenAsync();
        var plugin = new LoadedPlugin { Folder = Path.Combine(_fx.Root, "plugins", "fake") };
        var entry = new FakeEntryPoint("fake");
        plugin.FileSystems.Add(entry);
        vm.Plugins.Add(plugin);
        Assert.True(vm.HasConnectionMenuEntries); // a plugin alone is enough for the button

        var group = Assert.IsType<MenuItem>(Assert.Single(ConnectionMenus.Items(vm)));
        Assert.Equal("fake", group.Header);
        var item = Assert.IsType<MenuItem>(Assert.Single(group.Items));
        Assert.Equal("fake places", item.Header);

        item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        await WaitUntilAsync(() => _fx.Dialogs.Messages.Count > 0);
        Assert.Contains("not installed", _fx.Dialogs.Messages[^1]);

        // the command (shortcut, command line) opens the menu over the file list when no bar shows the button
        await vm.ExecuteAsync("nc_ConnectionsMenu");
        await SettleAsync();
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
