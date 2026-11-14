using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace NextCommander.Platform.Windows.Wpd;

// Windows Portable Devices (PortableDeviceApi.h / PortableDeviceTypes.h). Source-generated COM: the application turns
// the built-in COM interop off (BuiltInComInteropSupport=false). The methods are declared in vtable order; methods after
// the last one used are left out. Every call returns its HRESULT (PreserveSig) so a missing property is not an exception.

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey(Guid fmtid, uint pid)
{
    public Guid FmtId = fmtid;
    public uint Pid = pid;
}

/// <summary>PROPVARIANT, only the members used here (the union starts at offset 8; 24 bytes on 64-bit).</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    public const ushort VT_DATE = 7;
    public const ushort VT_LPWSTR = 31;

    [FieldOffset(0)]
    public ushort Vt;

    [FieldOffset(8)]
    public IntPtr Pointer;

    [FieldOffset(8)]
    public double Date;
}

[GeneratedComInterface]
[Guid("a1567595-4c2f-4574-a6fa-ecef917b9a40")]
internal unsafe partial interface IPortableDeviceManager
{
    [PreserveSig]
    int GetDevices(IntPtr* pnpDeviceIds, uint* count);

    [PreserveSig]
    int RefreshDeviceList();

    [PreserveSig]
    int GetDeviceFriendlyName(char* pnpDeviceId, char* name, uint* length);

    [PreserveSig]
    int GetDeviceDescription(char* pnpDeviceId, char* description, uint* length);

    [PreserveSig]
    int GetDeviceManufacturer(char* pnpDeviceId, char* manufacturer, uint* length);
}

[GeneratedComInterface]
[Guid("625e2df8-6392-4cf0-9ad1-3cfa5f17775c")]
internal unsafe partial interface IPortableDevice
{
    [PreserveSig]
    int Open(char* pnpDeviceId, IPortableDeviceValues clientInfo);

    [PreserveSig]
    int SendCommand(uint flags, IntPtr parameters, IntPtr* results);

    [PreserveSig]
    int Content(out IPortableDeviceContent content);

    [PreserveSig]
    int Capabilities(IntPtr* capabilities);

    [PreserveSig]
    int Cancel();

    [PreserveSig]
    int Close();
}

[GeneratedComInterface]
[Guid("6a96ed84-7c73-4480-9938-bf5af477d426")]
internal unsafe partial interface IPortableDeviceContent
{
    [PreserveSig]
    int EnumObjects(uint flags, char* parentObjectId, IntPtr filter, out IEnumPortableDeviceObjectIDs objectIds);

    [PreserveSig]
    int Properties(out IPortableDeviceProperties properties);

    [PreserveSig]
    int Transfer(out IPortableDeviceResources resources);

    [PreserveSig]
    int CreateObjectWithPropertiesOnly(IPortableDeviceValues values, IntPtr* objectId);

    [PreserveSig]
    int CreateObjectWithPropertiesAndData(IPortableDeviceValues values, out IWpdStream data, uint* optimalWriteBufferSize, IntPtr* cookie);

    [PreserveSig]
    int Delete(uint options, IPortableDevicePropVariantCollection objectIds, IntPtr* results);
}

[GeneratedComInterface]
[Guid("10ece955-cf41-4728-bfa0-41eedf1bbf19")]
internal unsafe partial interface IEnumPortableDeviceObjectIDs
{
    /// <summary>S_OK: all asked for were given; S_FALSE: fewer (the end).</summary>
    [PreserveSig]
    int Next(uint count, IntPtr* objectIds, uint* fetched);
}

[GeneratedComInterface]
[Guid("7f6d695c-03df-4439-a809-59266beee3a6")]
internal unsafe partial interface IPortableDeviceProperties
{
    [PreserveSig]
    int GetSupportedProperties(char* objectId, IntPtr* keys);

    [PreserveSig]
    int GetPropertyAttributes(char* objectId, PropertyKey* key, IntPtr* attributes);

    [PreserveSig]
    int GetValues(char* objectId, IPortableDeviceKeyCollection? keys, out IPortableDeviceValues values);

    [PreserveSig]
    int SetValues(char* objectId, IPortableDeviceValues values, out IPortableDeviceValues results);
}

[GeneratedComInterface]
[Guid("fd8878ac-d841-4d17-891c-e6829cdb6934")]
internal unsafe partial interface IPortableDeviceResources
{
    [PreserveSig]
    int GetSupportedResources(char* objectId, IntPtr* keys);

    [PreserveSig]
    int GetResourceAttributes(char* objectId, PropertyKey* key, IntPtr* attributes);

    [PreserveSig]
    int GetStream(char* objectId, PropertyKey* key, uint mode, uint* optimalBufferSize, out IWpdStream stream);
}

[GeneratedComInterface]
[Guid("6848f6f2-3155-4f86-b6f5-263eeeab3143")]
internal unsafe partial interface IPortableDeviceValues
{
    [PreserveSig]
    int GetCount(uint* count);

    [PreserveSig]
    int GetAt(uint index, PropertyKey* key, PropVariant* value);

    [PreserveSig]
    int SetValue(PropertyKey* key, PropVariant* value);

    [PreserveSig]
    int GetValue(PropertyKey* key, PropVariant* value);

    [PreserveSig]
    int SetStringValue(PropertyKey* key, char* value);

    [PreserveSig]
    int GetStringValue(PropertyKey* key, IntPtr* value);

    [PreserveSig]
    int SetUnsignedIntegerValue(PropertyKey* key, uint value);

    [PreserveSig]
    int GetUnsignedIntegerValue(PropertyKey* key, uint* value);

    [PreserveSig]
    int SetSignedIntegerValue(PropertyKey* key, int value);

    [PreserveSig]
    int GetSignedIntegerValue(PropertyKey* key, int* value);

    [PreserveSig]
    int SetUnsignedLargeIntegerValue(PropertyKey* key, ulong value);

    [PreserveSig]
    int GetUnsignedLargeIntegerValue(PropertyKey* key, ulong* value);

    [PreserveSig]
    int SetSignedLargeIntegerValue(PropertyKey* key, long value);

    [PreserveSig]
    int GetSignedLargeIntegerValue(PropertyKey* key, long* value);

    [PreserveSig]
    int SetFloatValue(PropertyKey* key, float value);

    [PreserveSig]
    int GetFloatValue(PropertyKey* key, float* value);

    [PreserveSig]
    int SetErrorValue(PropertyKey* key, int value);

    [PreserveSig]
    int GetErrorValue(PropertyKey* key, int* value);

    [PreserveSig]
    int SetKeyValue(PropertyKey* key, PropertyKey* value);

    [PreserveSig]
    int GetKeyValue(PropertyKey* key, PropertyKey* value);

    [PreserveSig]
    int SetBoolValue(PropertyKey* key, int value);

    [PreserveSig]
    int GetBoolValue(PropertyKey* key, int* value);

    [PreserveSig]
    int SetIUnknownValue(PropertyKey* key, IntPtr value);

    [PreserveSig]
    int GetIUnknownValue(PropertyKey* key, IntPtr* value);

    [PreserveSig]
    int SetGuidValue(PropertyKey* key, Guid* value);

    [PreserveSig]
    int GetGuidValue(PropertyKey* key, Guid* value);
}

[GeneratedComInterface]
[Guid("dada2357-e0ad-492e-98db-dd61c53ba353")]
internal unsafe partial interface IPortableDeviceKeyCollection
{
    [PreserveSig]
    int GetCount(uint* count);

    [PreserveSig]
    int GetAt(uint index, PropertyKey* key);

    [PreserveSig]
    int Add(PropertyKey* key);
}

[GeneratedComInterface]
[Guid("89b2e422-4f1b-4316-bcef-a44afea83eb3")]
internal unsafe partial interface IPortableDevicePropVariantCollection
{
    [PreserveSig]
    int GetCount(uint* count);

    [PreserveSig]
    int GetAt(uint index, PropVariant* value);

    [PreserveSig]
    int Add(PropVariant* value);
}

/// <summary>IStream (with the ISequentialStream methods first).</summary>
[GeneratedComInterface]
[Guid("0000000c-0000-0000-c000-000000000046")]
internal unsafe partial interface IWpdStream
{
    [PreserveSig]
    int Read(byte* buffer, uint count, uint* read);

    [PreserveSig]
    int Write(byte* buffer, uint count, uint* written);

    [PreserveSig]
    int Seek(long move, uint origin, ulong* newPosition);

    [PreserveSig]
    int SetSize(ulong size);

    [PreserveSig]
    int CopyTo(IntPtr target, ulong count, ulong* read, ulong* written);

    [PreserveSig]
    int Commit(uint flags);

    [PreserveSig]
    int Revert();
}

[SupportedOSPlatform("windows")]
internal static unsafe partial class WpdNative
{
    public static readonly Guid ClsidPortableDeviceManager = new("0af10cec-2ecd-4b92-9581-34f6ae0637f3");
    public static readonly Guid ClsidPortableDeviceFtm = new("f7c0039a-4762-488a-b4b3-760ef9a1ba9b");
    public static readonly Guid ClsidPortableDeviceValues = new("0c15d503-d017-47ce-9016-7b3f978721cc");
    public static readonly Guid ClsidPortableDeviceKeyCollection = new("de2d022d-2480-43be-97f0-d1fa2cf98f4f");
    public static readonly Guid ClsidPortableDevicePropVariantCollection = new("08a99e2f-6d6d-4b80-af5a-baf2bcbe4cb9");

    /// <summary>GUID_DEVINTERFACE_WPD: the device interface of portable devices.</summary>
    public static readonly Guid DeviceInterfaceWpd = new("6ac27878-a6fa-4155-ba85-f98f491d4f33");

    public const string DeviceObjectId = "DEVICE";

    private static readonly Guid ObjectProperties = new("ef6b490d-5cd8-437a-affc-da8b60ee4a3c");
    public static readonly PropertyKey ObjectId = new(ObjectProperties, 2);
    public static readonly PropertyKey ObjectParentId = new(ObjectProperties, 3);
    public static readonly PropertyKey ObjectName = new(ObjectProperties, 4);
    public static readonly PropertyKey ObjectFormat = new(ObjectProperties, 6);
    public static readonly PropertyKey ObjectContentType = new(ObjectProperties, 7);
    public static readonly PropertyKey ObjectSize = new(ObjectProperties, 11);
    public static readonly PropertyKey ObjectOriginalFileName = new(ObjectProperties, 12);
    public static readonly PropertyKey ObjectDateModified = new(ObjectProperties, 19);

    private static readonly Guid StorageProperties = new("01a3057a-74d6-4e80-bea7-dc4c212ce50a");
    public static readonly PropertyKey StorageCapacity = new(StorageProperties, 4);
    public static readonly PropertyKey StorageFreeSpace = new(StorageProperties, 5);

    private static readonly Guid DeviceProperties = new("26d4979a-e643-4626-9e2b-736dc0c92fdc");
    public static readonly PropertyKey DeviceProtocol = new(DeviceProperties, 6);

    private static readonly Guid ClientInformation = new("204d9f0c-2292-4080-9f42-40664e70f859");
    public static readonly PropertyKey ClientName = new(ClientInformation, 2);
    public static readonly PropertyKey ClientMajorVersion = new(ClientInformation, 3);
    public static readonly PropertyKey ClientMinorVersion = new(ClientInformation, 4);
    public static readonly PropertyKey ClientRevision = new(ClientInformation, 5);

    public static readonly PropertyKey ResourceDefault = new(new Guid("e81e79be-34f0-41bf-b53f-f1a06ae87842"), 0);

    public static readonly Guid ContentTypeFolder = new("27e2e392-a111-48e0-ab0c-e17705a05f85");
    public static readonly Guid ContentTypeFunctionalObject = new("99ed0160-17ff-4c44-9d98-1d7a6f941921");
    public static readonly Guid ContentTypeUnspecified = new("28d8d31e-249c-454e-aabc-34883168e634");
    public static readonly Guid FormatUnspecified = new("30000000-ae6c-4804-98ba-c57b46965fe7");
    public static readonly Guid FormatPropertiesOnly = new("30010000-ae6c-4804-98ba-c57b46965fe7");

    public const uint StgmRead = 0;
    public const uint DeleteNoRecursion = 0;
    public const uint DeleteWithRecursion = 1;
    public const int SFalse = 1;

    private const uint ClsctxInprocServer = 1;
    private const uint CoinitMultithreaded = 0;

    private static readonly StrategyBasedComWrappers s_wrappers = new();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(IntPtr reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(PropVariant* value);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    private static partial uint CmGetDeviceInterfaceListSize(out uint length, in Guid interfaceClass, IntPtr deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW")]
    private static partial uint CmGetDeviceInterfaceList(in Guid interfaceClass, IntPtr deviceId, char* buffer, uint length, uint flags);

    /// <summary>
    /// WPD objects live in the multi-threaded apartment. Worker threads join it (a no-op when they already did);
    /// the UI thread (single-threaded apartment) must not make WPD calls.
    /// </summary>
    public static void EnsureMta()
    {
        var hr = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
        if (hr < 0)
        {
            throw new InvalidOperationException($"Portable devices are used from worker threads only (COM error 0x{hr:X8}).");
        }
    }

    public static T Create<T>(Guid clsid)
        where T : class
    {
        EnsureMta();
        var iid = typeof(T).GUID;
        Check(CoCreateInstance(clsid, IntPtr.Zero, ClsctxInprocServer, iid, out var instance));
        try
        {
            return (T)s_wrappers.GetOrCreateObjectForComInstance(instance, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(instance);
        }
    }

    /// <summary>
    /// Throws for a failed HRESULT: <see cref="IOException"/> (a device unplugged mid-call is an I/O error too) or
    /// <see cref="UnauthorizedAccessException"/>. The operation and the file are shown by the caller (the job's error dialog).
    /// </summary>
    public static void Check(int hr)
    {
        if (hr < 0)
        {
            throw ErrorFor(hr);
        }
    }

    public static Exception ErrorFor(int hr) => hr switch
    {
        unchecked((int)0x80070005) => new UnauthorizedAccessException("Access denied. Unlock the device and allow file transfer on it."),
        unchecked((int)0x80070002) or unchecked((int)0x80070003) => new FileNotFoundException("Not found on the device."),
        unchecked((int)0x800700B7) => new IOException("It already exists on the device."),
        unchecked((int)0x80070070) => new IOException("The device is full."),
        unchecked((int)0x802A0002) => new NextCommander.Platform.Mtp.MtpSessionLostException("The device connection was reset; it is opened again."),
        unchecked((int)0x8007048F) or unchecked((int)0x80070015) or unchecked((int)0x8007001F) => new IOException("The device was disconnected."),
        _ => new IOException($"Portable device error 0x{hr:X8}."),
    };

    /// <summary>A CoTaskMem string from a WPD call, freed.</summary>
    public static string TakeString(IntPtr value)
    {
        try
        {
            return Marshal.PtrToStringUni(value) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeCoTaskMem(value);
        }
    }

    /// <summary>The device paths of the portable devices present: cheap, for noticing devices plugged in or removed.</summary>
    public static string DeviceInterfaceList()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CmGetDeviceInterfaceListSize(out var length, DeviceInterfaceWpd, IntPtr.Zero, 0) != 0 || length == 0)
            {
                return string.Empty;
            }
            var buffer = new char[length];
            fixed (char* p = buffer)
            {
                var result = CmGetDeviceInterfaceList(DeviceInterfaceWpd, IntPtr.Zero, p, length, 0);
                if (result == 0)
                {
                    return new string(buffer).TrimEnd('\0').Replace('\0', '|');
                }
                if (result != 0x1A) // CR_BUFFER_SMALL: a device came meanwhile, ask again
                {
                    return string.Empty;
                }
            }
        }
        return string.Empty;
    }
}
