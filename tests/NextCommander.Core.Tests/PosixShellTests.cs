using System.Diagnostics;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Tests;

/// <summary>The shell commands of SCP and ADB: quoting, the listing script run in a real POSIX shell, and its output.</summary>
public sealed class PosixShellTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-shell-" + Guid.NewGuid().ToString("N")[..8]);

    public PosixShellTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("$(rm -rf /)", "'$(rm -rf /)'")]
    public void Paths_are_quoted_for_the_shell(string text, string quoted) => Assert.Equal(quoted, PosixShell.Quote(text));

    [Fact]
    public void Lines_of_gnu_and_bsd_stat()
    {
        var gnu = PosixShell.ParseLine("0/12/1700000000/regular file/a b.txt")!.Value;
        Assert.Equal(("a b.txt", false, false, 12L), (gnu.Name, gnu.IsDirectory, gnu.IsLink, gnu.Size));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000).LocalDateTime, gnu.Modified);
        var link = PosixShell.ParseLine("1/7/1700000000/symbolic link/docs")!.Value;
        Assert.True(link.IsDirectory && link.IsLink); // a link to a folder
        var bsd = PosixShell.ParseLine("1/96/1700000000/Directory/Music")!.Value;
        Assert.Equal(("Music", true), (bsd.Name, bsd.IsDirectory));
        Assert.Equal("x/y", PosixShell.ParseLine("0/1/1/regular file/x/y")!.Value.Name); // only the first four slashes split
        Assert.Null(PosixShell.ParseLine("garbage"));
        Assert.Null(PosixShell.ParseLine("0/size/1/regular file/a"));
    }

    /// <summary>A POSIX shell here: /bin/sh, or Git for Windows' sh.</summary>
    private static string? Shell()
    {
        foreach (var candidate in new[] { "/bin/sh", @"C:\Program Files\Git\usr\bin\sh.exe" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string RunShell(string shell, string command)
    {
        var info = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(command);
        if (OperatingSystem.IsWindows())
        {
            info.Environment["PATH"] = Path.GetDirectoryName(shell) + ";" + info.Environment["PATH"]; // Git's stat, tr ...
        }
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    /// <summary>The folder as the shell sees it (Git for Windows: /c/Users/...).</summary>
    private string ShellPath(string shell) => OperatingSystem.IsWindows()
        ? RunShell(shell, "cygpath -u " + PosixShell.Quote(_root)).Trim()
        : _root;

    [Fact]
    public void The_listing_script_runs_in_a_posix_shell()
    {
        if (Shell() is not { } shell)
        {
            return; // no POSIX shell on this machine
        }
        File.WriteAllText(Path.Combine(_root, "a b.txt"), "12345");
        File.WriteAllText(Path.Combine(_root, "it's.txt"), "x");
        File.WriteAllText(Path.Combine(_root, ".hidden"), "");
        Directory.CreateDirectory(Path.Combine(_root, "sub folder"));
        var folder = ShellPath(shell);

        var lines = RunShell(shell, PosixShell.ListCommand(folder)).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => PosixShell.ParseLine(l.TrimEnd('\r'))).ToList();
        Assert.All(lines, l => Assert.NotNull(l));
        var items = lines.Select(l => l!.Value).ToDictionary(l => l.Name);
        Assert.Equal([".hidden", "a b.txt", "it's.txt", "sub folder"], items.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(5, items["a b.txt"].Size);
        Assert.True(items["sub folder"].IsDirectory);
        Assert.False(items["it's.txt"].IsDirectory);

        var one = RunShell(shell, PosixShell.StatCommand(folder + "/a b.txt")).Trim();
        Assert.Equal("a b.txt", PosixShell.ParseLine(one)!.Value.Name);
        Assert.Equal(string.Empty, RunShell(shell, PosixShell.StatCommand(folder + "/missing")).Trim());
    }
}
