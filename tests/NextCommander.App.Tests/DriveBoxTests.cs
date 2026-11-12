using Avalonia.Headless.XUnit;
using NextCommander.Core.Vfs;
using NextCommander.Platform;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The drive box: drives, then Computer (+), Documents (*) and Network (\).</summary>
public sealed class DriveBoxTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Drive_box_has_computer_documents_and_network_entries()
    {
        var sep = Path.DirectorySeparatorChar.ToString();
        IReadOnlyList<DriveItem> fake = [new DriveItem("A", _fx.L() + sep, "Veri", 1000, 500, DriveKind.Fixed, true), new DriveItem("B", _fx.R() + sep, null, 0, 0, DriveKind.Fixed, true)];
        var provider = new ComputerFileSystemProvider(() => fake);
        var (_, vm) = await _fx.OpenAsync(provider);
        var panel = vm.Left;
        panel.Drives = fake;
        Assert.Equal(["A", "B", "+", "*", "\\"], panel.DriveChoices.Select(d => d.ShortName).ToArray());
        Assert.All(panel.DriveChoices.Skip(2), d => Assert.True(d.IsSpecial));

        // + Computer: every drive as a folder; opening one goes to its root
        await panel.ChangeDriveAsync(panel.DriveChoices.Single(d => d.Kind == DriveKind.Computer));
        await SettleAsync();
        Assert.Equal(ComputerFileSystemProvider.Root, panel.CurrentPath);
        Assert.Equal(DriveKind.Computer, panel.SelectedDrive!.Kind);
        var drives = panel.Items.Where(i => !i.IsParent).ToList();
        Assert.Equal(2, drives.Count);
        Assert.Contains(drives, d => d.FullName.Contains("Veri", StringComparison.Ordinal));
        // read only: a drive cannot be deleted from here
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.DeleteAsync(drives[0].Entry!.Path, true, CancellationToken.None));
        await panel.NavigateAsync(drives.First(d => d.FullName.Contains("Veri", StringComparison.Ordinal)).Entry!.Path);
        Assert.Equal(VfsPath.Local(_fx.L()), panel.CurrentPath);

        // * Documents
        var documents = panel.DriveChoices.Single(d => d.Kind == DriveKind.Documents);
        await panel.ChangeDriveAsync(documents);
        Assert.Equal(VfsPath.Local(documents.RootPath), panel.CurrentPath);

    }

    [AvaloniaFact]
    public async Task Network_entry_shows_the_machines_in_the_file_list()
    {
        var nas = new NetworkHost(System.Net.IPAddress.Parse("192.168.1.40")) { Name = "nas" };
        nas.Services.UnionWith([NetworkService.Sftp, NetworkService.Ftp, NetworkService.Smb]);
        nas.Ports[NetworkService.Sftp] = 2222;
        var printer = new NetworkHost(System.Net.IPAddress.Parse("192.168.1.50"));
        printer.Services.Add(NetworkService.Http);
        var scans = 0;
        var network = new NetworkFileSystemProvider(null, () => false, _ =>
        {
            scans++;
            return Task.FromResult<IReadOnlyList<NetworkHost>>([nas, printer]);
        });
        var (_, vm) = await _fx.OpenAsync(network);
        var panel = vm.Left;
        var start = panel.CurrentPath;

        // \ Network: the machines as folders
        await panel.ChangeDriveAsync(panel.DriveChoices.Single(d => d.Kind == DriveKind.NetworkPlaces));
        Assert.Equal(NetworkFileSystemProvider.Root, panel.CurrentPath);
        Assert.Equal(DriveKind.NetworkPlaces, panel.SelectedDrive!.Kind);
        Assert.Equal(["192.168.1.50", "nas"], panel.Items.Where(i => !i.IsParent).Select(i => i.FullName).Order().ToArray());

        // a machine: its services
        await panel.NavigateAsync(panel.Items.Single(i => i.FullName == "nas").Entry!.Path);
        // (no share list from the system here, as on Linux / macOS: "SMB" opens the built-in SMB client)
        Assert.Equal(["FTP", "SFTP", "SMB"], panel.Items.Where(i => !i.IsParent).Select(i => i.FullName).Order().ToArray());
        Assert.Equal(new NetworkTarget(NetworkService.Smb, "nas", null, 0, false),
            NetworkFileSystemProvider.TargetOf(panel.Items.Single(i => i.FullName == "SMB").Entry!.Path));
        await panel.RefreshAsync();
        Assert.Equal(1, scans); // the scan is reused

        // opening SFTP asks the user name and connects (on the port the machine announced)
        var sftp = panel.Items.Single(i => i.FullName == "SFTP").Entry!.Path;
        Assert.Equal(new NetworkTarget(NetworkService.Sftp, "nas", null, 2222, false), NetworkFileSystemProvider.TargetOf(sftp));
        _fx.Dialogs.NextPrompt = null; // cancelled: the panel stays
        Assert.False(await panel.NavigateAsync(sftp));
        Assert.Equal("nas", panel.CurrentPath!.Name);

        // read only: nothing can be deleted here
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => network.DeleteAsync(sftp, true, CancellationToken.None));

        // a share: \\host\share
        Assert.Equal(@"\\nas\Ortak", NetworkFileSystemProvider.TargetOf(VfsPath.Parse("network:///nas/smb%3AOrtak"))!.SharePath);
        await panel.NavigateAsync(start!);
    }
}
