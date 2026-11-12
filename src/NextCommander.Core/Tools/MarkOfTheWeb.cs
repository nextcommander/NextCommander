using System.Runtime.InteropServices;
using System.Text;

namespace NextCommander.Core.Tools;

/// <summary>
/// "Downloaded from the internet" marks: the Zone.Identifier stream on Windows (NTFS) and the com.apple.quarantine
/// attribute on macOS. Downloads get one, and files extracted from a marked archive inherit it (like Explorer), so the
/// system still warns before running them. No-op on Linux and file systems without streams.
/// </summary>
public static partial class MarkOfTheWeb
{
    private const string StreamSuffix = ":Zone.Identifier";
    private const string QuarantineAttribute = "com.apple.quarantine";

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>The raw mark of a local file (Zone.Identifier text / quarantine value), or null.</summary>
    public static string? Read(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return File.Exists(path + StreamSuffix) ? File.ReadAllText(path + StreamSuffix) : null;
            }
            if (OperatingSystem.IsMacOS())
            {
                return GetXattr(path, QuarantineAttribute);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // no stream support (FAT, network share)
        }
        return null;
    }

    /// <summary>Writes a raw mark read with <see cref="Read"/>. Returns false when the file system cannot hold it.</summary>
    public static bool Write(string path, string mark)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(path + StreamSuffix, mark);
                return true;
            }
            if (OperatingSystem.IsMacOS())
            {
                return SetXattr(path, QuarantineAttribute, mark);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // no stream support
        }
        return false;
    }

    /// <summary>Marks a downloaded file as coming from the internet (zone 3) with its source address.</summary>
    public static bool MarkDownloaded(string path, Uri source)
    {
        if (OperatingSystem.IsWindows())
        {
            return Write(path, $"[ZoneTransfer]\r\nZoneId=3\r\nHostUrl={source}\r\n");
        }
        if (OperatingSystem.IsMacOS())
        {
            // flags;time;agent;uuid — 0081: downloaded, not yet approved
            var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return Write(path, $"0081;{seconds:x8};NextCommander;");
        }
        return false;
    }

    /// <summary>Copies the mark of <paramref name="from"/> (e.g. an archive) to <paramref name="to"/>, if it has one.</summary>
    public static bool Propagate(string from, string to) => Read(from) is { } mark && Write(to, mark);

    private static string? GetXattr(string path, string name)
    {
        var size = getxattr(path, name, null, 0, 0, 0);
        if (size <= 0)
        {
            return null;
        }
        var buffer = new byte[size];
        size = getxattr(path, name, buffer, buffer.Length, 0, 0);
        return size > 0 ? Encoding.UTF8.GetString(buffer, 0, (int)size) : null;
    }

    private static bool SetXattr(string path, string name, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return setxattr(path, name, bytes, bytes.Length, 0, 0) == 0;
    }

    // macOS libc (position and options are 0 for regular files)
    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint getxattr(string path, string name, byte[]? value, nint size, uint position, int options);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int setxattr(string path, string name, byte[] value, nint size, uint position, int options);
}
