using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using NextCommander.Core.Vfs;
using NextCommander.Platform;
using static NextCommander.App.Tests.UiFixture;

namespace NextCommander.App.Tests;

/// <summary>The drive box: drives, then Computer (+), Network (\) and the trash (×).</summary>
public sealed class DriveBoxTests : IDisposable
{
    private readonly UiFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [AvaloniaFact]
    public async Task Drive_box_has_computer_network_and_trash_entries()
    {
        var sep = Path.DirectorySeparatorChar.ToString();
        IReadOnlyList<DriveItem> fake =
        [
            new DriveItem("A", _fx.L() + sep, "Veri", 1000, 500, DriveKind.Fixed, true),
            new DriveItem("U", _fx.Root + sep, "STICK", 0, 0, DriveKind.Removable, true),
            new DriveItem("Galaxy", "mtp://Galaxy/", null, 0, 0, DriveKind.Phone, true),
            new DriveItem("B", _fx.R() + sep, null, 0, 0, DriveKind.Fixed, true),
        ];
        var provider = new ComputerFileSystemProvider(() => fake);
        var (_, vm) = await _fx.OpenAsync(provider);
        var panel = vm.Left;
        panel.Drives = fake;
        // no Documents entry: it is a drive bar shortcut
        Assert.Equal(["A", "U", "Galaxy", "B", "+", "\\", "×"], panel.DriveChoices.Select(d => d.ShortName).ToArray());
        Assert.Equal([DriveKind.NetworkPlaces, DriveKind.Trash], panel.DriveBarItems.Skip(4).Select(d => d.Kind));
        Assert.All(panel.DriveChoices.Skip(4), d => Assert.True(d.IsSpecial));

        // the trash button on the drive bar follows Configuration > Layout
        vm.Settings.DriveBarTrash = false;
        Assert.Equal([DriveKind.NetworkPlaces], panel.DriveBarItems.Skip(4).Select(d => d.Kind));
        vm.Settings.DriveBarTrash = true;

        // + Computer: the drives in letter order, then external disks, then phones, then the trash, whatever the name sort
        await panel.ChangeDriveAsync(panel.DriveChoices.Single(d => d.Kind == DriveKind.Computer));
        await SettleAsync();
        Assert.Equal(ComputerFileSystemProvider.Root, panel.CurrentPath);
        Assert.Equal(DriveKind.Computer, panel.SelectedDrive!.Kind);
        var drives = panel.Items.Where(i => !i.IsParent).ToList();
        // as Explorer names them: the label, or what the drive is
        Assert.Equal(["Veri (A)", "Local Disk (B)", "STICK (U)", "Phone (Galaxy)", OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash"],
            drives.Select(d => d.FullName));
        // read only: a drive cannot be deleted from here
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.DeleteAsync(drives[0].Entry!.Path, true, CancellationToken.None));
        // right click: the system's menu is made for the shell name of the entry (Recycle Bin, a phone, a drive root)
        Assert.Equal("::{645FF040-5081-101B-9F08-00AA002F954E}", NextCommander.App.Views.FilePanelView.ShellNameOfOverviewEntry(panel, drives[^1].Entry!.Path));
        Assert.Equal(_fx.L() + sep, NextCommander.App.Views.FilePanelView.ShellNameOfOverviewEntry(panel, drives[0].Entry!.Path));
        Assert.Null(NextCommander.App.Views.FilePanelView.ShellNameOfOverviewEntry(panel, drives[3].Entry!.Path)); // a phone without a device id

        await panel.NavigateAsync(drives[0].Entry!.Path);
        Assert.Equal(VfsPath.Local(_fx.L()), panel.CurrentPath);
    }

    [AvaloniaFact]
    public async Task Drive_bar_marks_the_panels_drive()
    {
        // the panels' folders are the "roots" of the two drives
        IReadOnlyList<DriveItem> fake = [new DriveItem("A", _fx.L(), "Veri", 0, 0, DriveKind.Fixed, true), new DriveItem("B", _fx.R(), null, 0, 0, DriveKind.Fixed, true)];
        var (w, vm) = await _fx.OpenAsync();
        vm.Settings.ShowDriveButtons = true;
        vm.Settings.TwoDriveBars = true;
        vm.ApplySettings();
        vm.Left.SetDrives(fake);
        vm.Right.SetDrives(fake); // left panel on A, right panel on B
        await SettleAsync();

        List<string> Marked(string barName) => w.GetVisualDescendants().OfType<ItemsControl>().Where(c => c.Name == barName && c.IsEffectivelyVisible)
            .Select(bar => string.Join(",", bar.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("current")).Select(b => ((DriveItem)b.Tag!).Name)))
            .ToList();

        // a bar above each panel: that panel's drive
        Assert.Equal(["A", "B"], Marked("DriveButtons"));
        var current = w.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("current") && b.IsEffectivelyVisible);
        Assert.True(current.FontWeight >= Avalonia.Media.FontWeight.SemiBold); // the template's chip: semi-bold
        if (Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            w.CaptureRenderedFrame()!.SavePng(Path.Combine(dir, "drivebar-current.png"));
        }

        // one shared bar: the active panel's drive
        vm.Settings.TwoDriveBars = false;
        vm.ApplySettings(); // reads the real drives again
        vm.Left.SetDrives(fake);
        vm.Right.SetDrives(fake);
        await SettleAsync();
        Assert.Equal(["A"], Marked("SharedDriveBar"));
        vm.ActiveSide = vm.RightSide;
        await SettleAsync();
        Assert.Equal(["B"], Marked("SharedDriveBar"));

        // the panel goes to another drive: the mark follows
        await vm.Right.NavigateAsync(VfsPath.Local(_fx.L()));
        await SettleAsync();
        Assert.Equal(["A"], Marked("SharedDriveBar"));
    }
    [AvaloniaFact]
    public void Drive_icons_come_from_the_system()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var system = Path.GetPathRoot(Environment.SystemDirectory)!;
        var dir = Environment.GetEnvironmentVariable("FM_SCREENSHOT_DIR");
        foreach (var drive in new[] { new DriveItem(system.TrimEnd('\\'), system, null, 0, 0, DriveKind.Fixed, true), DriveItem.ComputerEntry, DriveItem.NetworkEntry, DriveItem.TrashEntry })
        {
            var icon = NextCommander.App.Services.Icons.DriveIconConverter.IconFor(drive);
            Assert.True(icon is not null, $"no system icon for {drive.Kind}");
            if (dir is { Length: > 0 })
            {
                icon.SavePng(Path.Combine(dir, $"drive-icon-{drive.Kind}.png"));
            }
        }
    }
    [AvaloniaFact]
    public async Task Trash_entry_lists_deleted_items_and_restores_them()
    {
        // a freedesktop trash in the test folder: one deleted file whose place was the left folder
        var trash = Path.Combine(_fx.Root, "Trash");
        Directory.CreateDirectory(Path.Combine(trash, "files"));
        Directory.CreateDirectory(Path.Combine(trash, "info"));
        File.WriteAllText(Path.Combine(trash, "files", "old.txt"), "old");
        var original = Path.Combine(_fx.L(), "old.txt");
        File.WriteAllText(Path.Combine(trash, "info", "old.txt.trashinfo"),
            "[Trash Info]\nPath=" + string.Join('/', original.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString)) + "\nDeletionDate=2024-05-06T07:08:09\n");
        var provider = new NextCommander.Platform.Trash.TrashFileSystemProvider(new NextCommander.Platform.Trash.FreedesktopTrash(() => [(trash, null)]));
        var (_, vm) = await _fx.OpenAsync(provider);
        var panel = vm.Left;

        await panel.ChangeDriveAsync(DriveItem.TrashEntry);
        Assert.Equal(NextCommander.Platform.Trash.TrashFileSystemProvider.Root, panel.CurrentPath);
        Assert.Same(DriveItem.TrashEntry, panel.SelectedDrive);
        var item = Assert.Single(panel.Items, i => !i.IsParent);
        Assert.Equal("old.txt", item.FullName);
        // the trash's own view mode: the folder the file was deleted from
        Assert.Same(NextCommander.Platform.Trash.TrashColumns.ViewMode, panel.ActiveColumnSet);
        await WaitUntilAsync(() => item.Custom.FirstOrDefault() is { Length: > 1 }); // "…" while it is read
        Assert.Equal(_fx.L(), item.Custom[0]);

        panel.CursorItem = item;
        await vm.ExecuteAsync("nc_RestoreFromTrash");
        Assert.Equal("old", File.ReadAllText(original));
        Assert.DoesNotContain(panel.Items, i => !i.IsParent);
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
