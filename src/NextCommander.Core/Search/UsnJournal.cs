using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NextCommander.Core.Search;

/// <summary>
/// The NTFS (and ReFS) change journal of a Windows volume: every change on the volume (a file created, deleted,
/// renamed, written) gets a record with a growing number (USN). Remembering the number lets a later start learn
/// which folders changed meanwhile without walking the disk, also changes inside files that leave the folder's own
/// time as it was. Read without administrator rights (FSCTL_READ_UNPRIVILEGED_USN_JOURNAL, Windows 10 1709 and later);
/// the records then carry no names, only the ids of the file and its folder, and the folders are found by their id.
/// </summary>
public static partial class UsnJournal
{
    /// <summary>A place in a volume's journal.</summary>
    public readonly record struct Position(long JournalId, long Usn);

    private const uint FsctlQueryUsnJournal = 0x000900f4;
    private const uint FsctlReadUsnJournal = 0x000900bb;
    private const uint FsctlReadUnprivilegedUsnJournal = 0x000903ab;
    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const int BufferSize = 1 << 20;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(SafeFileHandle handle, uint code, void* input, int inputSize, void* output, int outputSize, out int returned, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial SafeFileHandle OpenFileById(SafeFileHandle hint, void* id, uint access, uint share, nint security, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial int GetFinalPathNameByHandle(SafeFileHandle handle, char* buffer, int size, uint flags);

    /// <summary>The volume of a folder ("C:\"), or null.</summary>
    public static string? VolumeOf(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path)) is { Length: > 0 } root && !root.StartsWith(@"\\", StringComparison.Ordinal) ? root : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle? Open(string volume)
    {
        var handle = CreateFile(volume, FileReadAttributes, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return handle;
    }

    [SupportedOSPlatform("windows")]
    private static unsafe (long JournalId, long First, long Next)? Query(SafeFileHandle volume)
    {
        var data = stackalloc long[10]; // USN_JOURNAL_DATA (V0: 56 bytes)
        return DeviceIoControl(volume, FsctlQueryUsnJournal, null, 0, data, 80, out _, 0) ? (data[0], data[1], data[2]) : null;
    }

    /// <summary>Where the journal of a volume is now; null when the volume has none (FAT, network) or it cannot be read.</summary>
    [SupportedOSPlatform("windows")]
    public static Position? Current(string volume)
    {
        using var handle = Open(volume);
        return handle is not null && Query(handle) is { } q ? new Position(q.JournalId, q.Next) : null;
    }

    /// <summary>
    /// The folders in which something changed since <paramref name="since"/> (full paths), and where the journal is now.
    /// Null when the changes cannot be known from the journal: another journal (it was recreated), records already
    /// dropped (the journal is full and wrapped), no access, or more than <paramref name="limit"/> folders.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static unsafe (List<string> Folders, Position Now)? ChangedFolders(string volume, Position since, int limit = 200_000, CancellationToken ct = default)
    {
        using var handle = Open(volume);
        if (handle is null || Query(handle) is not { } q || q.JournalId != since.JournalId || since.Usn < q.First || since.Usn > q.Next)
        {
            return null;
        }
        var parents = new HashSet<long>();
        var input = stackalloc long[6]; // READ_USN_JOURNAL_DATA_V1 (44 bytes, 48 with padding)
        var buffer = NativeMemory.Alloc(BufferSize);
        try
        {
            var start = since.Usn;
            var code = FsctlReadUnprivilegedUsnJournal;
            while (start < q.Next)
            {
                ct.ThrowIfCancellationRequested();
                input[0] = start;
                ((uint*)input)[2] = 0xFFFFFFFF; // every reason
                ((uint*)input)[3] = 0;          // not only on close
                input[2] = 0;                   // timeout
                input[3] = 0;                   // bytes to wait for: return at once
                input[4] = since.JournalId;
                ((ushort*)input)[20] = 2;       // records version 2 (V3 has 128 bit ids, ReFS)
                ((ushort*)input)[21] = 3;
                if (!DeviceIoControl(handle, code, input, 48, buffer, BufferSize, out var returned, 0))
                {
                    if (code == FsctlReadUnprivilegedUsnJournal)
                    {
                        code = FsctlReadUsnJournal; // before Windows 10 1709: works with administrator rights only
                        continue;
                    }
                    return null;
                }
                var next = *(long*)buffer;
                if (returned <= 8 || next <= start)
                {
                    break;
                }
                for (var offset = 8; offset + 24 <= returned;)
                {
                    var record = (byte*)buffer + offset;
                    var length = *(int*)record;
                    if (length <= 0)
                    {
                        break;
                    }
                    var major = *(ushort*)(record + 4);
                    if (major == 2)
                    {
                        parents.Add(*(long*)(record + 16));
                    }
                    else if (major == 3)
                    {
                        parents.Add(*(long*)(record + 24)); // the low half of the 128 bit parent id (NTFS ids fit in it)
                    }
                    offset += length;
                }
                if (parents.Count > limit)
                {
                    return null;
                }
                start = next;
            }

            var folders = new List<string>(parents.Count);
            var id = stackalloc long[3]; // FILE_ID_DESCRIPTOR: size, type 0 (64 bit id), id
            var path = stackalloc char[1024];
            foreach (var parent in parents)
            {
                ct.ThrowIfCancellationRequested();
                id[0] = 24; // dwSize = 24, Type = 0
                id[1] = parent;
                id[2] = 0;
                using var folder = OpenFileById(handle, id, 0, ShareAll, 0, BackupSemantics);
                if (folder.IsInvalid)
                {
                    continue; // deleted meanwhile (its own folder's record covers it) or no access
                }
                var length = GetFinalPathNameByHandle(folder, path, 1024, 0);
                if (length is > 0 and < 1024)
                {
                    var text = new string(path, 0, length);
                    folders.Add(text.StartsWith(@"\\?\", StringComparison.Ordinal) && !text.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? text[4..] : text);
                }
            }
            return (folders, new Position(q.JournalId, q.Next));
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }
}
