using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NextCommander.App.Services.Terminal;
using NextCommander.App.Views.Connections;
using NextCommander.Core.Config;
using NextCommander.Core.Net;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>
/// The connection manager's options per type: the Proxy tab (the program's proxy, none, its own with the password
/// stored on save), the Advanced tab of FTP, RDP and Telnet, and what Telnet sends for the keys.
/// </summary>
[Collection("Proxy")]
public sealed class ConnectionOptionsTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private (ConnectionManagerView View, Dictionary<string, string> Secrets) Open(AppSettings settings)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        var request = new ConnectionManagerRequest(settings, () => { }, (_, _) => Task.FromResult(string.Empty), _ => false,
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.FromResult<string?>(null))
        {
            HasSecret = secrets.ContainsKey,
            StoreSecret = (id, value) =>
            {
                secrets[id] = value;
                return Task.CompletedTask;
            },
        };
        var view = new ConnectionManagerView(request) { ConfirmDeletes = false };
        _fx.Track(new Window { Content = view, Width = 1080, Height = 720 }).Show();
        return (view, secrets);
    }

    private static TabControl Tabs(Control view) => view.GetVisualDescendants().OfType<TabControl>().Single();

    private static List<string> TabNames(Control view) => [.. Tabs(view).Items.OfType<TabItem>().Select(t => (string)t.Header!)];

    private static bool Shown(Control view, string text) =>
        view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);

    private static ComboBox ChoiceWith(Control view, string item) =>
        view.GetVisualDescendants().OfType<ComboBox>().First(c => c.ItemsSource is IEnumerable<string> items && items.Contains(item));

    private static void Click(Control view, string text)
    {
        var button = view.GetVisualDescendants().OfType<Button>().First(b => (Equals(b.Content, text) || Equals(b.Tag, text)) && b.IsEffectivelyVisible);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static TextBox BoxWith(Control view, string placeholder) =>
        view.GetVisualDescendants().OfType<TextBox>().First(t => t.PlaceholderText == placeholder && t.IsEffectivelyVisible);

    [AvaloniaFact]
    public async Task A_connection_gets_its_own_proxy_and_its_password_is_stored_on_save()
    {
        var settings = new AppSettings();
        var sftp = new RemoteConnection { Name = "web", Protocol = RemoteProtocol.Sftp, Host = "web.example.com", User = "deploy" };
        var rdp = new RemoteConnection { Name = "pc", Protocol = RemoteProtocol.Rdp, Host = "pc.example.com" };
        settings.Connections.AddRange([sftp, rdp]);
        var (view, secrets) = Open(settings);
        await SettleAsync();

        view.Reveal(rdp);
        await SettleAsync();
        Assert.DoesNotContain("Proxy", TabNames(view)); // the RDP client has its own way

        view.Reveal(sftp);
        await SettleAsync();
        Assert.Equal(["General", "Login", "Advanced", "Proxy", "Log"], TabNames(view));
        Tabs(view).SelectedIndex = 3;
        await SettleAsync();
        Assert.True(Shown(view, "As Configuration > Operation > Proxy says."));

        Click(view, "Its own proxy");
        await SettleAsync();
        Assert.Equal(ConnectionProxy.Custom, sftp.Proxy);
        Assert.Equal(3, Tabs(view).SelectedIndex); // still on the Proxy tab
        Assert.Equal("The proxy server is empty.", ConnectionChecks.Problem(sftp));

        ChoiceWith(view, "SSH tunnel").SelectedIndex = Array.FindIndex(Views.Dialogs.SettingsView.ProxyKinds, k => k.Kind == ProxyKind.Ssh);
        await SettleAsync();
        Assert.Equal(ProxyKind.Ssh, sftp.ProxyKind);
        Assert.True(Shown(view, "Private key")); // an SSH proxy can log in with a key
        BoxWith(view, "proxy.example.com").Text = "bastion.example.com";
        view.GetVisualDescendants().OfType<TextBox>().First(t => t.PasswordChar == '•').Text = "tunnel-pw";
        await SettleAsync();
        Assert.Equal("bastion.example.com", sftp.ProxyHost);
        Assert.Null(ConnectionChecks.Problem(sftp));
        Assert.Equal(new Uri("ssh://bastion.example.com:22"), WebProxyConfig.CustomOf(sftp)!.Address);
        Assert.Equal("tunnel-pw", WebProxyConfig.PasswordOf(WebProxyConfig.CustomOf(sftp)!)); // a test or connect uses it at once

        view.Save();
        Assert.Equal("tunnel-pw", secrets[WebProxyConfig.SecretIdOf(sftp)]);
        WebProxyConfig.SetPassword(WebProxyConfig.SecretIdOf(sftp), null);
    }

    [AvaloniaFact]
    public async Task The_advanced_tab_follows_the_type()
    {
        var settings = new AppSettings();
        var ftp = new RemoteConnection { Name = "edi", Protocol = RemoteProtocol.FtpsExplicit, Host = "edi.example.com", User = "u" };
        var rdp = new RemoteConnection { Name = "pc", Protocol = RemoteProtocol.Rdp, Host = "pc.example.com" };
        var telnet = new RemoteConnection { Name = "switch", Protocol = RemoteProtocol.Telnet, Host = "10.0.0.2" };
        settings.Connections.AddRange([ftp, rdp, telnet]);
        var (view, _) = Open(settings);
        await SettleAsync();

        view.Reveal(ftp);
        await SettleAsync();
        Tabs(view).SelectedIndex = 2;
        await SettleAsync();
        ChoiceWith(view, "Always LIST").SelectedIndex = 1;
        ChoiceWith(view, "TLS 1.3 only").SelectedIndex = 2;
        Assert.Equal((FtpListing.List, TlsVersion.Tls13), (ftp.FtpListing, ftp.TlsVersion));
        Assert.True(Shown(view, "Pinned certificate"));
        Assert.False(BoxWith(view, "the program's setting").IsEnabled); // the active mode ports: passive mode is on

        view.Reveal(rdp);
        await SettleAsync();
        // a remote desktop has pages of its own: Display, Local resources, Experience
        Assert.Equal(["General", "Login", "Advanced", "Display", "Local resources", "Experience", "Log"], TabNames(view));
        Tabs(view).SelectedIndex = 2;
        await SettleAsync();
        BoxWith(view, "gateway.example.com").Text = "gw.corp";
        ChoiceWith(view, "On the remote computer in full screen only").SelectedIndex = 1;
        await SettleAsync();
        Assert.Contains("gatewayhostname:s:gw.corp", Core.Net.RemoteDesktop.RdpFile(rdp), StringComparison.Ordinal);
        Assert.Equal(RdpKeyboard.Remote, rdp.RdpKeyboard);
        Tabs(view).SelectedIndex = 3;
        await SettleAsync();
        ChoiceWith(view, "1920 × 1080").SelectedIndex = Core.Net.RemoteDesktop.Sizes.ToList().IndexOf((1920, 1080));
        Tabs(view).SelectedIndex = 4;
        await SettleAsync();
        var quality = ChoiceWith(view, "High (a fast network)");
        ChoiceWith(view, "Do not play").SelectedIndex = 2;
        Assert.False(quality.IsEnabled); // no sound here: no quality
        Assert.Equal((1920, 1080, RdpAudio.None), (rdp.RdpWidth, rdp.RdpHeight, rdp.RdpAudio));
        Tabs(view).SelectedIndex = 5;
        await SettleAsync();
        // the detected speed leaves the effects to the client and the server; a chosen one fills them in
        var wallpaper = view.GetVisualDescendants().OfType<CheckBox>().First(b => Equals(b.Content, "Desktop background"));
        Assert.False(wallpaper.IsEnabled);
        ChoiceWith(view, "Modem (56 kbit/s)").SelectedIndex = (int)RdpConnectionSpeed.Modem;
        await SettleAsync();
        Assert.True(wallpaper.IsEnabled);
        Assert.Equal((RdpConnectionSpeed.Modem, false, false), (rdp.RdpSpeed, rdp.RdpWallpaper, rdp.RdpThemes));
        wallpaper.IsChecked = true;
        Assert.True(rdp.RdpWallpaper);

        view.Reveal(telnet);
        await SettleAsync();
        Tabs(view).SelectedIndex = 2;
        await SettleAsync();
        ChoiceWith(view, "Ctrl+H (8)").SelectedIndex = 1;
        ChoiceWith(view, "CR NUL").SelectedIndex = 1;
        ChoiceWith(view, "UTF-8").SelectedIndex = TelnetTerminal.Encodings.ToList().IndexOf("iso-8859-9");
        Assert.Equal((TelnetBackspace.ControlH, TelnetEnter.CrNul, "iso-8859-9"), (telnet.TelnetBackspace, telnet.TelnetEnter, telnet.TerminalEncoding));
        Assert.Equal(["General", "Login", "Advanced", "Proxy", "Log"], TabNames(view)); // a Telnet session can go through a proxy
    }

    [AvaloniaFact]
    public async Task A_remote_desktop_opens_its_files_over_smb()
    {
        var settings = new AppSettings();
        var rdp = new RemoteConnection { Name = "pc", Protocol = RemoteProtocol.Rdp, Host = "pc.example.com", User = @"CORP\ali" };
        var other = new RemoteConnection { Name = "server", Protocol = RemoteProtocol.Rdp, Host = "srv.example.com" };
        var share = new RemoteConnection { Name = "srv files", Protocol = RemoteProtocol.Smb, Host = "SRV.example.com", User = "admin" };
        settings.Connections.AddRange([rdp, other, share]);
        var (view, _) = Open(settings);
        await SettleAsync();

        view.Reveal(rdp);
        await SettleAsync();
        Click(view, "Files (SMB)");
        var result = Assert.Single(view.Result!.Connections);
        Assert.Equal((RemoteProtocol.Smb, "pc.example.com", @"CORP\ali"), (result.Protocol, result.Host, result.User)); // made from the remote desktop

        Assert.Same(share, view.SmbOf(other)); // a saved SMB connection to the same server is used
    }

    [AvaloniaFact]
    public void Telnet_sends_the_keys_as_the_connection_says()
    {
        var c = new RemoteConnection { Protocol = RemoteProtocol.Telnet };
        Assert.Equal("ls\r\n"u8.ToArray(), TelnetTerminal.Translate("ls\r"u8, c));
        Assert.Equal("ls\r\n"u8.ToArray(), TelnetTerminal.Translate("ls\r\n"u8, c)); // CR LF typed together is one Enter
        Assert.Equal(new byte[] { 0x7F }, TelnetTerminal.Translate([0x08], c));
        c.TelnetBackspace = TelnetBackspace.ControlH;
        c.TelnetEnter = TelnetEnter.CrNul;
        Assert.Equal(new byte[] { 0x08, (byte)'\r', 0 }, TelnetTerminal.Translate([0x7F, (byte)'\r'], c));
        c.TelnetEnter = TelnetEnter.Lf;
        Assert.Equal("a\n"u8.ToArray(), TelnetTerminal.Translate("a\r"u8, c));
        Assert.Null(TelnetTerminal.EncodingOf(c));
        c.TerminalEncoding = "windows-1254";
        Assert.Equal(1254, TelnetTerminal.EncodingOf(c)!.CodePage);
    }
}
