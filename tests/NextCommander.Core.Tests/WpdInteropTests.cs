using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NextCommander.Platform.Windows.Wpd;

namespace NextCommander.Core.Tests;

/// <summary>
/// The WPD helper objects of Windows, called through the source-generated COM interfaces: a method declared in the
/// wrong vtable place would call another method (or crash). No device is needed.
/// </summary>
public sealed class WpdInteropTests
{
    [Fact]
    public async Task ValuesKeysAndCollectionsRoundTrip()
    {
        if (OperatingSystem.IsWindows())
        {
            await Task.Run(RoundTrip); // WPD objects are used from worker threads (multi-threaded apartment)
        }
    }

    [Fact]
    public async Task DeviceManagerLists()
    {
        if (OperatingSystem.IsWindows())
        {
            // with no phone the list is empty, with one it has its name
            var devices = await Task.Run(ListDevices);
            Assert.All(devices, d => Assert.False(string.IsNullOrWhiteSpace(d.Name)));
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<NextCommander.Platform.Mtp.MtpDeviceInfo> ListDevices() => new WpdDeviceSource().GetDevices();

    [SupportedOSPlatform("windows")]
    private static unsafe void RoundTrip()
    {
        var values = WpdNative.Create<IPortableDeviceValues>(WpdNative.ClsidPortableDeviceValues);
        var name = WpdNative.ObjectOriginalFileName;
        fixed (char* text = "photo.jpg")
        {
            Assert.Equal(0, values.SetStringValue(&name, text));
        }
        IntPtr back;
        Assert.Equal(0, values.GetStringValue(&name, &back));
        Assert.Equal("photo.jpg", WpdNative.TakeString(back));

        var size = WpdNative.ObjectSize;
        ulong bytes = 0;
        Assert.Equal(0, values.SetUnsignedLargeIntegerValue(&size, 5_000_000_000));
        Assert.Equal(0, values.GetUnsignedLargeIntegerValue(&size, &bytes));
        Assert.Equal(5_000_000_000UL, bytes);

        var version = WpdNative.ClientMajorVersion;
        uint number = 0;
        Assert.Equal(0, values.SetUnsignedIntegerValue(&version, 7));
        Assert.Equal(0, values.GetUnsignedIntegerValue(&version, &number));
        Assert.Equal(7U, number);

        var type = WpdNative.ObjectContentType;
        var folder = WpdNative.ContentTypeFolder;
        Guid guid;
        Assert.Equal(0, values.SetGuidValue(&type, &folder));
        Assert.Equal(0, values.GetGuidValue(&type, &guid));
        Assert.Equal(WpdNative.ContentTypeFolder, guid);

        var dateKey = WpdNative.ObjectDateModified;
        var when = new DateTime(2024, 5, 6, 7, 8, 9);
        var date = new PropVariant { Vt = PropVariant.VT_DATE, Date = when.ToOADate() };
        Assert.Equal(0, values.SetValue(&dateKey, &date));
        PropVariant read = default;
        Assert.Equal(0, values.GetValue(&dateKey, &read));
        Assert.Equal(PropVariant.VT_DATE, read.Vt);
        Assert.Equal(when, DateTime.FromOADate(read.Date));
        _ = WpdNative.PropVariantClear(&read);

        var errorKey = WpdNative.ObjectName;
        var error = 0;
        Assert.Equal(0, values.SetErrorValue(&errorKey, unchecked((int)0x80070005)));
        Assert.Equal(0, values.GetErrorValue(&errorKey, &error));
        Assert.Equal(unchecked((int)0x80070005), error);

        uint count = 0;
        Assert.Equal(0, values.GetCount(&count));
        Assert.Equal(6U, count);
        var missing = WpdNative.ObjectParentId;
        Assert.True(values.GetStringValue(&missing, &back) < 0); // not set: an error code, not an exception

        var keys = WpdNative.Create<IPortableDeviceKeyCollection>(WpdNative.ClsidPortableDeviceKeyCollection);
        Assert.Equal(0, keys.Add(&name));
        Assert.Equal(0, keys.Add(&size));
        Assert.Equal(0, keys.GetCount(&count));
        Assert.Equal(2U, count);
        PropertyKey second;
        Assert.Equal(0, keys.GetAt(1, &second));
        Assert.Equal(WpdNative.ObjectSize.FmtId, second.FmtId);
        Assert.Equal(WpdNative.ObjectSize.Pid, second.Pid);

        var ids = WpdNative.Create<IPortableDevicePropVariantCollection>(WpdNative.ClsidPortableDevicePropVariantCollection);
        var id = new PropVariant { Vt = PropVariant.VT_LPWSTR, Pointer = Marshal.StringToCoTaskMemUni("o1A2") };
        Assert.Equal(0, ids.Add(&id));
        Marshal.FreeCoTaskMem(id.Pointer);
        Assert.Equal(0, ids.GetCount(&count));
        Assert.Equal(1U, count);
        PropVariant first = default;
        Assert.Equal(0, ids.GetAt(0, &first));
        Assert.Equal(PropVariant.VT_LPWSTR, first.Vt);
        Assert.Equal("o1A2", Marshal.PtrToStringUni(first.Pointer));
        _ = WpdNative.PropVariantClear(&first);
    }
}
