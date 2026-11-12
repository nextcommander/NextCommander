using System.Text.Json;
using System.Text.Json.Serialization;

namespace NextCommander.Core.Config;

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

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                // command names and the folder variable of earlier versions are converted (saved with the new names)
                var text = Commands.LegacyNames.Settings(File.ReadAllText(FilePath));
                var settings = JsonSerializer.Deserialize<AppSettings>(text, s_json) ?? new AppSettings();
                if (!ReadsVersion(FilePath))
                {
                    settings.SettingsVersion = 1; // written before versions existed
                }
                if (settings.SettingsVersion < 3 && !File.ReadAllText(FilePath).Contains("\"ColorThemeFile\"", StringComparison.Ordinal))
                {
                    settings.ColorThemeFile = string.Empty; // before theme files: the system colors (the default is "follow the system" now)
                }
                if (settings.SettingsVersion < 3 && string.IsNullOrEmpty(settings.ColorThemeFile) && UsedDarkColors(FilePath))
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
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings: keep a backup and start fresh.
            TryBackupCorrupt();
        }
        return new AppSettings();
    }

    /// <summary>A file of version 2 or older with "Theme": "Dark" or a dark Office scheme.</summary>
    private static bool UsedDarkColors(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            return text.Contains("\"Theme\": \"Dark\"", StringComparison.Ordinal)
                || text.Contains("\"ColorScheme\": \"OfficeDarkGray\"", StringComparison.Ordinal)
                || text.Contains("\"ColorScheme\": \"OfficeBlack\"", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool ReadsVersion(string path)
    {
        try
        {
            return File.ReadAllText(path).Contains("\"SettingsVersion\"", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return true;
        }
    }

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
