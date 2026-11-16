using NextCommander.Core.Vfs;
using NextCommander.Platform.Mtp;
using NextCommander.Platform.Mtp.LibMtp;

namespace NextCommander.Core.Tests;

/// <summary>Phones and cameras through libmtp (Linux, macOS): the device list, names, ids; libmtp and USB are fakes.</summary>
public sealed class LibMtpTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "fm-libmtp-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_tmp))
        {
            Directory.Delete(_tmp, recursive: true);
        }
    }

    private static readonly RawMtpDevice Pixel = new(1, 7, 0x18d1, 0x4ee1, "Google Inc", "Nexus/Pixel (MTP)");
    private static readonly RawMtpDevice Galaxy = new(2, 3, 0x04e8, 0x6860, "Samsung", "Galaxy models (MTP)");

    [Fact]
    public void Devices_are_named_after_their_USB_descriptors_then_libmtps_list()
    {
        Assert.Equal("Google Pixel 8", LibMtpDeviceSource.NameOf(Pixel, new UsbDeviceInfo(1, 7, 0x18d1, 0x4ee1, "Google", "Pixel 8", "3A1")));
        Assert.Equal("SAMSUNG Android", LibMtpDeviceSource.NameOf(Galaxy, new UsbDeviceInfo(2, 3, 0x04e8, 0x6860, "SAMSUNG", "SAMSUNG_Android", "R58")));
        Assert.Equal("Samsung Galaxy models (MTP)", LibMtpDeviceSource.NameOf(Galaxy, null));
        Assert.Equal("Portable device 1234:abcd", LibMtpDeviceSource.NameOf(new RawMtpDevice(1, 2, 0x1234, 0xabcd, null, null), null));
    }

    [Fact]
    public void LibMtp_searches_only_when_the_USB_devices_change()
    {
        var libMtp = new FakeLibMtp { Devices = [Pixel] };
        var usb = new FakeUsb { Devices = [new UsbDeviceInfo(1, 7, 0x18d1, 0x4ee1, "Google", "Pixel 8", "3A1")] };
        var source = new LibMtpDeviceSource(libMtp, usb);

        var devices = source.GetDevices();
        Assert.Equal("Google Pixel 8", Assert.Single(devices).Name);
        Assert.Equal("3A1", source.SerialOf(devices[0].Id));
        source.GetDevices();
        Assert.Equal(1, libMtp.Detects);

        usb.Signature = "another";
        libMtp.Devices = [Pixel, Galaxy];
        Assert.Equal(2, source.GetDevices().Count);
        Assert.Equal(2, libMtp.Detects);
    }

    [Fact]
    public void Two_devices_of_one_name_get_unique_names_and_open_by_their_id()
    {
        var second = Pixel with { Bus = 3, Address = 1 };
        var libMtp = new FakeLibMtp { Devices = [Pixel, second] };
        var source = new LibMtpDeviceSource(libMtp, new FakeUsb());

        var devices = source.GetDevices();
        Assert.Equal(["Google Inc Nexus Pixel (MTP)", "Google Inc Nexus Pixel (MTP) (2)"], devices.Select(d => d.Name)); // no '/' in a path's host
        source.Open(devices[1].Id).Dispose();
        Assert.Equal(second, Assert.Single(libMtp.Opened));
        Assert.Throws<DirectoryNotFoundException>(() => source.Open("usb:9:9:0000:0000"));
    }

    [Fact]
    public void A_device_another_program_has_is_left_out_also_when_it_takes_it_later()
    {
        var gvfs = new List<string>();
        var usb = new FakeUsb { Devices = [new UsbDeviceInfo(1, 7, 0x18d1, 0x4ee1, "Google", "Pixel 8", "3A1")] };
        var source = new LibMtpDeviceSource(new FakeLibMtp { Devices = [Pixel] }, usb, serial => serial is not null && gvfs.Any(f => f.Contains(serial, StringComparison.Ordinal)));

        Assert.Single(source.GetDevices());
        gvfs.Add("mtp:host=Google_Pixel_8_3A1"); // the USB devices did not change
        Assert.Empty(source.GetDevices());
    }

    [Fact]
    public void Without_libmtp_or_when_its_search_fails_there_are_no_devices()
    {
        Assert.Empty(new LibMtpDeviceSource(new FakeLibMtp { IsAvailable = false, Devices = [Pixel] }, new FakeUsb()).GetDevices());

        var failing = new FakeLibMtp { Devices = [Pixel], Fail = true };
        var source = new LibMtpDeviceSource(failing, new FakeUsb());
        Assert.Empty(source.GetDevices());
        failing.Fail = false; // the failed list is not kept: searched again
        Assert.Single(source.GetDevices());
    }

    [Fact]
    public async Task The_panel_reaches_a_libmtp_device_by_its_name()
    {
        var libMtp = new FakeLibMtp { Devices = [Pixel] };
        using var provider = new MtpFileSystemProvider(new LibMtpDeviceSource(libMtp, new FakeUsb { Devices = [new UsbDeviceInfo(1, 7, 0, 0, "Google", "Pixel 8", null)] }));
        libMtp.Device.AddFile("/Internal storage/DCIM/a.jpg", "jpeg");

        var root = await provider.ListAsync(VfsPath.Parse("mtp://Google Pixel 8/"), CancellationToken.None);
        Assert.Equal("Internal storage", Assert.Single(root).Name);
        var dcim = await provider.ListAsync(VfsPath.Parse("mtp://Google Pixel 8/Internal storage/DCIM"), CancellationToken.None);
        Assert.Equal("a.jpg", Assert.Single(dcim).Name);
    }

    [Fact]
    public void Object_ids_carry_the_storage_and_the_handle()
    {
        Assert.Equal((0x10001u, 0xFFFFFFFFu), LibMtpDevice.Parse(LibMtpDevice.StorageKey(0x10001)));
        Assert.Equal((0x10001u, 0x2au), LibMtpDevice.Parse(LibMtpDevice.ObjectKey(0x10001, 42)));
        Assert.Throws<FileNotFoundException>(() => LibMtpDevice.Parse("device"));
        Assert.Throws<FileNotFoundException>(() => LibMtpDevice.Parse("o:zz:1"));
    }

    [Fact]
    public void The_library_is_looked_for_in_the_bundle_then_the_system()
    {
        var mac = LibMtpNative.Candidates("/x/libmtp.dylib", "/Applications/NextCommander.app/Contents/MacOS/", macOS: true).ToList();
        Assert.Equal("/x/libmtp.dylib", mac[0]);
        Assert.Equal(Path.Combine("/Applications/NextCommander.app/Contents/MacOS/", "libmtp.9.dylib"), mac[1]);
        Assert.Contains("/opt/homebrew/lib/libmtp.9.dylib", mac);
        Assert.Equal(["libmtp.so.9", "libmtp.so"], LibMtpNative.Candidates(null, "/opt/nextcommander/", macOS: false));
    }

    [Fact]
    public void Sysfs_gives_the_names_serials_and_a_signature_of_the_USB_devices()
    {
        Device("usb1", bus: 1, address: 1, null, "xHCI Host Controller", null);
        Device("1-2", bus: 1, address: 7, "Google", "Pixel 8", "3A1", vendor: "18d1", product: "4ee1");
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Path.Combine(_tmp, "1-2:1.0")); // an interface, not a device (no ':' in Windows names)
        }
        var usb = new SysfsUsbDevices(_tmp);

        var pixel = Assert.Single(usb.List(), d => d.Address == 7);
        Assert.Equal(new UsbDeviceInfo(1, 7, 0x18d1, 0x4ee1, "Google", "Pixel 8", "3A1"), pixel);
        Assert.Equal(2, usb.List().Count);
        var before = usb.Signature();
        File.WriteAllText(Path.Combine(_tmp, "1-2", "devnum"), "8\n"); // connected again (another USB mode)
        Assert.NotEqual(before, usb.Signature());
        Assert.Empty(new SysfsUsbDevices(Path.Combine(_tmp, "missing")).List());
    }

    private void Device(string name, int bus, int address, string? manufacturer, string? productName, string? serial, string vendor = "1d6b", string product = "0002")
    {
        var folder = Path.Combine(_tmp, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "busnum"), bus + "\n");
        File.WriteAllText(Path.Combine(folder, "devnum"), address + "\n");
        File.WriteAllText(Path.Combine(folder, "idVendor"), vendor + "\n");
        File.WriteAllText(Path.Combine(folder, "idProduct"), product + "\n");
        foreach (var (file, text) in new[] { ("manufacturer", manufacturer), ("product", productName), ("serial", serial) })
        {
            if (text is not null)
            {
                File.WriteAllText(Path.Combine(folder, file), text + "\n");
            }
        }
    }

    private sealed class FakeLibMtp : ILibMtp
    {
        public bool IsAvailable { get; set; } = true;

        public IReadOnlyList<RawMtpDevice> Devices { get; set; } = [];

        public bool Fail { get; set; }

        public int Detects { get; private set; }

        public List<RawMtpDevice> Opened { get; } = [];

        public MtpProviderTests.FakeMtpDevice Device { get; } = new();

        public IReadOnlyList<RawMtpDevice> Detect()
        {
            Detects++;
            return Fail ? throw new IOException("libmtp error 3") : Devices;
        }

        public IMtpDevice Open(RawMtpDevice device)
        {
            Opened.Add(device);
            return Device;
        }
    }

    private sealed class FakeUsb : IUsbDevices
    {
        public string Signature { get; set; } = "usb";

        public IReadOnlyList<UsbDeviceInfo> Devices { get; set; } = [];

        string IUsbDevices.Signature() => Signature;

        public IReadOnlyList<UsbDeviceInfo> List() => Devices;
    }
}
