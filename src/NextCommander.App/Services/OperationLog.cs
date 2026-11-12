using System.Globalization;
using NextCommander.Core.Config;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using Serilog;

namespace NextCommander.App.Services;

/// <summary>
/// Configuration > Log file: file operations appended to a text file (one line per item, errors after them), in the
/// form ("2026-11-07 12:00:00  Kopyala  C:\a.txt -> D:\").
/// </summary>
public static class OperationLog
{
    private static readonly Lock s_lock = new();

    public enum Kind
    {
        CopyMove,
        Delete,
        Pack,
        Rename,

        /// <summary>To / from / on a server (FTP, SFTP, WebDAV, S3).</summary>
        Remote,

        /// <summary>Copying out of an archive.</summary>
        Unpack,

        /// <summary>In a file system plugin.</summary>
        Plugin,
    }

    /// <summary>The schemes of the file system plugins loaded (their operations are logged as <see cref="Kind.Plugin"/>).</summary>
    public static HashSet<string> PluginSchemes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The log file when none is given (operations.log in the logs folder of the settings; null in tests).</summary>
    public static string? DefaultFile { get; set; }

    /// <summary>The file the operations go to; null when the log is off.</summary>
    public static string? FileOf(AppSettings settings) =>
        !settings.LogEnabled ? null
        : !string.IsNullOrWhiteSpace(settings.LogFile) ? Environment.ExpandEnvironmentVariables(settings.LogFile.Trim())
        : DefaultFile;

    public static bool Wants(AppSettings settings, Kind kind) =>
        FileOf(settings) is not null && kind switch
        {
            Kind.CopyMove => settings.LogCopyMove,
            Kind.Delete => settings.LogDelete,
            Kind.Pack => settings.LogPack,
            Kind.Remote => settings.LogRemote,
            Kind.Unpack => settings.LogUnpack,
            Kind.Plugin => settings.LogPlugins,
            _ => settings.LogRename,
        };

    private static bool IsRemote(VfsPath path) => path.LocalAnchor is null && path.Scheme != VfsPath.ArchiveScheme;

    /// <summary>
    /// Kind of a finished job, null for jobs that are not logged: transfers and deleting on servers are "remote",
    /// copying out of an archive is "unpack", the rest by the job.
    /// </summary>
    public static Kind? KindOf(FileJob job, IReadOnlyList<VfsPath> sources, VfsPath? target)
    {
        if (job is CopyMoveJob or DeleteJob && (sources.Any(p => PluginSchemes.Contains(p.Scheme)) || (target is not null && PluginSchemes.Contains(target.Scheme))))
        {
            return Kind.Plugin;
        }
        if (job is CopyMoveJob or DeleteJob && (sources.Any(IsRemote) || (target is not null && IsRemote(target))))
        {
            return Kind.Remote;
        }
        if (job is CopyMoveJob && sources.Count > 0 && sources.All(s => s.Scheme == VfsPath.ArchiveScheme)
            && target is not null && target.Scheme != VfsPath.ArchiveScheme)
        {
            return Kind.Unpack;
        }
        return KindOf(job);
    }

    /// <summary>Kind of a finished job, null for jobs that are not logged.</summary>
    public static Kind? KindOf(FileJob job) => job switch
    {
        CopyMoveJob => Kind.CopyMove,
        DeleteJob => Kind.Delete,
        NextCommander.Vfs.Archive.PackJob => Kind.Pack,
        NextCommander.Core.Rename.MultiRenameJob => Kind.Rename,
        _ => null,
    };

    /// <summary>Logs a finished job: its items (unless "errors only") and its errors.</summary>
    public static void Job(AppSettings settings, FileJob job, IReadOnlyList<VfsPath> sources, VfsPath? targetPath)
    {
        if (KindOf(job, sources, targetPath) is not { } kind || !Wants(settings, kind))
        {
            return;
        }
        var items = sources.Select(s => s.ToDisplayString());
        var target = targetPath?.ToDisplayString();
        var lines = new List<string>();
        var arrow = target is null ? string.Empty : " -> " + target;
        if (!settings.LogErrorsOnly && job.State is JobState.Completed or JobState.CompletedWithErrors)
        {
            lines.AddRange(items.Select(i => Line(job.Title, i + arrow)));
        }
        lines.AddRange(job.Errors.Select(e => Line(job.Title, $"ERROR {e.Item}: {e.Message}")));
        if (settings.LogSkipped)
        {
            lines.AddRange(job.Skipped.Select(s => Line(job.Title, "SKIPPED " + s)));
        }
        if (job.State == JobState.Failed && job.Failure is not null)
        {
            lines.Add(Line(job.Title, "ERROR " + job.Failure.Message));
        }
        Append(settings, lines);
    }

    /// <summary>One operation done outside the job queue (rename).</summary>
    public static void Single(AppSettings settings, Kind kind, string operation, string text, bool failed = false)
    {
        if (Wants(settings, kind) && (failed || !settings.LogErrorsOnly))
        {
            Append(settings, [Line(operation, (failed ? "ERROR " : string.Empty) + text)]);
        }
    }

    private static string Line(string operation, string text) =>
        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + operation + "  " + text;

    /// <summary>
    /// Configuration > Log file > rotation: a log of an earlier day becomes "name-yyyyMMdd.ext" (daily), a log larger than the
    /// limit becomes "name-yyyyMMdd-HHmmss.ext" (by size); only the newest <see cref="AppSettings.LogKeepCopies"/> old files stay.
    /// </summary>
    public static void Rotate(AppSettings settings, string file, DateTime now)
    {
        if (settings.LogRotation == LogRotation.Never || !File.Exists(file))
        {
            return;
        }
        var info = new FileInfo(file);
        string? suffix = settings.LogRotation switch
        {
            LogRotation.Daily when info.LastWriteTime.Date < now.Date => info.LastWriteTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            LogRotation.BySize when info.Length > Math.Max(1, settings.LogMaxSizeKb) * 1024L => now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture),
            _ => null,
        };
        if (suffix is null)
        {
            return;
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        var name = Path.GetFileNameWithoutExtension(file);
        var ext = Path.GetExtension(file);
        var target = Path.Combine(dir, $"{name}-{suffix}{ext}");
        for (var n = 2; File.Exists(target); n++)
        {
            target = Path.Combine(dir, $"{name}-{suffix}-{n}{ext}");
        }
        File.Move(file, target);
        var old = Directory.EnumerateFiles(dir, $"{name}-*{ext}").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(Math.Max(0, settings.LogKeepCopies));
        foreach (var path in old)
        {
            File.Delete(path);
        }
    }

    private static void Append(AppSettings settings, IReadOnlyCollection<string> lines)
    {
        if (lines.Count == 0 || FileOf(settings) is not { } file)
        {
            return;
        }
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(file)) is { } dir)
            {
                Directory.CreateDirectory(dir);
            }
            lock (s_lock)
            {
                Rotate(settings, file, DateTime.Now);
                File.AppendAllLines(file, lines);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Warning(ex, "Could not write the operation log");
        }
    }
}
