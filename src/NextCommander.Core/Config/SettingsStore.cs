using System.Text.Json;
using System.Text.Json.Serialization;

namespace NextCommander.Core.Config;

/// <summary>One kept configuration: the file, when it was kept, what it holds, whether it is the one in use, whether it still loads.</summary>
public sealed record SettingsBackup(string Path, DateTime Time, int Connections, int HotlistEntries, bool IsCurrent, bool Damaged)
{
    /// <summary>Why it was made: "start", "manual", "file", "before:&lt;action&gt;" (<see cref="SettingsBackupInfo.Trigger"/>).</summary>
    public string Trigger { get; init; } = "start";

    /// <summary>The name the user gave it (null: none).</summary>
    public string? Label { get; init; }

    /// <summary>Never removed automatically.</summary>
    public bool Pinned { get; init; }

    /// <summary>The settings it holds (null when it does not load).</summary>
    public AppSettings? Settings { get; init; }

    /// <summary>Made by the user (Back up now) or added from a file: kept until deleted.</summary>
    public bool IsOwn => Trigger is "manual" or "file";
}

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

    /// <summary>
    /// How many automatic backups (the configurations that loaded, those made before a restore or an import) are kept in
    /// <see cref="BackupDirectory"/> (user's request, 19 November 2026); the user's own and pinned ones are kept until deleted.
    /// </summary>
    public const int BackupCount = 7;

    /// <summary>What is noted about the backups (why, a name, pinned): backups/index.json, by file name.</summary>
    private string IndexPath => Path.Combine(BackupDirectory, "index.json");

    private Dictionary<string, SettingsBackupInfo> ReadIndex()
    {
        try
        {
            if (File.Exists(IndexPath))
            {
                return JsonSerializer.Deserialize<Dictionary<string, SettingsBackupInfo>>(File.ReadAllText(IndexPath), s_json)
                    ?? new Dictionary<string, SettingsBackupInfo>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // what was noted is lost; the backups stay
        }
        return new Dictionary<string, SettingsBackupInfo>(StringComparer.OrdinalIgnoreCase);
    }

    private void WriteIndex(Dictionary<string, SettingsBackupInfo> index)
    {
        var present = Backups().Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in index.Keys.Where(k => !present.Contains(k)).ToList())
        {
            index.Remove(gone);
        }
        System.IO.Directory.CreateDirectory(BackupDirectory);
        File.WriteAllText(IndexPath, JsonSerializer.Serialize(index, s_json));
    }

    /// <summary>What is noted about a backup (a default for one without a note: kept at a start).</summary>
    public SettingsBackupInfo InfoOf(string backup) =>
        ReadIndex().TryGetValue(Path.GetFileName(backup), out var info) ? info : new SettingsBackupInfo();

    /// <summary>Gives a backup a name (empty: none).</summary>
    public void SetLabel(string backup, string? label)
    {
        var index = ReadIndex();
        var info = index.TryGetValue(Path.GetFileName(backup), out var known) ? known : new SettingsBackupInfo();
        info.Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        index[Path.GetFileName(backup)] = info;
        WriteIndex(index);
    }

    /// <summary>Pins a backup (never removed automatically) or unpins it.</summary>
    public void SetPinned(string backup, bool pinned)
    {
        var index = ReadIndex();
        var info = index.TryGetValue(Path.GetFileName(backup), out var known) ? known : new SettingsBackupInfo();
        info.Pinned = pinned;
        index[Path.GetFileName(backup)] = info;
        WriteIndex(index);
    }

    /// <summary>Deletes a backup; a pinned one is refused (unpin it first).</summary>
    public void DeleteBackup(string backup)
    {
        if (InfoOf(backup).Pinned)
        {
            throw new InvalidOperationException("Unpin it first: pinned backups are protected.");
        }
        File.Delete(backup);
        WriteIndex(ReadIndex());
    }

    /// <summary>A deleted backup back (its text and what was noted about it; the window's Undo).</summary>
    public void PutBack(string backup, string text, SettingsBackupInfo info)
    {
        System.IO.Directory.CreateDirectory(BackupDirectory);
        File.WriteAllText(backup, text);
        var index = ReadIndex();
        index[Path.GetFileName(backup)] = info;
        WriteIndex(index);
    }

    /// <summary>The settings as they are now kept as a backup by the user (Back up now), with a name or not; its file.</summary>
    public string? BackupNow(string? label = null) => KeepBackup("manual", label, evenIfSame: true);

    /// <summary>The settings file as it is now kept before an action ("Import", "Restore"), unless the newest backup is the same.</summary>
    public string? BackupBefore(string action) => KeepBackup("before:" + action);

    /// <summary>A settings file chosen by the user joins the backups (it must load), named after the file; its backup.</summary>
    public string AddBackupFromFile(string file)
    {
        _ = Read(file); // throws when it does not load
        var target = NewBackupPath();
        System.IO.Directory.CreateDirectory(BackupDirectory);
        File.Copy(file, target);
        var index = ReadIndex();
        index[Path.GetFileName(target)] = new SettingsBackupInfo { Trigger = "file", Label = Path.GetFileName(file) };
        WriteIndex(index);
        return target;
    }

    /// <summary>The settings of a backup (throws when it does not load).</summary>
    public AppSettings ReadBackup(string backup) => Read(backup);

    /// <summary>
    /// Restores the chosen parts of a backup into the settings file: the settings as they are now (<paramref name="current"/>,
    /// the running program's) are kept as a backup first ("before:Restore"), so a restore can be undone the same way. The
    /// program starts again after it.
    /// </summary>
    public void RestoreParts(string backup, IReadOnlyCollection<SettingsPart> parts, AppSettings current)
    {
        var from = Read(backup); // throws when it does not load
        Save(current);
        KeepBackup("before:Restore");
        Save(SettingsDiff.Merge(current, from, parts));
    }

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
                    KeepBackup("start");
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

    /// <summary>A new backup's file: the name sorts by time ("~" after "." keeps a second one of the same millisecond after the first).</summary>
    private string NewBackupPath()
    {
        var name = "settings-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        var target = Path.Combine(BackupDirectory, name + ".json");
        for (var n = 2; File.Exists(target); n++)
        {
            target = Path.Combine(BackupDirectory, $"{name}~{n}.json");
        }
        return target;
    }

    /// <summary>
    /// The settings file as it is now joins the backups (<paramref name="trigger"/>: why), unless the newest backup is the
    /// same (<paramref name="evenIfSame"/>: the user's own); of the automatic ones only the newest <see cref="BackupCount"/>
    /// are kept, the user's own and pinned ones stay. A backup that cannot be written does not keep the program from
    /// starting. The file made, or null.
    /// </summary>
    private string? KeepBackup(string trigger, string? label = null, bool evenIfSame = false)
    {
        try
        {
            var text = File.ReadAllText(FilePath);
            var backups = Backups();
            if (!evenIfSame && backups.Count > 0 && File.ReadAllText(backups[0]) == text)
            {
                return null; // nothing changed since the last one
            }
            System.IO.Directory.CreateDirectory(BackupDirectory);
            var target = NewBackupPath();
            File.WriteAllText(target, text);
            var index = ReadIndex();
            if (trigger != "start" || label is not null)
            {
                index[Path.GetFileName(target)] = new SettingsBackupInfo { Trigger = trigger, Label = label };
            }
            var automatic = Backups().Where(b => !index.TryGetValue(Path.GetFileName(b), out var info) || (!info.Pinned && info.Trigger is not ("manual" or "file")));
            foreach (var old in automatic.Skip(BackupCount))
            {
                File.Delete(old);
            }
            WriteIndex(index);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // no backup this time
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
        var index = ReadIndex();
        foreach (var backup in Backups())
        {
            var time = File.GetLastWriteTime(backup);
            var info = index.TryGetValue(Path.GetFileName(backup), out var known) ? known : new SettingsBackupInfo();
            try
            {
                var settings = Read(backup);
                list.Add(new SettingsBackup(backup, time, settings.Connections.Count, settings.Hotlist.Count, File.ReadAllText(backup) == current, Damaged: false)
                {
                    Trigger = info.Trigger, Label = info.Label, Pinned = info.Pinned, Settings = settings,
                });
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
                list.Add(new SettingsBackup(backup, time, 0, 0, IsCurrent: false, Damaged: true) { Trigger = info.Trigger, Label = info.Label, Pinned = info.Pinned });
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
            KeepBackup("before:Restore");
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
