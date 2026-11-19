using System.Text.Json;
using System.Text.Json.Serialization;

namespace NextCommander.Core.Config;

/// <summary>One kept configuration: the file, when it was kept, what it holds, whether it is the one in use, whether it still loads.</summary>
public sealed record SettingsBackup(string Path, DateTime Time, int Connections, int HotlistEntries, bool IsCurrent, bool Damaged);

/// <summary>
/// Loads/saves <see cref="AppSettings"/> as JSON. Location:
/// portable mode (a <c>portable.json</c> next to the executable) → the executable's folder;
/// otherwise the platform settings folder (%APPDATA%, ~/.config, ~/Library/Application Support).
/// </summary>
public sealed class SettingsStore
{
    public const string AppFolderName = "NextCommander";

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        // Readable Turkish in the file ("Arşivler", not "Arşivler"); it is a local file, never HTML.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonSerializerOptions JsonOptions => s_json;

    public SettingsStore(string? directory = null)
    {
        Directory = directory ?? ResolveDirectory();
    }

    public string Directory { get; }

    public string FilePath => Path.Combine(Directory, "settings.json");

    public static string ResolveDirectory()
    {
        var exeDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(exeDir, "portable.json")))
        {
            return exeDir;
        }
        string root;
        if (OperatingSystem.IsMacOS())
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
        }
        else if (OperatingSystem.IsWindows())
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            root = string.IsNullOrEmpty(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdg;
        }
        var folder = Path.Combine(root, AppFolderName);
        CopyLegacyFolder(Path.Combine(root, LegacyAppFolderName), folder);
        return folder;
    }

    /// <summary>The settings folder of the program's former name (FileManager); copied once to the new one.</summary>
    public const string LegacyAppFolderName = "FileManager";

    /// <summary>
    /// First start under the new name: the settings, saved passwords, own themes and languages of the old folder are
    /// copied (the old folder stays as it was).
    /// </summary>
    public static bool CopyLegacyFolder(string legacy, string folder)
    {
        if (System.IO.Directory.Exists(folder) || !File.Exists(Path.Combine(legacy, "settings.json")))
        {
            return false;
        }
        try
        {
            foreach (var source in System.IO.Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(legacy, source);
                if (relative.StartsWith("logs", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("thumbs", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // logs and the thumbnail cache are not worth copying
                }
                var target = Path.Combine(folder, relative);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: false);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>How many configurations that loaded are kept in <see cref="BackupDirectory"/> (user's request, 19 November 2026).</summary>
    public const int BackupCount = 7;

    /// <summary>The backups of the settings: the last <see cref="BackupCount"/> different settings files that loaded.</summary>
    public string BackupDirectory => Path.Combine(Directory, "backups");

    /// <summary>The backup the settings were restored from at the last <see cref="Load"/> (the file was damaged), or null.</summary>
    public string? RestoredBackup { get; private set; }

    /// <summary>
    /// Reads the settings. A file that loads is kept among the backups (when it differs from the newest one); a damaged
    /// one is set aside (.corrupt) and the newest backup that loads takes its place (<see cref="RestoredBackup"/>);
    /// without one the defaults. <paramref name="backups"/>: false for a quick look (nothing written, nothing restored).
    /// </summary>
    public AppSettings Load(bool backups = true)
    {
        RestoredBackup = null;
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = Read(FilePath);
                if (backups)
                {
                    KeepBackup();
                }
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // Corrupt or unreadable settings: set aside, then the newest backup that loads (or the defaults).
            if (!backups)
            {
                return new AppSettings();
            }
            TryBackupCorrupt();
            if (RestoreBackup() is { } restored)
            {
                return restored;
            }
        }
        return new AppSettings();
    }

    /// <summary>Reads a settings file, converting what earlier versions wrote.</summary>
    private AppSettings Read(string path)
    {
        // command names and the folder variable of earlier versions are converted (saved with the new names)
        var raw = File.ReadAllText(path);
        var settings = JsonSerializer.Deserialize<AppSettings>(Commands.LegacyNames.Settings(raw), s_json) ?? new AppSettings();
        if (!raw.Contains("\"SettingsVersion\"", StringComparison.Ordinal))
        {
            settings.SettingsVersion = 1; // written before versions existed
        }
        if (settings.SettingsVersion < 3 && !raw.Contains("\"ColorThemeFile\"", StringComparison.Ordinal))
        {
            settings.ColorThemeFile = string.Empty; // before theme files: the system colors (the default is "follow the system" now)
        }
        if (settings.SettingsVersion < 3 && string.IsNullOrEmpty(settings.ColorThemeFile) && UsedDarkColors(raw))
        {
            settings.ColorThemeFile = ColorTheme.DarkThemeId; // dark mode and dark Office schemes are a theme file now
        }
        if (settings.SettingsVersion < 4 && string.Equals(settings.ColorThemeFile, "koyu", StringComparison.OrdinalIgnoreCase)
            && !File.Exists(Path.Combine(Directory, "themes", "koyu" + ColorTheme.Extension)))
        {
            settings.ColorThemeFile = ColorTheme.DarkThemeId; // the shipped dark theme was renamed (unless the user has an own "koyu")
        }
        settings.Upgrade();
        return settings;
    }

    /// <summary>The backups, the newest first ("settings-20261119-073512-120.json").</summary>
    public IReadOnlyList<string> Backups()
    {
        if (!System.IO.Directory.Exists(BackupDirectory))
        {
            return [];
        }
        return [.. System.IO.Directory.GetFiles(BackupDirectory, "settings-*.json").OrderByDescending(Path.GetFileName, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The settings file that has just loaded joins the backups, unless the newest backup is the same; only the newest
    /// <see cref="BackupCount"/> are kept. A backup that cannot be written does not keep the program from starting.
    /// </summary>
    private void KeepBackup()
    {
        try
        {
            var text = File.ReadAllText(FilePath);
            var backups = Backups();
            if (backups.Count > 0 && File.ReadAllText(backups[0]) == text)
            {
                return; // nothing changed since the last one
            }
            System.IO.Directory.CreateDirectory(BackupDirectory);
            // the name sorts by time ("~" after "." keeps a second one of the same millisecond after the first)
            var name = "settings-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
            var target = Path.Combine(BackupDirectory, name + ".json");
            for (var n = 2; File.Exists(target); n++)
            {
                target = Path.Combine(BackupDirectory, $"{name}~{n}.json");
            }
            File.WriteAllText(target, text);
            foreach (var old in Backups().Skip(BackupCount))
            {
                File.Delete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // no backup this time
        }
    }

    /// <summary>
    /// The backups to choose from, the newest first: when each was kept, its connections and Hotlist entries, whether it
    /// is the settings file as it is now, whether it no longer loads.
    /// </summary>
    public IReadOnlyList<SettingsBackup> BackupList()
    {
        string? current = null;
        try
        {
            current = File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // compared with nothing
        }
        var list = new List<SettingsBackup>();
        foreach (var backup in Backups())
        {
            var time = File.GetLastWriteTime(backup);
            try
            {
                var settings = Read(backup);
                list.Add(new SettingsBackup(backup, time, settings.Connections.Count, settings.Hotlist.Count, File.ReadAllText(backup) == current, Damaged: false));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
                list.Add(new SettingsBackup(backup, time, 0, 0, IsCurrent: false, Damaged: true));
            }
        }
        return list;
    }

    /// <summary>
    /// Configuration > Restore settings from a backup: the backup becomes the settings file (it must load); the settings
    /// file as it is now joins the backups first, so the step can be undone. The program starts again after it.
    /// </summary>
    public void RestoreFrom(string backup)
    {
        var text = File.ReadAllText(backup); // read first: keeping the current file may drop the oldest backup
        _ = Read(backup); // throws when it does not load
        if (File.Exists(FilePath))
        {
            KeepBackup();
        }
        System.IO.Directory.CreateDirectory(Directory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary>The newest backup that loads, copied over the damaged settings file; null when none loads.</summary>
    private AppSettings? RestoreBackup()
    {
        foreach (var backup in Backups())
        {
            try
            {
                var settings = Read(backup);
                File.Copy(backup, FilePath, overwrite: true);
                RestoredBackup = backup;
                return settings;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
                // damaged too: the one before
            }
        }
        return null;
    }

    /// <summary>A file of version 2 or older with "Theme": "Dark" or a dark Office scheme.</summary>
    private static bool UsedDarkColors(string text) =>
        text.Contains("\"Theme\": \"Dark\"", StringComparison.Ordinal)
        || text.Contains("\"ColorScheme\": \"OfficeDarkGray\"", StringComparison.Ordinal)
        || text.Contains("\"ColorScheme\": \"OfficeBlack\"", StringComparison.Ordinal);

    public void Save(AppSettings settings)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temp = FilePath + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, settings, s_json);
        }
        File.Move(temp, FilePath, overwrite: true);
    }

    private void TryBackupCorrupt()
    {
        try
        {
            File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ignore
        }
    }
}
