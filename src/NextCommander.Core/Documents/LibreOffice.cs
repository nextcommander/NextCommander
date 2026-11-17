using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace NextCommander.Core.Documents;

/// <summary>
/// LibreOffice, when it is installed, turns the documents the program cannot read itself (doc, xls, ppt, rtf, Visio,
/// Publisher, WordPerfect, Keynote ...) into a PDF for the preview: soffice --headless --convert-to pdf, with a profile of
/// its own (a LibreOffice window that is open does not block it). The PDFs are kept in the temporary folder by the
/// file's path, size and time, so a file is converted once.
/// </summary>
public static class LibreOffice
{
    /// <summary>The extensions LibreOffice is asked for (Office Open XML and OpenDocument are read without it).</summary>
    public static IReadOnlySet<string> Extensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "doc", "dot", "wps", "wpd", "wpt", "xls", "xlt", "xlsb", "xlw", "ppt", "pps", "pot", "pub", "vsd", "vsdx", "vdx",
        "odg", "otg", "fodt", "fods", "fodp", "sxw", "sxc", "sxi", "hwp", "key", "pages", "numbers", "cdr", "602", "abw", "lwp",
    };

    /// <summary>Tests: the program instead of the installed one (null: looked for).</summary>
    public static string? ProgramOverride { get; set; }

    private static string? s_program;
    private static bool s_searched;
    private static readonly SemaphoreSlim s_one = new(1, 1);

    /// <summary>The soffice program, or null when LibreOffice is not installed.</summary>
    public static string? Program
    {
        get
        {
            if (ProgramOverride is { } program)
            {
                return program;
            }
            if (!s_searched)
            {
                s_program = Find();
                s_searched = true;
            }
            return s_program;
        }
    }

    public static bool Supports(string extension) => Extensions.Contains(extension) && Program is not null;

    private static string? Find()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "soffice.exe" } : ["soffice", "libreoffice"];
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(folder, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        IEnumerable<string> known = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"LibreOffice\program\soffice.exe"),
               Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"LibreOffice\program\soffice.exe")]
            : OperatingSystem.IsMacOS()
                ? ["/Applications/LibreOffice.app/Contents/MacOS/soffice"]
                : ["/usr/bin/soffice", "/usr/lib/libreoffice/program/soffice", "/opt/libreoffice/program/soffice", "/snap/bin/libreoffice"];
        return known.FirstOrDefault(File.Exists);
    }

    /// <summary>The folder of the converted PDFs (and LibreOffice's own profile).</summary>
    public static string CacheFolder => Path.Combine(Path.GetTempPath(), "NextCommander-preview");

    /// <summary>
    /// The PDF of a local file (converted now, or the one kept from before). One conversion runs at a time; an
    /// <see cref="IOException"/> when LibreOffice is missing, fails or takes longer than <paramref name="timeout"/>.
    /// </summary>
    public static async Task<string> ToPdfAsync(string file, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var program = Program ?? throw new IOException("LibreOffice is not installed.");
        var info = new FileInfo(file);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")))[..24];
        var folder = Path.Combine(CacheFolder, key);
        var pdf = Path.Combine(folder, Path.GetFileNameWithoutExtension(file) + ".pdf");
        if (File.Exists(pdf))
        {
            return pdf;
        }
        await s_one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(pdf))
            {
                return pdf;
            }
            Directory.CreateDirectory(folder);
            var profile = new Uri(Path.Combine(CacheFolder, "profile") + Path.DirectorySeparatorChar).AbsoluteUri;
            var start = new ProcessStartInfo(program)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-env:UserInstallation=" + profile, "--headless", "--norestore", "--nologo", "--convert-to", "pdf", "--outdir", folder, info.FullName },
            };
            using var process = Process.Start(start) ?? throw new IOException("LibreOffice did not start.");
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                cancellationToken.ThrowIfCancellationRequested();
                throw new IOException("LibreOffice did not convert the file in time.");
            }
            await Task.WhenAll(output, errors).ConfigureAwait(false);
            if (!File.Exists(pdf))
            {
                var reason = (await errors.ConfigureAwait(false)).Trim();
                throw new IOException("LibreOffice could not convert the file" + (reason.Length > 0 ? ": " + reason.Split('\n')[0] : "."));
            }
            return pdf;
        }
        finally
        {
            s_one.Release();
        }
    }
}
