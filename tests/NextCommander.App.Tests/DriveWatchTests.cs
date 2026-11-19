using Avalonia.Headless.XUnit;
using NextCommander.Core.Vfs;
using NextCommander.Platform;

namespace NextCommander.App.Tests;

public class DriveWatchTests
{
    [Fact]
    public async Task Watcher_ReportsAChangeOnceTheReadingIsStable()
    {
        var signature = "C:";
        var calls = 0;
        using var watcher = new DriveWatcher(() => Volatile.Read(ref signature), () => Interlocked.Increment(ref calls), TimeSpan.FromMilliseconds(20));

        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref calls)); // nothing changed

        Volatile.Write(ref signature, "C:|E:");
        var start = Environment.TickCount64;
        while (Volatile.Read(ref calls) == 0 && Environment.TickCount64 - start < 8000)
        {
            await Task.Delay(20);
        }
        await Task.Delay(200);
        Assert.Equal(1, Volatile.Read(ref calls)); // one call for the change, not one per tick

        Volatile.Write(ref signature, "C:");
        start = Environment.TickCount64;
        while (Volatile.Read(ref calls) == 1 && Environment.TickCount64 - start < 8000)
        {
            await Task.Delay(20);
        }
        Assert.Equal(2, Volatile.Read(ref calls)); // removed again
    }

    [Fact]
    public async Task Watcher_KeepsGoingWhenTheSignatureCannotBeRead()
    {
        var fail = true;
        var calls = 0;
        using var watcher = new DriveWatcher(() => Volatile.Read(ref fail) ? throw new IOException("busy") : "C:|E:",
            () => Interlocked.Increment(ref calls), TimeSpan.FromMilliseconds(20));
        await Task.Delay(100);
        Volatile.Write(ref fail, false);
        var start = Environment.TickCount64;
        while (Volatile.Read(ref calls) == 0 && Environment.TickCount64 - start < 8000)
        {
            await Task.Delay(20);
        }
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [AvaloniaFact]
    public async Task DrivesChanged_MovesAPanelOutOfAFolderThatIsGone()
    {
        using var fx = new UiFixture();
        var (_, vm) = await fx.OpenAsync();
        await vm.Left.NavigateAsync(VfsPath.Local(fx.L("sub")));
        var right = vm.Right.CurrentPath;

        Directory.Delete(fx.L("sub"), recursive: true); // as if its drive had been unplugged
        await vm.OnDrivesChangedAsync();

        Assert.Equal(VfsPath.Local(fx.L()), vm.Left.CurrentPath);
        Assert.Equal(right, vm.Right.CurrentPath); // a panel whose folder is still there stays
        Assert.NotEmpty(vm.Left.Drives);
    }

    [AvaloniaFact]
    public async Task PhoneDrive_OpensTheDeviceAndAPanelLeavesItWhenUnplugged()
    {
        using var fx = new UiFixture();
        using var mtp = new NextCommander.Platform.Mtp.MtpFileSystemProvider(new OnePhone());
        var (_, vm) = await fx.OpenAsync(mtp);
        var phone = new DriveItem("Phone", "mtp://Phone/", null, 0, 0, DriveKind.Phone, true);
        vm.Left.SetDrives([.. vm.Left.Drives, phone]);

        await vm.Left.ChangeDriveAsync(phone);
        Assert.Equal(VfsPath.Parse("mtp://Phone/"), vm.Left.CurrentPath);
        Assert.Equal("Internal storage", Assert.Single(vm.Left.Items, i => !i.IsParent).FullName);
        Assert.Same(phone, vm.Left.SelectedDrive);
        // the storage's free space in the footer, as for a drive
        await vm.Left.NavigateAsync(VfsPath.Parse("mtp://Phone/Internal storage"));
        await UiFixture.WaitUntilAsync(() => vm.Left.FreeSpaceText.StartsWith("[Internal storage]", StringComparison.Ordinal));
        Assert.StartsWith("[Internal storage]", vm.Left.FreeSpaceText, StringComparison.Ordinal);

        // the real drive list has no such phone: it was unplugged. A real phone on the machine running the tests was
        // there from the start, so it is no "new" phone the tab would follow: the tab goes home.
        await vm.OnDrivesChangedAsync();
        Assert.Equal(VfsPath.Local(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), vm.Left.CurrentPath);
    }

    /// <summary>A device with one empty storage.</summary>
    private sealed class OnePhone : NextCommander.Platform.Mtp.IMtpDeviceSource, NextCommander.Platform.Mtp.IMtpDevice
    {
        public IReadOnlyList<NextCommander.Platform.Mtp.MtpDeviceInfo> GetDevices() => [new("id", "Phone")];

        public NextCommander.Platform.Mtp.IMtpDevice Open(string deviceId) => this;

        public string RootId => "DEVICE";

        public IReadOnlyList<NextCommander.Platform.Mtp.MtpObject> ListChildren(string parentId) =>
            parentId == RootId ? [new("s1", "Internal storage", true, 0, null)] : [];

        public Stream OpenRead(string objectId) => throw new IOException("empty");

        public void CreateFile(string parentId, string name, long size, DateTime? modified, Stream data) => throw new IOException("read only");

        public NextCommander.Platform.Mtp.MtpObject CreateFolder(string parentId, string name) => throw new IOException("read only");

        public void Delete(string objectId, bool recursive) => throw new IOException("read only");

        public void Rename(string objectId, string newName) => throw new IOException("read only");

        public (long Total, long Free)? StorageSpace(string storageId) => storageId == "s1" ? (64L << 30, 10L << 30) : null;

        public void Dispose()
        {
        }
    }
}
