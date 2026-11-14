using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using NextCommander.App.Services;
using NextCommander.App.ViewModels;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;
using NextCommander.Platform;
using NextCommander.Vfs.Remote;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

public sealed class Phase6Tests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static void SetCursor(PanelViewModel panel, string name) =>
        panel.MoveCursorTo(panel.Items.ToList().FindIndex(i => i.FullName == name));

    /// <summary>Main window with real FTP / SFTP providers wired like the application (fake dialogs).</summary>
    private async Task<(Views.MainWindow Window, MainWindowViewModel Vm, RemoteSession Session)> OpenWithRemoteAsync(AppSettings? settings = null)
    {
        var secrets = new SecretStore(Path.Combine(_fx.Root, "secrets"));
        var holder = new AppSettings();
        var session = new RemoteSession(holder, secrets, _fx.Dialogs);
        var sftp = new SftpFileSystemProvider(session.Hooks);
        var ftp = new FtpFileSystemProvider(session.Hooks);
        var (w, vm) = await _fx.OpenAsync(sftp, ftp);
        // The session reads connections / known hosts from the window's settings.
        var live = new RemoteSession(vm.Settings, secrets, _fx.Dialogs);
        sftp.Hooks.ResolveConnection = live.Hooks.ResolveConnection;
        sftp.Hooks.GetPassword = live.Hooks.GetPassword;
        sftp.Hooks.LoginSucceeded = live.Hooks.LoginSucceeded;
        sftp.Hooks.KnownFingerprint = live.Hooks.KnownFingerprint;
        sftp.Hooks.ConfirmFingerprint = live.Hooks.ConfirmFingerprint;
        vm.Remote = live;
        if (settings is not null)
        {
            vm.Settings.Connections.AddRange(settings.Connections);
        }
        return (w, vm, live);
    }

    public static TheoryData<string> Servers()
    {
        var data = new TheoryData<string>();
        foreach (var variable in new[] { "FM_TEST_SFTP", "FM_TEST_FTP" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } url)
            {
                data.Add(url);
            }
        }
        if (data.Count == 0)
        {
            data.Add(string.Empty);
        }
        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Servers))]
    public async Task Connect_copy_save_password_and_disconnect(string url)
    {
        if (url.Length == 0)
        {
            return; // no test server configured
        }
        var uri = new Uri(url);
        var user = Uri.UnescapeDataString(uri.UserInfo.Split(':', 2)[0]);
        var password = Uri.UnescapeDataString(uri.UserInfo.Split(':', 2)[1]);
        var folder = uri.AbsolutePath.TrimEnd('/') + "/ui-" + Guid.NewGuid().ToString("N")[..6];
        var connection = new RemoteConnection
        {
            Name = "test",
            Protocol = uri.Scheme == "sftp" ? RemoteProtocol.Sftp : RemoteProtocol.Ftp,
            Host = uri.Host,
            Port = uri.Port,
            User = user,
            InitialPath = "/",
            SavePassword = true,
        };
        var (w, vm, session) = await OpenWithRemoteAsync();
        vm.Settings.Connections.Add(connection);

        _fx.Dialogs.Logins.Enqueue(("yanlış", false));
        _fx.Dialogs.Logins.Enqueue((password, true));
        _fx.Dialogs.NextConnection = connection;
        Press(w, Key.F, RawInputModifiers.Control);
        await WaitUntilAsync(() => vm.Left.CurrentPath?.Scheme is "sftp" or "ftp", 15000);
        Assert.Equal(connection.Authority, vm.Left.CurrentPath!.Authority);
        Assert.Empty(_fx.Dialogs.Logins);
        Assert.True(session.Secrets.Contains(connection.SecretId)); // saved after the successful login
        if (uri.Scheme == "sftp")
        {
            Assert.StartsWith("SHA256:", vm.Settings.KnownHosts[RemoteSession.HostKey(connection)], StringComparison.Ordinal);
        }

        var remote = VfsPath.Create(connection.Scheme, connection.Authority, folder);
        await vm.Tools.Registry.Resolve(remote).CreateDirectoryAsync(remote, CancellationToken.None);
        await vm.Right.NavigateAsync(remote);
        vm.Activate(vm.Left);
        await vm.Left.NavigateAsync(VfsPath.Local(_fx.L()));
        SetCursor(vm.Left, "b.log");
        Press(w, Key.F5);
        await WaitUntilAsync(() => vm.Right.Items.Any(i => i.FullName == "b.log"), 15000);
        Assert.Equal(2, vm.Right.Items.Single(i => i.FullName == "b.log").Entry!.Size);

        // Disconnect with the path line's button (it shows the connection's name), then connect again: the saved password
        // is used without asking.
        Assert.True(vm.Right.IsRemoteConnection);
        Assert.Equal("test", vm.Right.ConnectionName);
        var disconnect = w.RightPanel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DisconnectButton");
        Assert.True(disconnect.IsEffectivelyVisible);
        Assert.False(w.LeftPanel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DisconnectButton").IsEffectivelyVisible);
        disconnect.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await WaitUntilAsync(() => vm.Right.CurrentPath!.IsLocal);
        Assert.False(vm.Right.IsRemoteConnection);
        Assert.True(vm.Right.CurrentPath!.IsLocal);
        await vm.Right.NavigateAsync(remote);
        Assert.Contains(vm.Right.Items, i => i.FullName == "b.log");
        await vm.Tools.Registry.Resolve(remote).DeleteAsync(remote, recursive: true, CancellationToken.None);
    }

    [AvaloniaFact]
    public async Task Master_password_can_be_set_and_removed()
    {
        var (_, vm, session) = await OpenWithRemoteAsync();
        await session.Secrets.SetAsync("ftp://me@host", "hidden");
        _fx.Dialogs.NextMasterPassword = (null, "ana");
        await vm.ExecuteAsync("nc_MasterPassword");
        Assert.True(session.Secrets.UsesMasterPassword);
        Assert.True(new SecretStore(Path.Combine(_fx.Root, "secrets")).IsLocked);

        _fx.Dialogs.NextMasterPassword = ("yanlış", "");
        await vm.ExecuteAsync("nc_MasterPassword");
        Assert.Contains(_fx.Dialogs.Messages, m => m.Contains("incorrect", StringComparison.Ordinal));
        Assert.True(session.Secrets.UsesMasterPassword);

        _fx.Dialogs.NextMasterPassword = ("ana", "");
        await vm.ExecuteAsync("nc_MasterPassword");
        Assert.False(session.Secrets.UsesMasterPassword);
        Assert.Equal("hidden", await new SecretStore(Path.Combine(_fx.Root, "secrets")).GetAsync("ftp://me@host"));
    }

    [AvaloniaFact]
    public async Task Unreachable_server_shows_an_error_and_stays()
    {
        var (_, vm, _) = await OpenWithRemoteAsync();
        _fx.Dialogs.Logins.Enqueue(("x", false));
        await vm.Left.NavigateToTextAsync("ftp://nobody@127.0.0.1:1/");
        await WaitUntilAsync(() => _fx.Dialogs.Messages.Count > 0, 30000);
        Assert.True(vm.Left.CurrentPath!.IsLocal);
    }

    [AvaloniaFact]
    public async Task Column_sets_show_values_sort_and_switch_automatically()
    {
        File.WriteAllText(_fx.L("three.txt"), "1\n2\n3\n");
        var (w, vm) = await _fx.OpenAsync();
        await vm.Left.RefreshAsync();
        vm.Left.ChooseColumnSet("Source code");
        Assert.Equal("Source code", vm.Left.ActiveColumnSet!.Name);
        var grid = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w.LeftPanel).OfType<Avalonia.Controls.DataGrid>().First();
        Assert.Contains(grid.Columns, c => c.Header as string == "Line count");
        await WaitUntilAsync(() => vm.Left.Items.Single(i => i.FullName == "three.txt").Custom is [ "3", _ ]);
        Assert.Equal(["3", "UTF-8"], vm.Left.Items.Single(i => i.FullName == "three.txt").Custom);

        vm.Left.SetSortField("text.lines");
        await WaitUntilAsync(() => vm.Left.Items.Where(i => !i.IsDirectory && !i.IsParent).Last().FullName == "three.txt");
        Assert.Equal("three.txt", vm.Left.Items.Where(i => !i.IsDirectory && !i.IsParent).Last().FullName);
        Assert.EndsWith("▲", grid.Columns.Single(c => (c.Header as string)?.StartsWith("Line", StringComparison.Ordinal) == true).Header as string, StringComparison.Ordinal);

        vm.Left.ChooseColumnSet(string.Empty);
        Assert.Null(vm.Left.ActiveColumnSet);
        Assert.DoesNotContain(grid.Columns, c => c.Header as string == "Line count");

        // Automatic: a folder called Pictures gets the photo columns.
        Directory.CreateDirectory(_fx.L("Pictures"));
        vm.Left.ChooseColumnSet(null);
        await vm.Left.NavigateAsync(VfsPath.Local(_fx.L("Pictures")));
        Assert.Equal("Photos", vm.Left.ActiveColumnSet?.Name);
        await vm.Left.GoUpAsync();
        Assert.Null(vm.Left.ActiveColumnSet);
    }

    [AvaloniaFact]
    public async Task Associations_open_files_and_open_with_lists_them()
    {
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.Associations.Add(new FileAssociation { Mask = "*.log", Command = "lister", Label = "Günlüğü göster" });
        SetCursor(vm.Left, "b.log");
        Press(w, Key.Enter);
        await WaitUntilAsync(() => _fx.Dialogs.LastLister is not null);
        Assert.Equal("b.log", _fx.Dialogs.LastLister!.Files[_fx.Dialogs.LastLister.Index].Name);

        _fx.Dialogs.QuickListHandler = items => null;
        await vm.ExecuteAsync("nc_OpenWith");
        Assert.Equal(["Günlüğü göster", "System application", "Lister (F3)", "Editor (F4)"], _fx.Dialogs.LastQuickList.Select(i => i.Text));

        await vm.ExecuteAsync("nc_Plugins");
        Assert.Contains(_fx.Dialogs.LastQuickList, i => i.Text == "No plugins loaded");
    }

    [AvaloniaFact]
    public async Task Screenshot_with_column_set()
    {
        File.WriteAllText(_fx.L("Program.cs"), "using System;\nclass P\n{\n}\n");
        File.WriteAllText(_fx.L("notes.md"), "# Notlar\n\nbir\niki\n");
        var (w, vm) = await _fx.OpenAsync();
        await vm.Left.RefreshAsync();
        vm.Left.ChooseColumnSet("Source code");
        await WaitUntilAsync(() => vm.Left.Items.Where(i => i.Entry is { IsDirectory: false }).All(i => i.Custom is [_, _] && i.Custom[0] != "…"));
        await SettleAsync();
        var dir = Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR");
        Assert.NotNull(Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(w));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(w)!.SavePng(Path.Combine(dir, "phase6-columns.png"));
        }
    }
}
