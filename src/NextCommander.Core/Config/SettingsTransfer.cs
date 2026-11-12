using System.IO.Compression;
using System.Text.Json;

namespace NextCommander.Core.Config;

/// <summary>
/// Export and import of the settings (Configuration > Export / Import settings): one zip with settings.json
/// (options, connections, Hotlist, shortcuts, button bars ...) and the user's own themes, icon packs and language
/// files, to set up a new installation the same way. Saved passwords go along only when asked for; they can be read
/// on another computer only when a master password protects them.
/// </summary>
public static class SettingsTransfer
{
    /// <summary>Describes the export (when, from which system); also marks a zip as a settings export.</summary>
    public const string ManifestName = "nextcommander-export.json";

    public const string SettingsFile = "settings.json";
    public const string SecretsFile = "secrets.json";
    public const string SecretsKeyFile = "secrets.key";

    /// <summary>Folders of the settings folder that go along: the user's own themes, icon packs and languages.</summary>
    public static readonly IReadOnlyList<string> Folders = ["themes", "iconpacks", "languages"];

    /// <summary>What an export holds (shown before importing it).</summary>
    public sealed record Summary(DateTime? Exported, string? Machine, int Connections, int HotlistEntries, int UserFiles, bool HasPasswords);

    private sealed record Manifest(int Version, DateTime Exported, string Machine, string System);

    /// <summary>
    /// Writes the settings folder's settings (and with <paramref name="includePasswords"/> secrets.json; with
    /// <paramref name="includeMachineKey"/> also the key that only works on this computer, for a backup) into a zip.
    /// </summary>
    public static void Export(string settingsDirectory, string zipPath, bool includePasswords, bool includeMachineKey = false)
    {
        var settings = Path.Combine(settingsDirectory, SettingsFile);
        if (!File.Exists(settings))
        {
            throw new FileNotFoundException("There are no settings to export yet.", settings);
        }
        var folder = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
        Directory.CreateDirectory(folder);
        var temporary = zipPath + ".part";
        using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
        {
            var manifest = new Manifest(1, DateTime.UtcNow, Environment.MachineName, Environment.OSVersion.ToString());
            using (var writer = new StreamWriter(zip.CreateEntry(ManifestName).Open()))
            {
                writer.Write(JsonSerializer.Serialize(manifest, SettingsStore.JsonOptions));
            }
            zip.CreateEntryFromFile(settings, SettingsFile);
            var files = new List<string>();
            if (includePasswords)
            {
                files.Add(SecretsFile);
            }
            if (includeMachineKey)
            {
                files.Add(SecretsKeyFile);
            }
            foreach (var name in files.Where(f => File.Exists(Path.Combine(settingsDirectory, f))))
            {
                zip.CreateEntryFromFile(Path.Combine(settingsDirectory, name), name);
            }
            foreach (var sub in Folders)
            {
                var source = Path.Combine(settingsDirectory, sub);
                if (!Directory.Exists(source))
                {
                    continue;
                }
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    zip.CreateEntryFromFile(file, sub + "/" + Path.GetRelativePath(source, file).Replace('\\', '/'));
                }
            }
        }
        File.Move(temporary, zipPath, overwrite: true);
    }

    /// <summary>Reads what an export holds; <see cref="InvalidDataException"/> when the file is not a settings export.</summary>
    public static Summary Inspect(string zipPath)
    {
        using var zip = OpenExport(zipPath);
        var settings = ReadSettings(zip);
        Manifest? manifest = null;
        if (zip.GetEntry(ManifestName) is { } entry)
        {
            using var stream = entry.Open();
            try
            {
                manifest = JsonSerializer.Deserialize<Manifest>(stream, SettingsStore.JsonOptions);
            }
            catch (JsonException)
            {
                // a broken manifest only loses the date
            }
        }
        var userFiles = zip.Entries.Count(e => Folders.Any(f => e.FullName.StartsWith(f + "/", StringComparison.Ordinal)) && e.Name.Length > 0);
        return new Summary(manifest?.Exported, manifest?.Machine, settings.Connections.Count, settings.Hotlist.Count, userFiles,
            zip.GetEntry(SecretsFile) is not null);
    }

    /// <summary>
    /// Puts an export into the settings folder: the current settings are first kept in
    /// &lt;settings&gt;/backups/settings-&lt;time&gt;.zip (returned). Files of the user's folders with the same name are
    /// replaced, others stay. The running program has to start again to use them.
    /// </summary>
    public static string Import(string zipPath, string settingsDirectory)
    {
        using var zip = OpenExport(zipPath);
        ReadSettings(zip); // refuse anything that is not a settings export before touching the folder
        var root = Path.GetFullPath(settingsDirectory);
        var backup = Path.Combine(root, "backups", $"settings-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (File.Exists(Path.Combine(root, SettingsFile)))
        {
            Export(root, backup, includePasswords: true, includeMachineKey: true);
        }
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var allowed = name is SettingsFile or SecretsFile or SecretsKeyFile
                || (entry.Name.Length > 0 && Folders.Any(f => name.StartsWith(f + "/", StringComparison.Ordinal)));
            if (!allowed)
            {
                continue;
            }
            var target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue; // "../" in a name: never outside the settings folder
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
        if (zip.GetEntry(SecretsFile) is not null && zip.GetEntry(SecretsKeyFile) is null)
        {
            // passwords of another computer come with their master password; this computer's key does not open them
            File.Delete(Path.Combine(root, SecretsKeyFile));
        }
        return backup;
    }

    private static ZipArchive OpenExport(string zipPath)
    {
        try
        {
            return ZipFile.OpenRead(zipPath);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("The file is not a NextCommander settings export.");
        }
    }

    private static AppSettings ReadSettings(ZipArchive zip)
    {
        if (zip.GetEntry(SettingsFile) is not { } entry)
        {
            throw new InvalidDataException("The file is not a NextCommander settings export.");
        }
        using var stream = entry.Open();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(stream, SettingsStore.JsonOptions)
                ?? throw new InvalidDataException("The settings in the file are empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(string.Format(System.Globalization.CultureInfo.CurrentCulture, "The settings in the file cannot be read: {0}", ex.Message), ex);
        }
    }
}
