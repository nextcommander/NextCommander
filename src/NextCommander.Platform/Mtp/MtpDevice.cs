namespace NextCommander.Platform.Mtp;

/// <summary>An object on an MTP device: a storage (internal storage, SD card), a folder or a file.</summary>
public sealed record MtpObject(string Id, string Name, bool IsFolder, long Size, DateTime? Modified);

/// <summary>
/// The device handle is not open any more (Windows: E_WPD_DEVICE_NOT_OPENED, 0x802A0002): the device connected again,
/// for example after its USB mode changed. The call did not run; the device can be opened again.
/// </summary>
public sealed class MtpSessionLostException(string message) : IOException(message);

/// <summary>
/// The device does not show its storages: an Android phone that is locked, still asks "Allow access to phone data?",
/// or is set to charge only. The session is closed and opened again (after the user allows, Android connects again).
/// </summary>
public sealed class MtpAccessDeniedException(string? detail = null) : IOException(Text + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " (" + detail.Trim() + ")"))
{
    /// <summary>What the user is told to do.</summary>
    public const string Text =
        "The phone does not let its files be read. Unlock it; when it asks \"Allow access to phone data?\", tap Allow; "
        + "set its USB connection to \"File transfer\" (not \"Charging only\"). Then open the device again.";
}

/// <summary>A connected portable device: the system's id and its name (as the system shows it).</summary>
public sealed record MtpDeviceInfo(string Id, string Name);

/// <summary>
/// One opened MTP / PTP device (a phone, a camera, a player). Objects are addressed by the ids the device gives them;
/// the calls block and may be slow (USB round trips), so they run on worker threads. A device that is gone throws
/// <see cref="IOException"/>.
/// </summary>
public interface IMtpDevice : IDisposable
{
    /// <summary>The id of the device object whose children are the storages.</summary>
    string RootId { get; }

    IReadOnlyList<MtpObject> ListChildren(string parentId);

    Stream OpenRead(string objectId);

    /// <summary>Creates a file of <paramref name="size"/> bytes from <paramref name="data"/>; the time is a hint the device may ignore.</summary>
    void CreateFile(string parentId, string name, long size, DateTime? modified, Stream data);

    MtpObject CreateFolder(string parentId, string name);

    void Delete(string objectId, bool recursive);

    void Rename(string objectId, string newName);

    /// <summary>The ids of a folder's children only (cheap: no names or sizes), to notice changes.</summary>
    IReadOnlyList<string> ChildIds(string parentId) => [.. ListChildren(parentId).Select(o => o.Id)];

    /// <summary>The capacity and free space of a storage (a child of <see cref="RootId"/>); null when the device does not tell.</summary>
    (long Total, long Free)? StorageSpace(string storageId) => null;
}

/// <summary>The portable devices of the system (Windows: WPD).</summary>
public interface IMtpDeviceSource
{
    /// <summary>The devices connected now, with unique names (see <see cref="MtpDeviceNames.MakeUnique"/>).</summary>
    IReadOnlyList<MtpDeviceInfo> GetDevices();

    IMtpDevice Open(string deviceId);

    /// <summary>The next <see cref="GetDevices"/> looks for the devices again instead of using what it found last.</summary>
    void Rescan()
    {
    }
}

/// <summary>No portable devices (systems where MTP devices appear as folders, or no MTP support).</summary>
public sealed class NoMtpDevices : IMtpDeviceSource
{
    public static NoMtpDevices Instance { get; } = new();

    public IReadOnlyList<MtpDeviceInfo> GetDevices() => [];

    public IMtpDevice Open(string deviceId) => throw new DirectoryNotFoundException("No portable device: " + deviceId);
}

public static class MtpDeviceNames
{
    /// <summary>
    /// Names usable as the host of an <c>mtp://</c> path: no '/', '\' or ':'; two devices with the same name become
    /// "Name" and "Name (2)", in the system's order.
    /// </summary>
    public static IReadOnlyList<MtpDeviceInfo> MakeUnique(IEnumerable<MtpDeviceInfo> devices)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<MtpDeviceInfo>();
        foreach (var device in devices)
        {
            var name = Clean(device.Name);
            var unique = name;
            for (var i = 2; !used.Add(unique); i++)
            {
                unique = $"{name} ({i})";
            }
            result.Add(device with { Name = unique });
        }
        return result;
    }

    /// <summary>
    /// The name of a gvfs (GNOME) phone / camera folder: "mtp:host=SAMSUNG_Galaxy_S24" → "SAMSUNG Galaxy S24",
    /// "mtp:host=SAMSUNG_SAMSUNG_Android_R5CX15QTV5J" → "SAMSUNG Android" (the maker once, without the USB serial),
    /// "gphoto2:host=%5Busb%3A001%2C007%5D" → "Camera [usb:001,007]"; null for other gvfs mounts (smb, sftp ...).
    /// </summary>
    public static string? FromGvfsFolder(string folderName)
    {
        if (!folderName.StartsWith("mtp:", StringComparison.Ordinal) && !folderName.StartsWith("gphoto2:", StringComparison.Ordinal))
        {
            return null;
        }
        var host = folderName.Split(',').Select(p => p[(p.IndexOf(':', StringComparison.Ordinal) + 1)..])
            .FirstOrDefault(p => p.StartsWith("host=", StringComparison.Ordinal))?["host=".Length..];
        if (string.IsNullOrEmpty(host))
        {
            return folderName;
        }
        var name = Uri.UnescapeDataString(host);
        if (name.StartsWith('['))
        {
            return "Camera " + name; // gphoto2 hosts are USB addresses
        }
        // gvfs writes maker_model_serial: the maker repeats in Samsung's model name, the serial is no part of a name
        var words = name.Split(['_', ' '], StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && LooksLikeSerial(words[^1]))
        {
            words.RemoveAt(words.Count - 1);
        }
        for (var i = words.Count - 1; i > 0; i--)
        {
            if (string.Equals(words[i], words[i - 1], StringComparison.OrdinalIgnoreCase))
            {
                words.RemoveAt(i);
            }
        }
        return words.Count == 0 ? name : string.Join(' ', words);
    }

    /// <summary>
    /// The name in the "standard::display-name:" line of <c>gio info</c> for a gvfs device folder (what the desktop
    /// shows); null when missing or only the folder's own name.
    /// </summary>
    public static string? FromGioInfo(string output, string folderName)
    {
        const string key = "standard::display-name:";
        var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(key, StringComparison.Ordinal));
        var name = line?[key.Length..].Trim();
        return string.IsNullOrEmpty(name) || name == folderName || name.Contains("host=", StringComparison.Ordinal) ? null : Clean(name);
    }

    /// <summary>A USB serial number ("R5CX15QTV5J", "1A2B3C4D5E6F"): letters and digits, at least 8, with a digit.</summary>
    private static bool LooksLikeSerial(string word) =>
        word.Length >= 8 && word.All(char.IsLetterOrDigit) && word.Any(char.IsDigit);

    private static string Clean(string name)
    {
        var chars = name.Trim().Select(c => c is '/' or '\\' or ':' || char.IsControl(c) ? ' ' : c).ToArray();
        var cleaned = new string(chars).Trim();
        return cleaned.Length == 0 ? "Device" : cleaned;
    }
}
