using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NextCommander.Platform.Mtp;

namespace NextCommander.Platform.Windows.Wpd;

/// <summary>One portable device opened through WPD: listing, reading, writing, deleting and renaming its objects.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WpdDevice : IMtpDevice
{
    private static readonly Guid ContentTypeGenericFile = new("0085e0a6-8d34-45d7-bc5c-447e59c73d48");

    private readonly IPortableDevice _device;
    private readonly IPortableDeviceContent _content;
    private readonly IPortableDeviceProperties _properties;
    private readonly IPortableDeviceResources _resources;
    private readonly IPortableDeviceKeyCollection _listKeys;
    private bool _closed;

    private WpdDevice(IPortableDevice device)
    {
        _device = device;
        WpdNative.Check(device.Content(out _content));
        WpdNative.Check(_content.Properties(out _properties));
        WpdNative.Check(_content.Transfer(out _resources));
        _listKeys = WpdNative.Create<IPortableDeviceKeyCollection>(WpdNative.ClsidPortableDeviceKeyCollection);
        foreach (var key in new[] { WpdNative.ObjectName, WpdNative.ObjectOriginalFileName, WpdNative.ObjectContentType, WpdNative.ObjectSize, WpdNative.ObjectDateModified })
        {
            var k = key;
            _listKeys.Add(&k);
        }
    }

    public static WpdDevice Open(string deviceId)
    {
        var device = WpdNative.Create<IPortableDevice>(WpdNative.ClsidPortableDeviceFtm);
        var client = WpdNative.Create<IPortableDeviceValues>(WpdNative.ClsidPortableDeviceValues);
        var name = WpdNative.ClientName;
        var major = WpdNative.ClientMajorVersion;
        var minor = WpdNative.ClientMinorVersion;
        var revision = WpdNative.ClientRevision;
        fixed (char* clientName = "NextCommander")
        {
            client.SetStringValue(&name, clientName);
        }
        client.SetUnsignedIntegerValue(&major, 1);
        client.SetUnsignedIntegerValue(&minor, 0);
        client.SetUnsignedIntegerValue(&revision, 0);
        fixed (char* id = deviceId)
        {
            WpdNative.Check(device.Open(id, client));
        }
        return new WpdDevice(device);
    }

    public string RootId => WpdNative.DeviceObjectId;

    public IReadOnlyList<MtpObject> ListChildren(string parentId)
    {
        WpdNative.EnsureMta();
        IEnumPortableDeviceObjectIDs ids;
        fixed (char* parent = parentId)
        {
            WpdNative.Check(_content.EnumObjects(0, parent, IntPtr.Zero, out ids));
        }
        var result = new List<MtpObject>();
        var batch = stackalloc IntPtr[64];
        while (true)
        {
            uint fetched = 0;
            var hr = ids.Next(64, batch, &fetched);
            WpdNative.Check(hr);
            for (var i = 0; i < fetched; i++)
            {
                var id = WpdNative.TakeString(batch[i]);
                if (Describe(id) is { } item)
                {
                    result.Add(item);
                }
            }
            if (hr == WpdNative.SFalse || fetched == 0)
            {
                break;
            }
        }
        return result;
    }

    private MtpObject? Describe(string objectId)
    {
        IPortableDeviceValues values;
        fixed (char* id = objectId)
        {
            if (_properties.GetValues(id, _listKeys, out values) < 0)
            {
                return null;
            }
        }
        var name = String(values, WpdNative.ObjectOriginalFileName) is { Length: > 0 } original ? original : String(values, WpdNative.ObjectName) ?? objectId;
        var type = WpdNative.ObjectContentType;
        Guid contentType;
        var folder = values.GetGuidValue(&type, &contentType) >= 0
            && (contentType == WpdNative.ContentTypeFolder || contentType == WpdNative.ContentTypeFunctionalObject);
        var sizeKey = WpdNative.ObjectSize;
        ulong size = 0;
        values.GetUnsignedLargeIntegerValue(&sizeKey, &size);
        return new MtpObject(objectId, name, folder, (long)Math.Min(size, long.MaxValue), Date(values, WpdNative.ObjectDateModified));
    }

    private static string? String(IPortableDeviceValues values, PropertyKey key)
    {
        IntPtr text;
        return values.GetStringValue(&key, &text) >= 0 && text != IntPtr.Zero ? WpdNative.TakeString(text) : null;
    }

    private static DateTime? Date(IPortableDeviceValues values, PropertyKey key)
    {
        PropVariant value = default;
        if (values.GetValue(&key, &value) < 0)
        {
            return null;
        }
        try
        {
            return value.Vt == PropVariant.VT_DATE && value.Date > 0 ? DateTime.FromOADate(value.Date) : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        finally
        {
            _ = WpdNative.PropVariantClear(&value);
        }
    }

    public Stream OpenRead(string objectId)
    {
        WpdNative.EnsureMta();
        var key = WpdNative.ResourceDefault;
        uint optimal = 0;
        IWpdStream stream;
        fixed (char* id = objectId)
        {
            WpdNative.Check(_resources.GetStream(id, &key, WpdNative.StgmRead, &optimal, out stream));
        }
        return new WpdReadStream(stream);
    }

    public void CreateFile(string parentId, string name, long size, DateTime? modified, Stream data)
    {
        WpdNative.EnsureMta();
        var values = NewObjectValues(parentId, name, ContentTypeGenericFile, WpdNative.FormatUnspecified);
        var sizeKey = WpdNative.ObjectSize;
        values.SetUnsignedLargeIntegerValue(&sizeKey, (ulong)size);
        if (modified is { } time)
        {
            var dateKey = WpdNative.ObjectDateModified;
            var date = new PropVariant { Vt = PropVariant.VT_DATE, Date = time.ToOADate() };
            values.SetValue(&dateKey, &date);
        }
        uint optimal = 0;
        WpdNative.Check(_content.CreateObjectWithPropertiesAndData(values, out var target, &optimal, null));
        var buffer = new byte[Math.Clamp((int)optimal, 64 * 1024, 1024 * 1024)];
        int read;
        while ((read = data.Read(buffer, 0, buffer.Length)) > 0)
        {
            fixed (byte* p = buffer)
            {
                var offset = 0;
                while (offset < read)
                {
                    uint written = 0;
                    WpdNative.Check(target.Write(p + offset, (uint)(read - offset), &written));
                    if (written == 0)
                    {
                        throw new IOException("The device took no data.");
                    }
                    offset += (int)written;
                }
            }
        }
        // without Commit the object is not created (a failed copy leaves nothing behind)
        WpdNative.Check(target.Commit(0));
    }

    public MtpObject CreateFolder(string parentId, string name)
    {
        WpdNative.EnsureMta();
        var values = NewObjectValues(parentId, name, WpdNative.ContentTypeFolder, WpdNative.FormatPropertiesOnly);
        IntPtr id;
        WpdNative.Check(_content.CreateObjectWithPropertiesOnly(values, &id));
        return new MtpObject(WpdNative.TakeString(id), name, true, 0, DateTime.Now);
    }

    private static IPortableDeviceValues NewObjectValues(string parentId, string name, Guid contentType, Guid format)
    {
        var values = WpdNative.Create<IPortableDeviceValues>(WpdNative.ClsidPortableDeviceValues);
        var parentKey = WpdNative.ObjectParentId;
        var nameKey = WpdNative.ObjectName;
        var fileNameKey = WpdNative.ObjectOriginalFileName;
        var typeKey = WpdNative.ObjectContentType;
        var formatKey = WpdNative.ObjectFormat;
        fixed (char* parent = parentId)
        fixed (char* n = name)
        {
            values.SetStringValue(&parentKey, parent);
            values.SetStringValue(&nameKey, n);
            values.SetStringValue(&fileNameKey, n);
        }
        values.SetGuidValue(&typeKey, &contentType);
        values.SetGuidValue(&formatKey, &format);
        return values;
    }

    public void Delete(string objectId, bool recursive)
    {
        WpdNative.EnsureMta();
        var ids = WpdNative.Create<IPortableDevicePropVariantCollection>(WpdNative.ClsidPortableDevicePropVariantCollection);
        var value = new PropVariant { Vt = PropVariant.VT_LPWSTR, Pointer = Marshal.StringToCoTaskMemUni(objectId) };
        try
        {
            WpdNative.Check(ids.Add(&value));
        }
        finally
        {
            Marshal.FreeCoTaskMem(value.Pointer); // the collection keeps its own copy
        }
        WpdNative.Check(_content.Delete(recursive ? WpdNative.DeleteWithRecursion : WpdNative.DeleteNoRecursion, ids, null));
    }

    public void Rename(string objectId, string newName)
    {
        WpdNative.EnsureMta();
        var values = WpdNative.Create<IPortableDeviceValues>(WpdNative.ClsidPortableDeviceValues);
        var fileNameKey = WpdNative.ObjectOriginalFileName;
        fixed (char* n = newName)
        {
            values.SetStringValue(&fileNameKey, n);
        }
        IPortableDeviceValues results;
        fixed (char* id = objectId)
        {
            WpdNative.Check(_properties.SetValues(id, values, out results));
        }
        int error = 0;
        if (results.GetErrorValue(&fileNameKey, &error) >= 0 && error < 0)
        {
            throw WpdNative.ErrorFor(error);
        }
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }
        _closed = true;
        try
        {
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            {
                Task.Run(() => _device.Close()).Wait();
            }
            else
            {
                _device.Close();
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or AggregateException)
        {
            // the device is gone already
        }
    }

    /// <summary>Read-only stream over a WPD resource stream.</summary>
    private sealed class WpdReadStream(IWpdStream stream) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }
            WpdNative.EnsureMta();
            uint read = 0;
            fixed (byte* p = buffer)
            {
                WpdNative.Check(stream.Read(p, (uint)buffer.Length, &read));
            }
            return (int)read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.Run(() => Read(buffer, offset, count), cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Run(() => Read(buffer.Span), cancellationToken));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
