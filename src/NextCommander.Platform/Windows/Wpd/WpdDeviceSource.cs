using System.Runtime.Versioning;
using NextCommander.Platform.Mtp;

namespace NextCommander.Platform.Windows.Wpd;

/// <summary>
/// The MTP / PTP devices of the system (phones, cameras, players). USB disks and memory cards also have a WPD side
/// (enumerated by "WPDBusEnum"); they are left out, they already are drives with a letter. The list is read again only
/// when the system's list of portable device interfaces changes.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WpdDeviceSource : IMtpDeviceSource
{
    private readonly Lock _lock = new();
    private string? _signature;
    private IReadOnlyList<MtpDeviceInfo> _devices = [];

    /// <summary>The device interfaces present (cheap): changes when a device is plugged in or removed.</summary>
    public static string Signature() => WpdNative.DeviceInterfaceList();

    /// <summary>How long a device list that misses devices is read again (a phone's interface comes before the phone can be listed).</summary>
    private static readonly TimeSpan RetryFor = TimeSpan.FromSeconds(30);

    private string? _pending;
    private DateTime _pendingSince;

    public IReadOnlyList<MtpDeviceInfo> GetDevices()
    {
        var now = Signature();
        lock (_lock)
        {
            if (now == _signature)
            {
                return _devices;
            }
        }
        IReadOnlyList<MtpDeviceInfo> devices;
        var complete = true;
        try
        {
            devices = now.Length == 0 ? [] : OnWorker(Enumerate);
            // every portable device interface (but the WPD side of disks) should be a device in the list
            var expected = now.Split('|', StringSplitOptions.RemoveEmptyEntries).Count(i => !i.Contains("wpdbusenum", StringComparison.OrdinalIgnoreCase));
            complete = devices.Count >= expected;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or AggregateException)
        {
            System.Diagnostics.Trace.TraceWarning("Portable devices could not be listed: " + ex.Message);
            devices = [];
            complete = false;
        }
        lock (_lock)
        {
            if (_pending != now)
            {
                _pending = now;
                _pendingSince = DateTime.UtcNow;
            }
            // a list that misses a device is not kept: it is read again on the next call (for a while)
            _signature = complete || DateTime.UtcNow - _pendingSince > RetryFor ? now : null;
            _devices = devices;
        }
        return devices;
    }

    public IMtpDevice Open(string deviceId) => OnWorker(() => WpdDevice.Open(deviceId));

    /// <summary>WPD calls are made in the multi-threaded apartment: from the UI thread they go to a worker thread.</summary>
    private static T OnWorker<T>(Func<T> action) =>
        Thread.CurrentThread.GetApartmentState() == ApartmentState.STA ? Task.Run(action).GetAwaiter().GetResult() : action();

    private static IReadOnlyList<MtpDeviceInfo> Enumerate()
    {
        var manager = WpdNative.Create<IPortableDeviceManager>(WpdNative.ClsidPortableDeviceManager);
        manager.RefreshDeviceList();
        uint count = 0;
        WpdNative.Check(manager.GetDevices(null, &count));
        if (count == 0)
        {
            return [];
        }
        var ids = new IntPtr[count];
        fixed (IntPtr* p = ids)
        {
            WpdNative.Check(manager.GetDevices(p, &count));
        }
        var result = new List<MtpDeviceInfo>();
        for (var i = 0; i < count; i++)
        {
            var id = WpdNative.TakeString(ids[i]);
            if (id.Contains("wpdbusenum", StringComparison.OrdinalIgnoreCase))
            {
                continue; // a disk with a drive letter
            }
            var name = Text(manager, id, 0) ?? Text(manager, id, 1) ?? "Portable device";
            result.Add(new MtpDeviceInfo(id, name));
        }
        return MtpDeviceNames.MakeUnique(result);
    }

    /// <summary>The friendly name (0) or description (1) of a device; null when it has none.</summary>
    private static string? Text(IPortableDeviceManager manager, string deviceId, int which)
    {
        fixed (char* id = deviceId)
        {
            uint length = 0;
            var hr = which == 0 ? manager.GetDeviceFriendlyName(id, null, &length) : manager.GetDeviceDescription(id, null, &length);
            if (hr < 0 || length == 0)
            {
                return null;
            }
            var buffer = new char[length];
            fixed (char* b = buffer)
            {
                hr = which == 0 ? manager.GetDeviceFriendlyName(id, b, &length) : manager.GetDeviceDescription(id, b, &length);
            }
            if (hr < 0)
            {
                return null;
            }
            var text = new string(buffer).TrimEnd('\0').Trim();
            return text.Length == 0 ? null : text;
        }
    }
}
