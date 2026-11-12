using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace NextCommander.Core.Search;

/// <summary>
/// An operating system file index used to speed up name searches: Everything (es.exe) on Windows, plocate / locate on
/// Linux, Spotlight (mdfind) on macOS. It only proposes candidates; the searcher still checks every criterion.
/// </summary>
public interface IFileIndex
{
    string Name { get; }

    bool IsAvailable { get; }

    /// <summary>Full paths of files and folders below <paramref name="root"/> whose names may match <paramref name="patterns"/>
    /// (simple wildcards like "*.txt"; empty: everything below root).</summary>
    /// <exception cref="IOException">The index cannot be queried (service not running...).</exception>
    IAsyncEnumerable<string> QueryAsync(string root, IReadOnlyList<string> patterns, CancellationToken cancellationToken);
}

public static class FileIndexes
{
    private static IFileIndex? s_default;
    private static bool s_searched;

    /// <summary>Overrides the platform index (tests).</summary>
    public static IFileIndex? Override { get; set; }

    /// <summary>NextCommander's own index when it is switched on (Configuration > Operation > File index).</summary>
    public static LocalFileIndex? Local { get; set; }

    /// <summary>
    /// The index to use: the application's own once it is ready, otherwise the platform's (Everything, plocate,
    /// Spotlight) when installed, else null.
    /// </summary>
    public static IFileIndex? Default
    {
        get
        {
            if (Override is not null)
            {
                return Override;
            }
            if (Local is { IsReady: true } local)
            {
                return local;
            }
            if (!s_searched)
            {
                IFileIndex[] candidates = OperatingSystem.IsWindows() ? [new EverythingIndex()]
                    : OperatingSystem.IsMacOS() ? [new SpotlightIndex()]
                    : [new LocateIndex()];
                s_default = candidates.FirstOrDefault(c => c.IsAvailable);
                s_searched = true;
            }
            return s_default;
        }
    }

    /// <summary>
    /// Simple name patterns of a search mask for the index ("*.txt *.cs" → both). Null when the mask cannot be expressed
    /// (exclusions, paths, regex): then the index is asked for everything below the root.
    /// </summary>
    public static IReadOnlyList<string>? SimplePatterns(string mask, bool isRegex)
    {
        if (isRegex)
        {
            return null;
        }
        var include = mask.Split('|')[0];
        var parts = include.Split([' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p.IndexOfAny(['\\', '/', '"', '<', '>']) >= 0))
        {
            return null;
        }
        if (parts.Any(p => p is "*" or "*.*"))
        {
            return [];
        }
        // Without wildcards "name" matches as "*name*".
        return parts.Select(p => p.Contains('*', StringComparison.Ordinal) || p.Contains('?', StringComparison.Ordinal) ? p : "*" + p + "*").ToList();
    }

    internal static string? FindExecutable(params string[] names)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (OperatingSystem.IsWindows())
        {
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything"));
        }
        else
        {
            dirs.AddRange(["/usr/bin", "/usr/local/bin", "/opt/homebrew/bin"]);
        }
        foreach (var name in names)
        {
            foreach (var dir in dirs)
            {
                try
                {
                    var path = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
                catch (ArgumentException)
                {
                    // malformed PATH entry
                }
            }
        }
        return null;
    }

    /// <summary>Runs a query program and yields its output lines (or NUL separated records).</summary>
    internal static async IAsyncEnumerable<string> RunAsync(string exe, IEnumerable<string> args, char separator, Encoding encoding,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = encoding,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        using var process = Process.Start(info) ?? throw new IOException($"Could not start {Path.GetFileName(exe)}.");
        await using var kill = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // exited
            }
        });
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var buffer = new char[8192];
        var current = new StringBuilder();
        int n;
        while ((n = await process.StandardOutput.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < n; i++)
            {
                var c = buffer[i];
                if (c == separator || (separator == '\n' && c == '\r'))
                {
                    if (current.Length > 0)
                    {
                        yield return current.ToString();
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
        }
        if (current.Length > 0)
        {
            yield return current.ToString();
        }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // locate exits with 1 when nothing matched
        if (process.ExitCode != 0 && !(process.ExitCode == 1 && Path.GetFileNameWithoutExtension(exe).Contains("locate", StringComparison.Ordinal)))
        {
            var message = (await errors.ConfigureAwait(false)).Trim();
            throw new IOException($"{Path.GetFileName(exe)}: {(message.Length > 0 ? message : "exit code " + process.ExitCode)}");
        }
    }
}

/// <summary>voidtools Everything through its command line client es.exe (Everything must be running).</summary>
public sealed class EverythingIndex : IFileIndex
{
    private readonly string? _exe = OperatingSystem.IsWindows() ? FileIndexes.FindExecutable("es.exe") : null;

    public string Name => "Everything";

    public bool IsAvailable => _exe is not null;

    public IAsyncEnumerable<string> QueryAsync(string root, IReadOnlyList<string> patterns, CancellationToken cancellationToken)
    {
        // es.exe -path <root> [a|b]; output as UTF-8 so non-ASCII names survive.
        var args = new List<string> { "-utf8-bom", "-path", Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar };
        if (patterns.Count > 0)
        {
            args.Add(string.Join('|', patterns));
        }
        return FileIndexes.RunAsync(_exe!, args, '\n', new UTF8Encoding(false), cancellationToken);
    }

    /// <summary>
    /// Total size of the files under a folder from Everything's index (es.exe -get-total-size -path), in an instant
    /// instead of walking the folder; null when Everything cannot answer (not running, an old es.exe).
    /// </summary>
    public async Task<long?> FolderSizeAsync(string folder, CancellationToken cancellationToken)
    {
        if (_exe is null)
        {
            return null;
        }
        var args = new List<string> { "-get-total-size", "-path", Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar };
        try
        {
            var lines = new List<string>();
            await foreach (var line in FileIndexes.RunAsync(_exe, args, '\n', new UTF8Encoding(false), cancellationToken).ConfigureAwait(false))
            {
                lines.Add(line);
            }
            return ParseSize(string.Join('\n', lines));
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The number in es.exe's answer (digit grouping of any culture left out); null when there is none.</summary>
    public static long? ParseSize(string output)
    {
        var digits = new string(output.Trim().TakeWhile(c => char.IsDigit(c) || c is '.' or ',' or ' ' or '\u00A0' or '\'').Where(char.IsDigit).ToArray());
        return digits.Length > 0 && long.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size) ? size : null;
    }
}

/// <summary>
/// plocate / mlocate / locate on Linux. <paramref name="executable"/> and <paramref name="database"/> (-d) are for tests;
/// by default the program on the PATH and the system's database.
/// </summary>
public sealed class LocateIndex(string? executable = null, string? database = null) : IFileIndex
{
    private readonly string? _exe = executable ?? (OperatingSystem.IsWindows() ? null : FileIndexes.FindExecutable("plocate", "locate"));

    public string Name => _exe is null ? "locate" : Path.GetFileName(_exe);

    public bool IsAvailable => _exe is not null;

    /// <summary>The arguments of one query: NUL separated, any case, "&lt;root&gt;/*&lt;name pattern&gt;" (a pattern with wildcards matches the whole path).</summary>
    public IReadOnlyList<string> Arguments(string root, string? pattern)
    {
        var prefix = Path.TrimEndingDirectorySeparator(root).Replace('\\', '/') + "/";
        var args = new List<string> { "-0", "-i" };
        if (database is not null)
        {
            args.AddRange(["-d", database]);
        }
        args.Add(pattern is null ? prefix + "*" : prefix + "*" + (pattern.StartsWith('*') ? pattern[1..] : "/" + pattern));
        return args;
    }

    /// <summary>
    /// One query per pattern: plocate requires every pattern of one call to match (AND), mlocate any of them (OR);
    /// separate calls give "any" with both. A path found by two patterns comes once.
    /// </summary>
    public async IAsyncEnumerable<string> QueryAsync(string root, IReadOnlyList<string> patterns, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pattern in patterns.Count == 0 ? [null] : patterns.Cast<string?>())
        {
            await foreach (var path in FileIndexes.RunAsync(_exe!, Arguments(root, pattern), '\0', Encoding.UTF8, cancellationToken).ConfigureAwait(false))
            {
                if (seen.Add(path))
                {
                    yield return path;
                }
            }
        }
    }
}

/// <summary>macOS Spotlight (mdfind).</summary>
public sealed class SpotlightIndex : IFileIndex
{
    private readonly string? _exe = OperatingSystem.IsMacOS() ? FileIndexes.FindExecutable("mdfind") : null;

    public string Name => "Spotlight";

    public bool IsAvailable => _exe is not null;

    public IAsyncEnumerable<string> QueryAsync(string root, IReadOnlyList<string> patterns, CancellationToken cancellationToken)
    {
        var query = patterns.Count == 0
            ? "kMDItemFSName == \"*\""
            : string.Join(" || ", patterns.Select(p => $"kMDItemFSName == \"{p.Replace("\"", string.Empty, StringComparison.Ordinal)}\"c"));
        return FileIndexes.RunAsync(_exe!, ["-0", "-onlyin", root, query], '\0', Encoding.UTF8, cancellationToken);
    }
}
