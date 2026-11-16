using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace NextCommander.Platform.Mtp.LibMtp;

/// <summary>A device on a USB bus with the texts of its descriptors (what the system shows as its name and serial).</summary>
public sealed record UsbDeviceInfo(int Bus, int Address, int VendorId, int ProductId, string? Manufacturer, string? Product, string? Serial);

/// <summary>
/// The USB devices of the system, read without touching them: a cheap <see cref="Signature"/> (changes when a device is
/// plugged in, removed or connects again in another mode) and the names and serials of the devices.
/// </summary>
public interface IUsbDevices
{
    string Signature();

    IReadOnlyList<UsbDeviceInfo> List();
}

/// <summary>Linux: <c>/sys/bus/usb/devices</c> (a folder per device: busnum, devnum, idVendor, product, serial ...).</summary>
public sealed class SysfsUsbDevices(string root = "/sys/bus/usb/devices") : IUsbDevices
{
    /// <summary>The device folders ("1-2", "usb1"; the interfaces "1-2:1.0" are left out).</summary>
    private IEnumerable<string> DeviceFolders()
    {
        try
        {
            return Directory.Exists(root) ? Directory.GetFileSystemEntries(root).Where(p => !Path.GetFileName(p).Contains(':', StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public string Signature() => string.Join('|', DeviceFolders().Select(p => Path.GetFileName(p) + "=" + Text(p, "devnum")));

    public IReadOnlyList<UsbDeviceInfo> List() =>
    [
        .. DeviceFolders()
            .Select(p => (Folder: p, Bus: Number(p, "busnum", NumberStyles.Integer), Address: Number(p, "devnum", NumberStyles.Integer)))
            .Where(d => d.Bus > 0 && d.Address > 0)
            .Select(d => new UsbDeviceInfo(d.Bus, d.Address, Number(d.Folder, "idVendor", NumberStyles.HexNumber), Number(d.Folder, "idProduct", NumberStyles.HexNumber),
                Text(d.Folder, "manufacturer"), Text(d.Folder, "product"), Text(d.Folder, "serial"))),
    ];

    private static string? Text(string folder, string file)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(folder, file)).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int Number(string folder, string file, NumberStyles style) =>
        int.TryParse(Text(folder, file), style, CultureInfo.InvariantCulture, out var value) ? value : 0;
}

/// <summary>
/// macOS: the IOUSBHostDevice objects of the I/O Registry. libusb (under libmtp) numbers a device's bus with the top
/// byte of its "locationID" and its address with "USB Address"; the names are "USB Product Name", "USB Vendor Name".
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe partial class MacUsbDevices : IUsbDevices
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8Encoding = 0x08000100; // kCFStringEncodingUTF8
    private const int SInt64Type = 4; // kCFNumberSInt64Type

    public string Signature() => string.Join('|', Enumerate(entry => RegistryId(entry).ToString(CultureInfo.InvariantCulture)));

    public IReadOnlyList<UsbDeviceInfo> List() => Enumerate(entry =>
    {
        var location = Number(entry, "locationID");
        return new UsbDeviceInfo((int)((location >> 24) & 0xFF), (int)Number(entry, "USB Address"), (int)Number(entry, "idVendor"), (int)Number(entry, "idProduct"),
            Text(entry, "USB Vendor Name"), Text(entry, "USB Product Name"), Text(entry, "USB Serial Number"));
    });

    private static List<T> Enumerate<T>(Func<uint, T> read)
    {
        var result = new List<T>();
        var className = "IOUSBHostDevice\0"u8;
        IntPtr matching;
        fixed (byte* name = className)
        {
            matching = IOServiceMatching(name);
        }
        uint iterator = 0;
        // the matching dictionary is consumed by the call; 0 = kIOMainPortDefault
        if (matching == IntPtr.Zero || IOServiceGetMatchingServices(0, matching, &iterator) != 0)
        {
            return result;
        }
        try
        {
            for (var entry = IOIteratorNext(iterator); entry != 0; entry = IOIteratorNext(iterator))
            {
                try
                {
                    result.Add(read(entry));
                }
                finally
                {
                    _ = IOObjectRelease(entry);
                }
            }
        }
        finally
        {
            _ = IOObjectRelease(iterator);
        }
        return result;
    }

    private static ulong RegistryId(uint entry)
    {
        ulong id = 0;
        return IORegistryEntryGetRegistryEntryID(entry, &id) == 0 ? id : 0;
    }

    private static IntPtr Property(uint entry, string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key + "\0");
        fixed (byte* text = bytes)
        {
            var cfKey = CFStringCreateWithCString(IntPtr.Zero, text, Utf8Encoding);
            try
            {
                return IORegistryEntryCreateCFProperty(entry, cfKey, IntPtr.Zero, 0);
            }
            finally
            {
                CFRelease(cfKey);
            }
        }
    }

    private static long Number(uint entry, string key)
    {
        var value = Property(entry, key);
        if (value == IntPtr.Zero)
        {
            return 0;
        }
        try
        {
            long number = 0;
            return CFGetTypeID(value) == CFNumberGetTypeID() && CFNumberGetValue(value, SInt64Type, &number) != 0 ? number : 0;
        }
        finally
        {
            CFRelease(value);
        }
    }

    private static string? Text(uint entry, string key)
    {
        var value = Property(entry, key);
        if (value == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            if (CFGetTypeID(value) != CFStringGetTypeID())
            {
                return null;
            }
            var buffer = stackalloc byte[512];
            return CFStringGetCString(value, buffer, 512, Utf8Encoding) != 0 && Marshal.PtrToStringUTF8((IntPtr)buffer)?.Trim() is { Length: > 0 } text ? text : null;
        }
        finally
        {
            CFRelease(value);
        }
    }

    [LibraryImport(IOKit)]
    private static partial IntPtr IOServiceMatching(byte* name);

    [LibraryImport(IOKit)]
    private static partial int IOServiceGetMatchingServices(uint mainPort, IntPtr matching, uint* iterator);

    [LibraryImport(IOKit)]
    private static partial uint IOIteratorNext(uint iterator);

    [LibraryImport(IOKit)]
    private static partial int IOObjectRelease(uint item);

    [LibraryImport(IOKit)]
    private static partial int IORegistryEntryGetRegistryEntryID(uint entry, ulong* id);

    [LibraryImport(IOKit)]
    private static partial IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, byte* text, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr item);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFGetTypeID(IntPtr item);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFStringGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFNumberGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial byte CFNumberGetValue(IntPtr number, int type, long* value);

    [LibraryImport(CoreFoundation)]
    private static partial byte CFStringGetCString(IntPtr text, byte* buffer, nint size, uint encoding);
}
