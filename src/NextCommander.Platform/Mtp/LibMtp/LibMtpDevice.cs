using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static NextCommander.Platform.Mtp.LibMtp.LibMtpNative;

namespace NextCommander.Platform.Mtp.LibMtp;

/// <summary>
/// One device opened through libmtp (Linux, macOS). Ids: <see cref="RootId"/> for the device, <c>s:storage</c> for a
/// storage and <c>o:storage:handle</c> for a folder or file (hexadecimal): libmtp lists a folder by its storage and
/// handle. A device handle of libmtp is not for several threads: every call holds the device's lock. Files are read
/// into a temporary file first (the device cannot answer other calls while a transfer runs).
/// </summary>
internal sealed unsafe class LibMtpDevice : IMtpDevice
{
    private readonly Lock _lock = new();
    private IntPtr _device;

    private LibMtpDevice(IntPtr device) => _device = device;

    public string RootId => "device";

    public static LibMtpDevice Open(RawMtpDevice raw)
    {
        // macOS: its camera service (ptpcamerad) takes the phone first; it is stopped and the phone opened again
        var device = OperatingSystem.IsMacOS()
            ? MacImageCapture.OpenReleasing(() => LibMtpNative.Open(raw), MacImageCapture.Release, Thread.Sleep)
            : LibMtpNative.Open(raw);
        if (device == IntPtr.Zero)
        {
            throw new IOException(OperatingSystem.IsMacOS()
                ? "The device could not be opened. macOS gives a phone or camera to the first program that takes it: close Photos, Image Capture, Android File Transfer and Smart Switch (NextCommander already stops the camera service ptpcamerad), unplug the device and plug it in again. The phone must be unlocked and in \"File transfer\" mode."
                : "The device could not be opened. Another program may be using it (the desktop's file manager mounts phones: unmount it there), or the user may not have the right to use it (the package libmtp installs the rules for that). The phone must be unlocked and in \"File transfer\" mode.");
        }
        return new LibMtpDevice(device);
    }

    internal static string StorageKey(uint storage) => "s:" + storage.ToString("x", CultureInfo.InvariantCulture);

    internal static string ObjectKey(uint storage, uint handle) =>
        "o:" + storage.ToString("x", CultureInfo.InvariantCulture) + ":" + handle.ToString("x", CultureInfo.InvariantCulture);

    /// <summary>The storage and handle of an id; the handle of a storage is libmtp's "root of the storage".</summary>
    internal static (uint Storage, uint Handle) Parse(string id)
    {
        var parts = id.Split(':');
        if (parts.Length == 2 && parts[0] == "s" && uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var storage))
        {
            return (storage, FilesAndFoldersRoot);
        }
        if (parts.Length == 3 && parts[0] == "o" && uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out storage)
            && uint.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var handle))
        {
            return (storage, handle);
        }
        throw new FileNotFoundException("No such object on the device: " + id);
    }

    public IReadOnlyList<MtpObject> ListChildren(string parentId)
    {
        lock (_lock)
        {
            var device = Handle();
            if (parentId == RootId)
            {
                var storages = Storages(device);
                // a phone always has a storage: none means it is locked or has not been allowed yet
                return storages.Count > 0
                    ? storages.Select(s => new MtpObject(StorageKey(s.Id), s.Name, true, 0, null)).ToList()
                    : throw new MtpAccessDeniedException();
            }
            var (storage, parent) = Parse(parentId);
            var result = new List<MtpObject>();
            var file = GetFilesAndFolders(device, storage, parent);
            if (file == IntPtr.Zero && TakeErrors(device) is { } errors)
            {
                throw new IOException("The folder could not be read: " + errors); // an empty folder has no error
            }
            while (file != IntPtr.Zero)
            {
                var next = Read<IntPtr>(file, FileNext);
                result.Add(ObjectOf(file, storage));
                DestroyFile(file);
                file = next;
            }
            return result;
        }
    }

    private static MtpObject ObjectOf(IntPtr file, uint storage)
    {
        var time = Read<long>(file, FileModified);
        DateTime? modified = time > 0 ? DateTimeOffset.FromUnixTimeSeconds(time).LocalDateTime : null;
        return new MtpObject(ObjectKey(storage, Read<uint>(file, FileItemId)), Utf8(Read<IntPtr>(file, FileName)) ?? string.Empty,
            Read<int>(file, FileType) == FileTypeFolder, (long)Math.Min(Read<ulong>(file, FileSize), long.MaxValue), modified);
    }

    private sealed record StorageInfo(uint Id, string Name, long Total, long Free);

    /// <summary>The storages of the device (read again: free space changes); two storages with one name get "(2)".</summary>
    private static List<StorageInfo> Storages(IntPtr device)
    {
        if (GetStorage(device, 0) != 0)
        {
            // Android refuses GetStorageIDs (access denied) until the phone is unlocked and access is allowed
            throw new MtpAccessDeniedException(TakeErrors(device));
        }
        var result = new List<StorageInfo>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var storage = Read<IntPtr>(device, DeviceStorage); storage != IntPtr.Zero; storage = Read<IntPtr>(storage, StorageNext))
        {
            var name = (Utf8(Read<IntPtr>(storage, StorageDescription)) ?? Utf8(Read<IntPtr>(storage, StorageVolume)) ?? "Storage").Replace('/', ' ').Trim();
            if (name.Length == 0)
            {
                name = "Storage";
            }
            var unique = name;
            for (var i = 2; !used.Add(unique); i++)
            {
                unique = $"{name} ({i})";
            }
            result.Add(new StorageInfo(Read<uint>(storage, StorageId), unique,
                (long)Math.Min(Read<ulong>(storage, StorageMaxCapacity), long.MaxValue), (long)Math.Min(Read<ulong>(storage, StorageFree), long.MaxValue)));
        }
        return result;
    }

    public (long Total, long Free)? StorageSpace(string storageId)
    {
        lock (_lock)
        {
            var (storage, _) = Parse(storageId);
            return Storages(Handle()).FirstOrDefault(s => s.Id == storage) is { Total: > 0 } s ? (s.Total, s.Free) : null;
        }
    }

    public Stream OpenRead(string objectId)
    {
        var (_, handle) = Parse(objectId);
        var path = Path.Combine(Path.GetTempPath(), "nc-mtp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                lock (_lock)
                {
                    var device = Handle();
                    var transfer = GCHandle.Alloc(target);
                    try
                    {
                        if (GetFileToHandler(device, handle, &Put, GCHandle.ToIntPtr(transfer), IntPtr.Zero, IntPtr.Zero) != 0)
                        {
                            ThrowOnError(device, "The file could not be read from the device");
                        }
                    }
                    finally
                    {
                        transfer.Free();
                    }
                }
            }
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.DeleteOnClose);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    public void CreateFile(string parentId, string name, long size, DateTime? modified, Stream data)
    {
        var (storage, parent) = Parse(parentId);
        lock (_lock)
        {
            var device = Handle();
            var file = NewFile();
            var transfer = GCHandle.Alloc(data);
            try
            {
                Write(file, FileParentId, parent);
                Write(file, FileStorageId, storage);
                Write(file, FileName, CString(name)); // freed by LIBMTP_destroy_file_t
                Write(file, FileSize, (ulong)size);
                Write(file, FileModified, new DateTimeOffset(modified ?? DateTime.Now).ToUnixTimeSeconds());
                Write(file, FileType, FileTypeUnknown);
                if (SendFileFromHandler(device, &Get, GCHandle.ToIntPtr(transfer), file, IntPtr.Zero, IntPtr.Zero) != 0)
                {
                    ThrowOnError(device, "The file could not be written to the device");
                }
            }
            finally
            {
                transfer.Free();
                DestroyFile(file);
            }
        }
    }

    public MtpObject CreateFolder(string parentId, string name)
    {
        var (storage, parent) = Parse(parentId);
        lock (_lock)
        {
            var device = Handle();
            var text = CString(name); // libmtp may change the name in place (devices with 7-bit names only)
            try
            {
                var id = LibMtpNative.CreateFolder(device, (byte*)text, parent, storage);
                if (id == 0)
                {
                    ThrowOnError(device, "The folder could not be created on the device");
                }
                return new MtpObject(ObjectKey(storage, id), name, true, 0, DateTime.Now);
            }
            finally
            {
                Free(text);
            }
        }
    }

    public void Delete(string objectId, bool recursive)
    {
        var (_, handle) = Parse(objectId);
        if (recursive)
        {
            // not every device deletes the contents of a folder with it: the contents first
            foreach (var child in ListChildren(objectId))
            {
                Delete(child.Id, child.IsFolder);
            }
        }
        lock (_lock)
        {
            var device = Handle();
            if (DeleteObject(device, handle) != 0)
            {
                ThrowOnError(device, "The object could not be deleted from the device");
            }
        }
    }

    public void Rename(string objectId, string newName)
    {
        var (_, handle) = Parse(objectId);
        lock (_lock)
        {
            var device = Handle();
            var file = GetFilemetadata(device, handle);
            if (file == IntPtr.Zero)
            {
                ThrowOnError(device, "The object was not found on the device");
            }
            var text = CString(newName);
            try
            {
                if (SetFileName(device, file, (byte*)text) != 0)
                {
                    ThrowOnError(device, "The object could not be renamed on the device");
                }
            }
            finally
            {
                Free(text);
                DestroyFile(file);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_device != IntPtr.Zero)
            {
                ReleaseDevice(_device);
                _device = IntPtr.Zero;
            }
        }
    }

    private IntPtr Handle() => _device != IntPtr.Zero ? _device : throw new ObjectDisposedException(nameof(LibMtpDevice));

    /// <summary>The messages of libmtp's error stack (which is cleared); null when it is empty.</summary>
    private static string? TakeErrors(IntPtr device)
    {
        var messages = new List<string>();
        for (var error = GetErrorstack(device); error != IntPtr.Zero; error = Read<IntPtr>(error, ErrorNext))
        {
            if (Utf8(Read<IntPtr>(error, ErrorText))?.Trim() is { Length: > 0 } text && !messages.Contains(text))
            {
                messages.Add(text);
            }
        }
        ClearErrorstack(device);
        return messages.Count == 0 ? null : string.Join("; ", messages);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowOnError(IntPtr device, string what) =>
        throw new IOException(TakeErrors(device) is { } messages ? what + ": " + messages : what + ".");

    /// <summary>libmtp hands a piece of the file being read: written to the stream of the transfer.</summary>
    [UnmanagedCallersOnly]
    private static ushort Put(IntPtr parameters, IntPtr transfer, uint length, byte* data, uint* written)
    {
        try
        {
            ((Stream)GCHandle.FromIntPtr(transfer).Target!).Write(new ReadOnlySpan<byte>(data, (int)length));
            *written = length;
            return 0; // LIBMTP_HANDLER_RETURN_OK
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException)
        {
            *written = 0;
            return 1; // LIBMTP_HANDLER_RETURN_ERROR
        }
    }

    /// <summary>libmtp asks for the next piece of the file being written: read from the stream of the transfer.</summary>
    [UnmanagedCallersOnly]
    private static ushort Get(IntPtr parameters, IntPtr transfer, uint wanted, byte* data, uint* got)
    {
        try
        {
            var stream = (Stream)GCHandle.FromIntPtr(transfer).Target!;
            var buffer = new Span<byte>(data, (int)wanted);
            var total = 0;
            while (total < buffer.Length)
            {
                var read = stream.Read(buffer[total..]);
                if (read == 0)
                {
                    break;
                }
                total += read;
            }
            *got = (uint)total;
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException)
        {
            *got = 0;
            return 1;
        }
    }
}
