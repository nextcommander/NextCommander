using NextCommander.Core.Config;
using NextCommander.Core.Search;

namespace NextCommander.App.Services;

/// <summary>
/// Runs NextCommander's own file index (Configuration > Operation > File index): started with the main window, rebuilt
/// when its roots or exclusions change, kept in &lt;settings&gt;/index/files.idx. File search, the duplicate finder and
/// folder sizes use it when it is ready (<see cref="FileIndexes.Local"/>).
/// </summary>
public static class FileIndexService
{
    private static string? s_settingsDirectory;
    private static string? s_configuration;

    /// <summary>The running index (null: switched off).</summary>
    public static LocalFileIndex? Current => FileIndexes.Local;

    /// <summary>The folders indexed when none are chosen: the fixed drives (Windows) or the home folder.</summary>
    public static IReadOnlyList<string> DefaultRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
        }
        return [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)];
    }

    /// <summary>The chosen roots ("; " separated in the settings), or the defaults.</summary>
    public static IReadOnlyList<string> RootsOf(AppSettings settings)
    {
        var chosen = Split(settings.FileIndexRoots);
        return chosen.Count > 0 ? chosen : DefaultRoots();
    }

    private static List<string> Split(string? text) =>
        (text ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Starts, restarts (other roots / exclusions) or stops the index as the settings say.</summary>
    public static void Apply(AppSettings settings, string? settingsDirectory = null)
    {
        s_settingsDirectory = settingsDirectory ?? s_settingsDirectory;
        if (s_settingsDirectory is null)
        {
            return;
        }
        var roots = RootsOf(settings);
        var configuration = settings.FileIndexEnabled
            ? string.Join("|", roots) + "#" + settings.FileIndexExcluded + "#" + settings.FileIndexUseChangeJournal + "#" + settings.FileIndexFullCheckHours
            : null;
        if (configuration == s_configuration)
        {
            return;
        }
        s_configuration = configuration;
        Stop();
        if (configuration is null)
        {
            return;
        }
        var index = new LocalFileIndex(roots, Path.Combine(s_settingsDirectory, "index", "files.idx"), Split(settings.FileIndexExcluded))
        {
            UseChangeJournal = settings.FileIndexUseChangeJournal,
            FullCheckInterval = TimeSpan.FromHours(Math.Max(0, settings.FileIndexFullCheckHours)),
        };
        FileIndexes.Local = index;
        _ = index.StartAsync();
    }

    /// <summary>Saves and closes the index (on exit, or when it is switched off).</summary>
    public static void Stop()
    {
        var index = FileIndexes.Local;
        FileIndexes.Local = null;
        index?.Dispose();
    }
}
