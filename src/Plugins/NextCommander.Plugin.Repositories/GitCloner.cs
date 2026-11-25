using System.Diagnostics;
using System.Text;

namespace NextCommander.Plugin.Repositories;

/// <summary>
/// Cloning a repository with the installed git (found as the Git plugin finds it: FM_GIT, the PATH, the usual folders).
/// The access token goes to git as an extra HTTP header through the environment (GIT_CONFIG_*), so it is neither on the
/// command line nor in the clone's .git/config. Nothing is asked in a terminal (GIT_TERMINAL_PROMPT=0).
/// </summary>
internal static class GitCloner
{
    public const string NotInstalled = "Cloning needs git: install it from git-scm.com (or set FM_GIT to its path).";

    /// <summary>git(.exe): FM_GIT, then PATH, then the usual install folders; null when there is none.</summary>
    public static string? Find()
    {
        var windows = OperatingSystem.IsWindows();
        var exe = windows ? "git.exe" : "git";
        if (Environment.GetEnvironmentVariable("FM_GIT") is { Length: > 0 } configured && File.Exists(configured))
        {
            return configured;
        }
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim('"'), exe)).ToList();
        if (windows)
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", exe));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd", exe));
        }
        else
        {
            candidates.AddRange(["/usr/bin/git", "/usr/local/bin/git", "/opt/homebrew/bin/git"]);
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>The start of "git clone" (tests read it without running git).</summary>
    internal static ProcessStartInfo StartInfo(string git, string url, string target, string? branch, (string User, string Token)? signIn)
    {
        var info = new ProcessStartInfo(git)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in (string[])["clone", .. branch is null ? (string[])[] : ["--branch", branch], "--", url, target])
        {
            info.ArgumentList.Add(argument);
        }
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (signIn is var (user, token))
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + token));
            info.Environment["GIT_CONFIG_COUNT"] = "1";
            info.Environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            info.Environment["GIT_CONFIG_VALUE_0"] = "Authorization: Basic " + basic;
        }
        return info;
    }

    /// <summary>Runs "git clone"; git's own message when it fails.</summary>
    public static async Task CloneAsync(string git, string url, string target, string? branch, (string User, string Token)? signIn, CancellationToken cancellationToken)
    {
        using var process = Process.Start(StartInfo(git, url, target, branch, signIn)) ?? throw new IOException("git could not be started.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
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
            throw;
        }
        await output.ConfigureAwait(false);
        var message = await error.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var line = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(l => l.StartsWith("fatal:", StringComparison.Ordinal) || l.StartsWith("error:", StringComparison.Ordinal))
                ?? message.Trim();
            throw new IOException("git: " + (line.Length > 0 ? line : "exit code " + process.ExitCode));
        }
    }
}
