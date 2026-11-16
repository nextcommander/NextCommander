using System.Runtime.InteropServices;

namespace NextCommander.Platform.Mtp.LibMtp;

/// <summary>
/// libmtp (LGPL), loaded when it is there: Linux "libmtp.so.9" of the system (package libmtp9 / libmtp9t64 / libmtp,
/// with its udev rules giving the user the device), macOS the copy inside the .app bundle or Homebrew's
/// (<c>brew install libmtp</c>); <c>FM_LIBMTP</c> names another file. Without it there are no MTP devices (as before).
/// The structures are read at their offsets for 64-bit Linux and macOS (pointers and time_t of 8 bytes; libmtp.h):
/// <list type="bullet">
/// <item>LIBMTP_raw_device_t (40 bytes): device_entry { char* vendor @0, uint16 vendor_id @8, char* product @16,
/// uint16 product_id @24, uint32 device_flags @28 }, uint32 bus_location @32, uint8 devnum @36.</item>
/// <item>LIBMTP_mtpdevice_t: LIBMTP_devicestorage_t* storage @24.</item>
/// <item>LIBMTP_devicestorage_t: uint32 id @0, uint64 MaxCapacity @16, uint64 FreeSpaceInBytes @24,
/// char* StorageDescription @40, char* VolumeIdentifier @48, next @56.</item>
/// <item>LIBMTP_file_t: uint32 item_id @0, parent_id @4, storage_id @8, char* filename @16, uint64 filesize @24,
/// time_t modificationdate @32, LIBMTP_filetype_t filetype @40, next @48.</item>
/// <item>LIBMTP_error_t: int errornumber @0, char* error_text @8, next @16.</item>
/// </list>
/// </summary>
internal static unsafe class LibMtpNative
{
    public const int RawDeviceSize = 40;
    public const uint FilesAndFoldersRoot = 0xFFFFFFFF;
    public const int FileTypeFolder = 0;
    public const int FileTypeUnknown = 44;
    public const int ErrorNoDeviceAttached = 5;

    // offsets (see the summary)
    public const int RawVendor = 0, RawVendorId = 8, RawProduct = 16, RawProductId = 24, RawBus = 32, RawDevnum = 36;
    public const int DeviceStorage = 24;
    public const int StorageId = 0, StorageMaxCapacity = 16, StorageFree = 24, StorageDescription = 40, StorageVolume = 48, StorageNext = 56;
    public const int FileItemId = 0, FileParentId = 4, FileStorageId = 8, FileName = 16, FileSize = 24, FileModified = 32, FileType = 40, FileNext = 48;
    public const int ErrorText = 8, ErrorNext = 16;

    private static readonly Lock s_lock = new();
    private static bool s_tried;
    private static bool s_loaded;

    public static delegate* unmanaged<void> Init;
    public static delegate* unmanaged<int, void> SetDebug;
    public static delegate* unmanaged<IntPtr*, int*, int> DetectRawDevices;
    public static delegate* unmanaged<IntPtr, IntPtr> OpenRawDeviceUncached;
    public static delegate* unmanaged<IntPtr, void> ReleaseDevice;
    public static delegate* unmanaged<IntPtr, int, int> GetStorage;
    public static delegate* unmanaged<IntPtr, uint, uint, IntPtr> GetFilesAndFolders;
    public static delegate* unmanaged<IntPtr, uint, IntPtr> GetFilemetadata;
    public static delegate* unmanaged<IntPtr, uint, delegate* unmanaged<IntPtr, IntPtr, uint, byte*, uint*, ushort>, IntPtr, IntPtr, IntPtr, int> GetFileToHandler;
    public static delegate* unmanaged<IntPtr, delegate* unmanaged<IntPtr, IntPtr, uint, byte*, uint*, ushort>, IntPtr, IntPtr, IntPtr, IntPtr, int> SendFileFromHandler;
    public static delegate* unmanaged<IntPtr, byte*, uint, uint, uint> CreateFolder;
    public static delegate* unmanaged<IntPtr, uint, int> DeleteObject;
    public static delegate* unmanaged<IntPtr, IntPtr, byte*, int> SetFileName;
    public static delegate* unmanaged<IntPtr> NewFile;
    public static delegate* unmanaged<IntPtr, void> DestroyFile;
    public static delegate* unmanaged<IntPtr, IntPtr> GetErrorstack;
    public static delegate* unmanaged<IntPtr, void> ClearErrorstack;

    /// <summary>The files tried, in order: <c>FM_LIBMTP</c>, the copy next to the program (macOS bundle), the system's.</summary>
    internal static IEnumerable<string> Candidates(string? overridePath, string programFolder, bool macOS)
    {
        if (!string.IsNullOrEmpty(overridePath))
        {
            yield return overridePath;
        }
        if (macOS)
        {
            yield return Path.Combine(programFolder, "libmtp.9.dylib");
            yield return Path.GetFullPath(Path.Combine(programFolder, "..", "Frameworks", "libmtp.9.dylib"));
            yield return "/opt/homebrew/lib/libmtp.9.dylib"; // Homebrew on Apple silicon
            yield return "/usr/local/lib/libmtp.9.dylib"; // Homebrew on Intel
            yield return "/opt/local/lib/libmtp.9.dylib"; // MacPorts
            yield return "libmtp.9.dylib";
        }
        else
        {
            yield return "libmtp.so.9";
            yield return "libmtp.so";
        }
    }

    /// <summary>Loads libmtp once and initializes it; false when it is not installed (or not every function is there).</summary>
    public static bool TryLoad()
    {
        lock (s_lock)
        {
            if (s_tried)
            {
                return s_loaded;
            }
            s_tried = true;
            if (OperatingSystem.IsWindows() || IntPtr.Size != 8)
            {
                return false;
            }
            foreach (var candidate in Candidates(Environment.GetEnvironmentVariable("FM_LIBMTP"), AppContext.BaseDirectory, OperatingSystem.IsMacOS()))
            {
                if (NativeLibrary.TryLoad(candidate, out var library))
                {
                    try
                    {
                        Bind(library);
                        Init();
                        SetDebug(0);
                        s_loaded = true;
                        return true;
                    }
                    catch (EntryPointNotFoundException ex)
                    {
                        System.Diagnostics.Trace.TraceWarning($"libmtp ({candidate}) cannot be used: {ex.Message}");
                    }
                }
            }
            return false;
        }
    }

    private static void Bind(IntPtr library)
    {
        IntPtr F(string name) => NativeLibrary.GetExport(library, name);
        Init = (delegate* unmanaged<void>)F("LIBMTP_Init");
        SetDebug = (delegate* unmanaged<int, void>)F("LIBMTP_Set_Debug");
        DetectRawDevices = (delegate* unmanaged<IntPtr*, int*, int>)F("LIBMTP_Detect_Raw_Devices");
        OpenRawDeviceUncached = (delegate* unmanaged<IntPtr, IntPtr>)F("LIBMTP_Open_Raw_Device_Uncached");
        ReleaseDevice = (delegate* unmanaged<IntPtr, void>)F("LIBMTP_Release_Device");
        GetStorage = (delegate* unmanaged<IntPtr, int, int>)F("LIBMTP_Get_Storage");
        GetFilesAndFolders = (delegate* unmanaged<IntPtr, uint, uint, IntPtr>)F("LIBMTP_Get_Files_And_Folders");
        GetFilemetadata = (delegate* unmanaged<IntPtr, uint, IntPtr>)F("LIBMTP_Get_Filemetadata");
        GetFileToHandler = (delegate* unmanaged<IntPtr, uint, delegate* unmanaged<IntPtr, IntPtr, uint, byte*, uint*, ushort>, IntPtr, IntPtr, IntPtr, int>)F("LIBMTP_Get_File_To_Handler");
        SendFileFromHandler = (delegate* unmanaged<IntPtr, delegate* unmanaged<IntPtr, IntPtr, uint, byte*, uint*, ushort>, IntPtr, IntPtr, IntPtr, IntPtr, int>)F("LIBMTP_Send_File_From_Handler");
        CreateFolder = (delegate* unmanaged<IntPtr, byte*, uint, uint, uint>)F("LIBMTP_Create_Folder");
        DeleteObject = (delegate* unmanaged<IntPtr, uint, int>)F("LIBMTP_Delete_Object");
        SetFileName = (delegate* unmanaged<IntPtr, IntPtr, byte*, int>)F("LIBMTP_Set_File_Name");
        NewFile = (delegate* unmanaged<IntPtr>)F("LIBMTP_new_file_t");
        DestroyFile = (delegate* unmanaged<IntPtr, void>)F("LIBMTP_destroy_file_t");
        GetErrorstack = (delegate* unmanaged<IntPtr, IntPtr>)F("LIBMTP_Get_Errorstack");
        ClearErrorstack = (delegate* unmanaged<IntPtr, void>)F("LIBMTP_Clear_Errorstack");
    }

    public static string? Utf8(IntPtr text) => text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(text);

    public static T Read<T>(IntPtr structure, int offset) where T : unmanaged => *(T*)((byte*)structure + offset);

    public static void Write<T>(IntPtr structure, int offset, T value) where T : unmanaged => *(T*)((byte*)structure + offset) = value;

    /// <summary>A NUL-terminated UTF-8 copy in memory of the C allocator (libmtp frees strings it owns with free()).</summary>
    public static IntPtr CString(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var memory = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        bytes.CopyTo(new Span<byte>(memory, bytes.Length));
        memory[bytes.Length] = 0;
        return (IntPtr)memory;
    }

    public static void Free(IntPtr memory) => NativeMemory.Free((void*)memory);

    /// <summary>The MTP devices on the USB buses (libmtp's own list, devices it knows, and others that answer as MTP).</summary>
    public static IReadOnlyList<RawMtpDevice> Detect()
    {
        IntPtr devices = IntPtr.Zero;
        var count = 0;
        var error = DetectRawDevices(&devices, &count);
        try
        {
            if (error == ErrorNoDeviceAttached || devices == IntPtr.Zero)
            {
                return [];
            }
            if (error != 0)
            {
                throw new IOException($"The MTP devices could not be listed (libmtp error {error}).");
            }
            var result = new List<RawMtpDevice>();
            for (var i = 0; i < count; i++)
            {
                var raw = devices + i * RawDeviceSize;
                result.Add(new RawMtpDevice(
                    (int)Read<uint>(raw, RawBus), Read<byte>(raw, RawDevnum), Read<ushort>(raw, RawVendorId), Read<ushort>(raw, RawProductId),
                    Utf8(Read<IntPtr>(raw, RawVendor)), Utf8(Read<IntPtr>(raw, RawProduct))));
            }
            return result;
        }
        finally
        {
            if (devices != IntPtr.Zero)
            {
                Free(devices);
            }
        }
    }

    /// <summary>Opens the device at this USB address (the raw list is read again: the entry is copied by libmtp).</summary>
    public static IntPtr Open(RawMtpDevice device)
    {
        IntPtr devices = IntPtr.Zero;
        var count = 0;
        DetectRawDevices(&devices, &count);
        try
        {
            for (var i = 0; i < count && devices != IntPtr.Zero; i++)
            {
                var raw = devices + i * RawDeviceSize;
                if ((int)Read<uint>(raw, RawBus) == device.Bus && Read<byte>(raw, RawDevnum) == device.Address)
                {
                    return OpenRawDeviceUncached(raw);
                }
            }
            return IntPtr.Zero;
        }
        finally
        {
            if (devices != IntPtr.Zero)
            {
                Free(devices);
            }
        }
    }
}

/// <summary>An MTP device found by libmtp: its USB address and what libmtp's device list knows of it.</summary>
public sealed record RawMtpDevice(int Bus, int Address, int VendorId, int ProductId, string? Vendor, string? Product);
