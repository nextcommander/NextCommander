using System.Text.Json;

namespace NextCommander.Platform;

/// <summary>
/// The local folders of cloud sync clients (OneDrive, iCloud Drive, Dropbox, Google Drive for desktop, Nextcloud, Box,
/// MEGA ...), found where the clients keep them; shown in the Computer view and, as an option, on the drive bar.
/// </summary>
public static class SyncFolders
{
    /// <summary>The sync folders of the current user that exist.</summary>
    public static IReadOnlyList<(string Name, string Path)> Find() =>
        Locate(KnownFolders.Home, Environment.GetEnvironmentVariable, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS());

    /// <summary>The sync folders under <paramref name="home"/> with the environment of <paramref name="env"/> (tests give their own).</summary>
    public static IReadOnlyList<(string Name, string Path)> Locate(string home, Func<string, string?> env, bool windows, bool mac)
    {
        var found = new List<(string Name, string Path)>();
        void Add(string name, string? path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)
                && !found.Any(f => string.Equals(Path.TrimEndingDirectorySeparator(f.Path), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase)))
            {
                found.Add((name, path));
            }
        }

        if (windows)
        {
            // the OneDrive client sets these for the personal and the work / school account
            Add("OneDrive", env("OneDriveConsumer"));
            if (env("OneDriveCommercial") is { Length: > 0 } work)
            {
                Add(Path.GetFileName(Path.TrimEndingDirectorySeparator(work)), work); // "OneDrive - Organization"
            }
            Add("OneDrive", env("OneDrive"));
            Add("iCloud Drive", Path.Combine(home, "iCloudDrive"));
            Add("Google Drive", Path.Combine(home, "Google Drive")); // Backup and Sync (Drive for desktop is a drive letter)
        }
        if (mac)
        {
            Add("iCloud Drive", Path.Combine(home, "Library", "Mobile Documents", "com~apple~CloudDocs"));
            // File Provider clients: ~/Library/CloudStorage/GoogleDrive-me@gmail.com, OneDrive-Personal, Dropbox, Box-Box ...
            var cloudStorage = Path.Combine(home, "Library", "CloudStorage");
            if (Directory.Exists(cloudStorage))
            {
                foreach (var folder in Directory.GetDirectories(cloudStorage).Order(StringComparer.OrdinalIgnoreCase))
                {
                    Add(CloudStorageName(Path.GetFileName(folder)), folder);
                }
            }
        }
        foreach (var (name, path) in DropboxFolders(home, env, windows))
        {
            Add(name, path);
        }
        // clients that use a folder in the home folder on every system
        foreach (var name in new[] { "OneDrive", "Nextcloud", "ownCloud", "Box", "MEGA", "pCloudDrive" })
        {
            Add(name, Path.Combine(home, name));
        }
        return found;
    }

    /// <summary>The shown name of a ~/Library/CloudStorage folder: "GoogleDrive-me@x.com" → "Google Drive (me@x.com)", "OneDrive-Personal" → "OneDrive".</summary>
    public static string CloudStorageName(string folder)
    {
        var dash = folder.IndexOf('-', StringComparison.Ordinal);
        var product = dash < 0 ? folder : folder[..dash];
        var account = dash < 0 ? string.Empty : folder[(dash + 1)..];
        product = product switch
        {
            "GoogleDrive" => "Google Drive",
            "OneDrive" => "OneDrive",
            _ => product,
        };
        if (account.Length == 0 || account == "Personal" || string.Equals(account, product, StringComparison.OrdinalIgnoreCase))
        {
            return product;
        }
        return product == "OneDrive" ? $"OneDrive - {account}" : $"{product} ({account})";
    }

    /// <summary>Dropbox writes where its folders are to info.json: {"personal": {"path": ...}, "business": {"path": ...}}.</summary>
    private static IEnumerable<(string Name, string Path)> DropboxFolders(string home, Func<string, string?> env, bool windows)
    {
        var candidates = windows
            ? new[] { env("APPDATA"), env("LOCALAPPDATA") }.Where(d => !string.IsNullOrEmpty(d)).Select(d => Path.Combine(d!, "Dropbox", "info.json"))
            : [Path.Combine(home, ".dropbox", "info.json")];
        foreach (var file in candidates)
        {
            if (!File.Exists(file))
            {
                continue;
            }
            List<(string, string)> folders = [];
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var (key, name) in new[] { ("personal", "Dropbox"), ("business", "Dropbox (business)") })
                {
                    if (document.RootElement.TryGetProperty(key, out var account) && account.TryGetProperty("path", out var path)
                        && path.GetString() is { Length: > 0 } folder)
                    {
                        folders.Add((name, folder));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                continue;
            }
            return folders;
        }
        return [];
    }
}
