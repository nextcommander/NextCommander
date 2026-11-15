using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using NextCommander.App.Services;
using NextCommander.Core.Config;
using NextCommander.Platform;
using NextCommander.Vfs.Local;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>PC link in the window: sharing on and off from the Net menu, the settings page, the connection type.</summary>
[Collection("Appearance")] // the configuration applies the (application-wide) appearance again
public sealed class LinkUiTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private LinkService NewService(AppSettings settings) =>
        new(settings, new SecretStore(Path.Combine(_fx.Root, "secrets")), _fx.Dialogs, new LocalFileSystemProvider()) { LoopbackOnly = true };

    [AvaloniaFact]
    public async Task Sharing_needs_a_folder_and_is_turned_on_and_off_from_the_net_menu()
    {
        var (w, vm) = await _fx.OpenAsync();
        using var link = NewService(vm.Settings);
        vm.Link = link;
        vm.Settings.LinkPort = FreePort();

        // no shared folder yet: the configuration opens on the PC link page, sharing stays off
        await vm.ExecuteAsync("nc_LinkShare");
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("Choose the folders to share first", StringComparison.Ordinal));
        Assert.Equal("PC link", _fx.Dialogs.LastSettingsSearch);
        Assert.False(vm.LinkSharing);
        Assert.False(link.IsSharing);

        // a folder chosen in the configuration: sharing goes on at once
        _fx.Dialogs.SettingsEditor = (settings, _, _) => settings.LinkShares.Add(new LinkShare { Name = "Left", Path = _fx.L() });
        await vm.ExecuteAsync("nc_LinkShare");
        Assert.True(vm.LinkSharing);
        Assert.True(link.IsSharing, link.Problem ?? link.Status());
        Assert.StartsWith("Shared folders: 1; computer name: ", link.Status(), StringComparison.Ordinal);
        Assert.Contains($"port: {vm.Settings.LinkPort}", link.Status(), StringComparison.Ordinal);

        // the menu item shows it with a check mark
        var item = w.GetLogicalDescendants().OfType<MenuItem>().Single(m => (m.CommandParameter as string) == "nc_LinkShare");
        await SettleAsync();
        Assert.True(item.IsChecked, "menu check mark");

        await vm.ExecuteAsync("nc_LinkShare");
        Assert.False(vm.LinkSharing);
        Assert.False(link.IsSharing);
        Assert.Equal("Sharing is off.", link.Status());
        await SettleAsync();
        Assert.False(item.IsChecked);
    }

    [AvaloniaFact]
    public async Task Settings_page_lists_the_shared_folders_and_forgets_allowed_computers()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.LinkShares.Add(new LinkShare { Name = "Photos", Path = _fx.L(), ReadOnly = true });
        vm.Settings.LinkTrustedPeers.Add(new LinkPeer { Name = "laptop", Fingerprint = "SHA256:01", Allowed = new DateTime(2026, 11, 15) });
        var view = new Views.Dialogs.SettingsView(vm.Settings, () => { }, []);
        var shown = w.ShowSettingsAsync(view);
        view.SelectPage("Operation/PC link");
        await SettleAsync(4);
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(w)!.SavePng(Path.Combine(dir, "settings-pc-link.png"));
        }

        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Share these folders with other computers", texts);
        Assert.Contains(texts, t => t?.StartsWith("Photos — ", StringComparison.Ordinal) == true && t.EndsWith("(read only)", StringComparison.Ordinal));
        Assert.Contains(texts, t => t?.StartsWith("laptop — ", StringComparison.Ordinal) == true);

        // "Forget": the computer is asked for the code again (on the working copy until OK)
        var forget = view.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Forget"));
        forget.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettleAsync();
        Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "(none)");
        view.Close();
        await shown;
    }

    [AvaloniaFact]
    public void Pc_link_is_a_connection_type_with_its_own_icon()
    {
        Assert.Equal(RemoteProtocol.Link, RemoteProtocols.FromScheme("link")?.Protocol);
        Assert.Equal("PC link", Views.Connections.ConnectionManagerView.TypeName(RemoteProtocol.Link));
        Assert.Null(ConnectionChecks.Problem(new RemoteConnection { Protocol = RemoteProtocol.Link, Host = "laptop" }));
        Assert.Equal("link://laptop/", new RemoteConnection { Protocol = RemoteProtocol.Link, Host = "laptop" }.Scheme + "://laptop/");
        Assert.NotNull(Views.MainWindow.LocationIcon(Core.Vfs.VfsPath.Create("link", "laptop", "/Photos")));
    }

    /// <summary>A port free for TCP and for UDP (the link uses the same number for both).</summary>
    private static int FreePort()
    {
        for (var attempt = 0; ; attempt++)
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                using var udp = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
                return port;
            }
            catch (System.Net.Sockets.SocketException) when (attempt < 20)
            {
                // that number is taken for UDP: another one
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
