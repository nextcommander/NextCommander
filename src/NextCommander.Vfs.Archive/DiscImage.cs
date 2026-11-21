using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;

namespace NextCommander.Vfs.Archive;

/// <summary>
/// Disc images (.iso; user's request, 21 November 2026) read with DiscUtils (MIT): UDF when the image has it (DVD and
/// Blu-ray images, whose ISO 9660 part is often only a stub), else ISO 9660 with Joliet's long names. Read-only.
/// </summary>
internal static class DiscImage
{
    /// <summary>The file system of the image (the caller disposes it; the stream stays the caller's).</summary>
    public static DiscFileSystem Open(Stream image)
    {
        try
        {
            if (UdfReader.Detect(image))
            {
                image.Position = 0;
                return new UdfReader(image);
            }
            image.Position = 0;
            if (CDReader.Detect(image))
            {
                image.Position = 0;
                return new CDReader(image, joliet: true, hideVersions: true);
            }
        }
        catch (Exception ex) when (ex is InvalidFileSystemException or EndOfStreamException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("Not a readable disc image: " + ex.Message, ex);
        }
        throw new InvalidDataException("Not a disc image (no ISO 9660 or UDF file system found).");
    }

    /// <summary>Every folder and file of the image ("\\DIR\\FILE.TXT"), folders before their contents.</summary>
    public static IEnumerable<(string Path, bool IsDirectory, long Size, DateTime Modified)> Entries(DiscFileSystem disc)
    {
        var folders = new Stack<string>();
        folders.Push(string.Empty);
        while (folders.Count > 0)
        {
            var folder = folders.Pop();
            foreach (var directory in disc.GetDirectories(folder))
            {
                yield return (directory, true, 0, Time(() => disc.GetLastWriteTime(directory)));
                folders.Push(directory);
            }
            foreach (var file in disc.GetFiles(folder))
            {
                yield return (file, false, disc.GetFileLength(file), Time(() => disc.GetLastWriteTime(file)));
            }
        }
    }

    /// <summary>A time the image may not have (or have out of range).</summary>
    private static DateTime Time(Func<DateTime> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or NotSupportedException or IOException)
        {
            return DateTime.MinValue;
        }
    }
}
