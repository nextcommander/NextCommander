using Avalonia.Headless.XUnit;
using NextCommander.App.Services;
using NextCommander.App.Services.Terminal;
using NextCommander.App.Views;
using NextCommander.Core.Config;
using NextCommander.Platform;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The files of an SSH session over SFTP (MobaXterm's SFTP browser next to an SSH session).</summary>
public sealed class SshFilesTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static RemoteConnection Ssh() => new()
    {
        Name = "web", Protocol = RemoteProtocol.Ssh, Host = "web.example.com", Port = 2222, User = "deploy",
        KeyFile = @"C:\keys\deploy.ppk", JumpHost = "bastion.example.com", UseSshAgent = true,
    };

    [AvaloniaFact]
    public void An_ssh_session_gives_its_files_over_sftp_with_the_same_login()
    {
        var ssh = Ssh();
        var files = ssh.FilesOverSsh([], "/var/www");
        Assert.Equal((RemoteProtocol.Sftp, "web.example.com", 2222, "deploy", "/var/www"), (files.Protocol, files.Host, files.EffectivePort, files.User, files.InitialPath));
        Assert.Equal((ssh.KeyFile, ssh.JumpHost, true), (files.KeyFile, files.JumpHost, files.UseSshAgent));
        Assert.Equal(RemoteProtocol.Ssh, ssh.Protocol); // the session itself stays

        // a saved SFTP connection to the same server is taken (its own settings), in the shell's folder
        var saved = new RemoteConnection { Name = "web files", Protocol = RemoteProtocol.Sftp, Host = "web.example.com", Port = 2222, User = "deploy", InitialPath = "/srv" };
        var other = new RemoteConnection { Name = "other", Protocol = RemoteProtocol.Sftp, Host = "other.example.com", User = "deploy" };
        Assert.Equal("web files", ssh.FilesOverSsh([other, saved]).Name);
        Assert.Equal("/srv", ssh.FilesOverSsh([saved]).InitialPath);
        Assert.Equal("/home/deploy", ssh.FilesOverSsh([saved], "/home/deploy").InitialPath);
        Assert.Equal("/", ssh.FilesOverSsh([]).InitialPath);
    }

    [AvaloniaFact]
    public void The_shell_reports_its_folder_with_osc_7()
    {
        var screen = new VtScreen(80, 24);
        Assert.Null(screen.WorkingDirectory);
        var changed = 0;
        screen.WorkingDirectoryChanged += () => changed++;
        screen.Feed("\u001b]7;file://web/var/www/my%20site\u0007$ ");
        Assert.Equal("/var/www/my site", screen.WorkingDirectory);
        screen.Feed("\u001b]7;file://web/home/deploy\u001b\\");
        Assert.Equal("/home/deploy", screen.WorkingDirectory);
        Assert.Equal(2, changed);
    }

    [AvaloniaFact]
    public async Task A_remembered_connection_keeps_its_login_and_the_ssh_password()
    {
        await _fx.OpenAsync();
        var secrets = new SecretStore(Path.Combine(_fx.Root, "secrets"));
        var session = new RemoteSession(new AppSettings(), secrets, _fx.Dialogs);
        var ssh = Ssh();
        await secrets.SetAsync(ssh.SecretId, "s3cret");
        var files = ssh.FilesOverSsh([]);
        session.Remember(files, ssh.SecretId);

        var resolved = session.Hooks.ResolveConnection(files.Scheme, files.Authority);
        Assert.Equal((ssh.KeyFile, ssh.JumpHost), (resolved.KeyFile, resolved.JumpHost)); // not just the address
        Assert.Equal("s3cret", await session.Hooks.GetPassword!(resolved, false)); // the session's saved password
    }

    [AvaloniaFact]
    public async Task The_terminal_offers_the_files_of_an_ssh_tab_in_the_shells_folder()
    {
        await _fx.OpenAsync();
        var view = new TerminalView();
        var local = view.NewTab(new TerminalSession.ShellInfo("local", "cmd", string.Empty, true, string.Empty), (_, _) => new NeverTerminal());
        Assert.False(view.OffersFiles);
        var ssh = Ssh();
        var tab = (EmulatedTerminal)view.NewTab(new TerminalSession.ShellInfo("web", "ssh", string.Empty, true, string.Empty), (_, _) => new NeverTerminal(), ssh);
        Assert.True(view.OffersFiles);
        (RemoteConnection Connection, string? Folder)? asked = null;
        view.FilesRequested += (_, e) => asked = e;
        tab.Screen.Feed("\u001b]7;file://web/var/log\u0007");
        view.OpenFiles();
        Assert.Same(ssh, asked!.Value.Connection);
        Assert.Equal("/var/log", asked.Value.Folder);
        view.Select(local);
        Assert.False(view.OffersFiles);
        await SettleAsync();
        view.Stop();
    }

    /// <summary>A terminal that never connects (the tab only needs one).</summary>
    private sealed class NeverTerminal : PseudoTerminal
    {
        public override void Write(byte[] data)
        {
        }

        public override void Resize(int columns, int rows)
        {
        }

        public override void Dispose()
        {
        }
    }
}
