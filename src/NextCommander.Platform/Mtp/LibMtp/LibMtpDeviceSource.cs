using System.Globalization;

namespace NextCommander.Platform.Mtp.LibMtp;

/// <summary>What the device source needs of libmtp (the real one: <see cref="NativeLibMtp"/>; tests use a fake).</summary>
public interface ILibMtp
{
    /// <summary>False when libmtp is not installed: no devices.</summary>
    bool IsAvailable { get; }

    IReadOnlyList<RawMtpDevice> Detect();

    IMtpDevice Open(RawMtpDevice device);
}

/// <summary>The libmtp of the system (<see cref="LibMtpNative"/>).</summary>
public sealed class NativeLibMtp : ILibMtp
{
    public static NativeLibMtp Instance { get; } = new();

    private readonly Lock _detect = new();

    public bool IsAvailable => LibMtpNative.TryLoad();

    public IReadOnlyList<RawMtpDevice> Detect()
    {
        lock (_detect)
        {
            return LibMtpNative.Detect();
        }
    }

    public IMtpDevice Open(RawMtpDevice device)
    {
        lock (_detect)
        {
            return LibMtpDevice.Open(device);
        }
    }
}

/// <summary>
/// The phones, cameras and players of the system through libmtp (Linux, macOS). libmtp's search probes the USB
/// devices it does not know, so it runs only when the USB devices change (<see cref="IUsbDevices.Signature"/>), not at
/// every look at the drive list. A device's name is that of its USB descriptors ("Google Pixel 8"), else libmtp's
/// list; its id is its USB address. <paramref name="isTaken"/> leaves out devices another program shows already
/// (Linux: the gvfs folder of the phone, by the USB serial in its name): libmtp could not open them.
/// </summary>
public sealed class LibMtpDeviceSource(ILibMtp libMtp, IUsbDevices usb, Func<string?, bool>? isTaken = null) : IMtpDeviceSource
{
    private readonly Lock _lock = new();
    private string? _signature;
    private IReadOnlyList<MtpDeviceInfo> _devices = [];
    private Dictionary<string, (RawMtpDevice Raw, string? Serial)> _found = new(StringComparer.Ordinal);

    public IReadOnlyList<MtpDeviceInfo> GetDevices()
    {
        if (!libMtp.IsAvailable)
        {
            return [];
        }
        var signature = usb.Signature();
        lock (_lock)
        {
            if (signature == _signature)
            {
                return Usable(_devices, _found);
            }
        }
        try
        {
            var descriptors = usb.List();
            var found = new Dictionary<string, (RawMtpDevice, string?)>(StringComparer.Ordinal);
            var devices = new List<MtpDeviceInfo>();
            foreach (var raw in libMtp.Detect())
            {
                var descriptor = descriptors.FirstOrDefault(d => d.Bus == raw.Bus && d.Address == raw.Address);
                var id = IdOf(raw);
                found[id] = (raw, descriptor?.Serial);
                devices.Add(new MtpDeviceInfo(id, NameOf(raw, descriptor)));
            }
            lock (_lock)
            {
                _signature = signature;
                _devices = devices;
                _found = found;
            }
            return Usable(devices, found);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // not kept: read again the next time
            System.Diagnostics.Trace.TraceWarning("MTP devices could not be listed: " + ex.Message);
            return [];
        }
    }

    /// <summary>The devices no other program shows (checked at every call: gvfs may mount a phone later), with unique names.</summary>
    private IReadOnlyList<MtpDeviceInfo> Usable(IReadOnlyList<MtpDeviceInfo> devices, Dictionary<string, (RawMtpDevice Raw, string? Serial)> found) =>
        MtpDeviceNames.MakeUnique(isTaken is null ? devices : devices.Where(d => !isTaken(found[d.Id].Serial)));

    /// <summary>The USB serial of a device of the last list (to leave out the same phone over ADB).</summary>
    public string? SerialOf(string deviceId)
    {
        lock (_lock)
        {
            return _found.TryGetValue(deviceId, out var device) ? device.Serial : null;
        }
    }

    public IMtpDevice Open(string deviceId)
    {
        (RawMtpDevice Raw, string? Serial) device;
        lock (_lock)
        {
            if (!_found.TryGetValue(deviceId, out device))
            {
                throw new DirectoryNotFoundException("No portable device: " + deviceId);
            }
        }
        return libMtp.Open(device.Raw);
    }

    internal static string IdOf(RawMtpDevice raw) =>
        string.Create(CultureInfo.InvariantCulture, $"usb:{raw.Bus}:{raw.Address}:{raw.VendorId:x4}:{raw.ProductId:x4}");

    /// <summary>
    /// "Google" + "Pixel 8" → "Google Pixel 8"; "SAMSUNG" + "SAMSUNG_Android" → "SAMSUNG Android"; without descriptors
    /// libmtp's entry ("Samsung" + "Galaxy models (MTP)"), else "Portable device 04e8:6860".
    /// </summary>
    internal static string NameOf(RawMtpDevice raw, UsbDeviceInfo? descriptor)
    {
        var product = Clean(descriptor?.Product);
        var maker = Clean(descriptor?.Manufacturer);
        if (product is not null)
        {
            return maker is null || product.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? product : maker + " " + product;
        }
        var tableProduct = Clean(raw.Product);
        var tableVendor = Clean(raw.Vendor);
        if (tableProduct is not null)
        {
            return tableVendor is null || tableProduct.StartsWith(tableVendor, StringComparison.OrdinalIgnoreCase) ? tableProduct : tableVendor + " " + tableProduct;
        }
        return string.Create(CultureInfo.InvariantCulture, $"Portable device {raw.VendorId:x4}:{raw.ProductId:x4}");
    }

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var cleaned = string.Join(' ', text.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length == 0 ? null : cleaned;
    }
}
