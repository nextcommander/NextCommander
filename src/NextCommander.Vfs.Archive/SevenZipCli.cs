using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace NextCommander.Vfs.Archive;

/// <summary>
/// The 7-Zip command line program (7zz / 7z / 7za), used to write .7z archives (reading is built in).
/// Looked up in FM_7Z, PATH and the usual install folders.
/// </summary>
public static partial class SevenZipCli
{
    private static string? s_cached;
    private static bool s_searched;

    /// <summary>Overrides the lookup (settings / tests); null restores it.</summary>
    public static string? ExecutableOverride { get; set; }

    /// <summary>Full path of the 7-Zip executable, or null when it is not installed.</summary>
    public static string? Executable
    {
        get
        {
            if (ExecutableOverride is { } over)
            {
                return File.Exists(over) ? over : null;
            }
            if (!s_searched)
            {
                s_cached = Find();
                s_searched = true;
            }
            return s_cached;
        }
    }

    public static bool IsAvailable => Executable is not null;

    private static string? Find()
    {
        if (Environment.GetEnvironmentVariable("FM_7Z") is { Length: > 0 } env && File.Exists(env))
        {
            return env;
        }
        var names = OperatingSystem.IsWindows() ? new[] { "7zz.exe", "7z.exe", "7za.exe" } : ["7zz", "7z", "7za"];
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (OperatingSystem.IsWindows())
        {
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip"));
        }
        else
        {
            dirs.AddRange(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/snap/bin"]);
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

    /// <summary>-mx value for a compression level.</summary>
    public static int Level(CompressionLevel level) => level switch
    {
        CompressionLevel.NoCompression => 0,
        CompressionLevel.Fastest => 1,
        CompressionLevel.SmallestSize => 9,
        _ => 5,
    };

    /// <summary>
    /// Creates <paramref name="archive"/> (must not exist) from <paramref name="names"/> relative to <paramref name="workingDirectory"/>.
    /// <paramref name="percent"/> receives 0..100. Exit code 1 (some files skipped) returns the warning text; failures throw.
    /// </summary>
    /// <summary>The switches of "7z a" for the level, password and the options of Configuration > Packer > 7-Zip.</summary>
    public static IReadOnlyList<string> Arguments(CompressionLevel level, string? password, SevenZipOptions options)
    {
        var args = new List<string> { "a", "-t7z", $"-mx={(options.Level is >= 0 and <= 9 ? options.Level.Value : Level(level))}" };
        if (!string.IsNullOrWhiteSpace(options.Method))
        {
            args.Add("-m0=" + options.Method.Trim());
        }
        args.Add(options.Solid ? "-ms=on" : "-ms=off");
        if (options.DictionaryMb > 0)
        {
            args.Add("-md=" + options.DictionaryMb.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m");
        }
        if (options.WordSize > 0)
        {
            args.Add("-mfb=" + Math.Clamp(options.WordSize, 5, 273).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (options.Threads > 0)
        {
            args.Add("-mmt=" + options.Threads.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        args.AddRange(["-bsp1", "-bso0", "-bse2", "-y", "-scsUTF-8"]);
        if (!string.IsNullOrEmpty(password))
        {
            args.Add("-p" + password);
            if (options.EncryptNames)
            {
                args.Add("-mhe=on");
            }
        }
        args.AddRange(SplitArguments(options.Arguments));
        return args;
    }

    /// <summary>A command line split at spaces; "quoted parts" stay together.</summary>
    public static IEnumerable<string> SplitArguments(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
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
        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    public static async Task<string?> AddAsync(string archive, string workingDirectory, IReadOnlyList<string> names, CompressionLevel level,
        string? password, SevenZipOptions? options, Action<int>? percent, CancellationToken ct)
    {
        var exe = Executable ?? throw new IOException("7-Zip not found (7zz / 7z). Install it or specify its path with the FM_7Z environment variable.");
        var list = Path.Combine(ArchiveSettings.TempRoot, "fm7z-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllLinesAsync(list, names, new UTF8Encoding(false), ct).ConfigureAwait(false);
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in Arguments(level, password, options ?? new SevenZipOptions()))
            {
                info.ArgumentList.Add(arg);
            }
            info.ArgumentList.Add(archive);
            info.ArgumentList.Add("@" + list);

            using var process = Process.Start(info) ?? throw new IOException("Could not start 7-Zip.");
            await using var kill = ct.Register(() =>
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // already exited
                }
            });
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await ReadProgressAsync(process.StandardOutput, percent).ConfigureAwait(false);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var message = (await errors.ConfigureAwait(false)).Trim();
            return process.ExitCode switch
            {
                0 => null,
                1 => message.Length > 0 ? message : "7-Zip: some files could not be added.",
                _ => throw new IOException($"7-Zip exit code {process.ExitCode}: {(message.Length > 0 ? message : "unknown error")}"),
            };
        }
        finally
        {
            File.Delete(list);
        }
    }

    /// <summary>-bsp1 prints "  37% 12 + name" redrawn with backspaces: pick up every percentage.</summary>
    private static async Task ReadProgressAsync(StreamReader output, Action<int>? percent)
    {
        var buffer = new char[4096];
        var pending = new StringBuilder();
        int n;
        while ((n = await output.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            pending.Append(buffer, 0, n);
            var text = pending.ToString();
            var last = -1;
            foreach (Match m in Percent().Matches(text))
            {
                last = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            if (last >= 0)
            {
                percent?.Invoke(Math.Clamp(last, 0, 100));
            }
            // keep only an unfinished tail
            var cut = text.LastIndexOfAny(['\b', '\r', '\n']);
            pending.Clear();
            if (cut >= 0 && cut + 1 < text.Length)
            {
                pending.Append(text, cut + 1, text.Length - cut - 1);
            }
        }
    }

    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex Percent();
}

/// <summary>Configuration > Packer > 7-Zip: how new 7z archives are written.</summary>
/// <param name="Level">0..9; null = the level of the pack dialog.</param>
/// <param name="Method">"LZMA2", "LZMA", "PPMd", "BZip2"; null / empty = 7-Zip's default.</param>
/// <param name="Solid">Solid archive.</param>
/// <param name="Threads">CPU threads; 0 = 7-Zip decides.</param>
/// <param name="EncryptNames">With a password: the file names are encrypted too.</param>
/// <param name="Arguments">Extra switches.</param>
/// <param name="DictionaryMb">LZMA / LZMA2 dictionary in MB; 0 = the level's default.</param>
/// <param name="WordSize">LZMA / LZMA2 word size (fast bytes); 0 = the level's default.</param>
public sealed record SevenZipOptions(int? Level = null, string? Method = null, bool Solid = true, int Threads = 0, bool EncryptNames = true, string? Arguments = null,
    int DictionaryMb = 0, int WordSize = 0)
{
    /// <summary>7-Zip's LZMA2 dictionary for a level (MB): 0..1: 0.25 (shown as 1), 3: 4, 5: 16, 7: 32, 9: 64.</summary>
    public static int DefaultDictionaryMb(int level) => level switch { <= 1 => 1, <= 3 => 4, <= 5 => 16, <= 7 => 32, _ => 64 };

    /// <summary>
    /// About how much memory 7-Zip needs (MB) with LZMA / LZMA2: compressing about 11 times the dictionary (binary tree match
    /// finder) plus a little per thread, unpacking the dictionary plus a couple of MB. Other methods: null.
    /// </summary>
    public static (int Compress, int Decompress)? MemoryMb(string? method, int dictionaryMb, int threads)
    {
        if (!string.IsNullOrEmpty(method) && !method.StartsWith("LZMA", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var dict = Math.Max(1, dictionaryMb);
        var workers = Math.Max(1, threads == 0 ? Math.Min(Environment.ProcessorCount, 16) : threads);
        var compress = (int)Math.Ceiling(dict * 11.5) + (6 * ((workers + 1) / 2));
        return (compress, dict + 2);
    }
}
