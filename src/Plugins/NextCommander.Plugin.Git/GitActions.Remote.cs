using NextCommander.Core.Plugins;

namespace NextCommander.Plugin.Git;

/// <summary>
/// Servers and merging (user's request, 24 November 2026): fetch, pull, push (a branch without an upstream is pushed
/// and tracked when the user agrees), merging a branch, and a merge with conflicts: the conflicted files listed,
/// finished once they are staged, or aborted. Credentials come from git's own helper; a server that wants a password
/// nobody can type is said plainly.
/// </summary>
public sealed partial class GitActions
{
    /// <summary>A merge waits for its conflicts to be solved (cheap: a file of the repository).</summary>
    internal static bool IsMerging(string repository) => File.Exists(Path.Combine(repository, ".git", "MERGE_HEAD"));

    /// <summary>The actions of servers and merging for the repository.</summary>
    private IEnumerable<PluginAction> RemoteActions(string repository, IPluginDialogs dialogs)
    {
        if (IsMerging(repository))
        {
            yield return new PluginAction("Git: Conflicts...", PluginActionIcon.Info, ct => ConflictsAsync(repository, dialogs, ct));
            yield return new PluginAction("Git: Finish merge", PluginActionIcon.Start, ct => FinishMergeAsync(repository, ct));
            yield return new PluginAction("Git: Abort merge", PluginActionIcon.Stop, ct => AbortMergeAsync(repository, dialogs, ct));
            yield break;
        }
        yield return new PluginAction("Git: Fetch", PluginActionIcon.Refresh, ct => FetchAsync(repository, ct));
        yield return new PluginAction("Git: Pull", PluginActionIcon.Refresh, ct => PullAsync(repository, ct));
        yield return new PluginAction("Git: Push", PluginActionIcon.Start, ct => PushAsync(repository, dialogs, ct));
        yield return new PluginAction("Git: Merge branch...", PluginActionIcon.Other, ct => MergeAsync(repository, dialogs, ct));
    }

    private async Task<(int ExitCode, string Output, string Error)> FullAsync(string repository, IEnumerable<string> arguments, bool remote, CancellationToken ct)
    {
        try
        {
            return await Git.RunFullAsync(repository, arguments, remote, ct).ConfigureAwait(false);
        }
        finally
        {
            RepositoryChanged?.Invoke(repository);
        }
    }

    /// <summary>The last line that says something (git's progress and hints left out).</summary>
    private static string? LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => !l.StartsWith("hint:", StringComparison.Ordinal) && !l.StartsWith("remote:", StringComparison.Ordinal));

    /// <summary>A failure of a server's command as the user can act on it.</summary>
    internal static string Failure(string error)
    {
        if (error.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase)
            || error.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase))
        {
            return "The server wants a user name and password that git has not stored. Let git's credential helper keep them (e.g. push once from a terminal), then try again.";
        }
        if (error.Contains("Permission denied (publickey", StringComparison.OrdinalIgnoreCase) || error.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase))
        {
            return "The SSH server did not take a key: load the key into the SSH agent (and accept the server's key once from a terminal), then try again.";
        }
        var line = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.StartsWith("fatal:", StringComparison.Ordinal) || l.StartsWith("error:", StringComparison.Ordinal))
            ?? LastLine(error) ?? "git failed.";
        return "git: " + line;
    }

    /// <summary>The files with conflicts (both sides changed them).</summary>
    private async Task<IReadOnlyList<string>> ConflictedAsync(string repository, CancellationToken ct) =>
        (await Git.RunAsync(repository, ["diff", "--name-only", "--diff-filter=U"], ct).ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>"Conflicts in 2 files: a.txt, b.txt. ..." after a pull or merge that stopped.</summary>
    private async Task<string> ConflictMessageAsync(string repository, CancellationToken ct)
    {
        var files = await ConflictedAsync(repository, ct).ConfigureAwait(false);
        return files.Count == 0
            ? "The merge stopped. Git: Finish merge or Git: Abort merge."
            : $"Conflicts in {files.Count} files: {string.Join(", ", files.Take(8))}{(files.Count > 8 ? " ..." : string.Empty)}. Edit them, stage them (Git: Stage), then Git: Finish merge, or Git: Abort merge.";
    }

    /// <summary>"main: 2 ahead, 1 behind origin/main" from git status.</summary>
    private async Task<string> TrackingAsync(string repository, CancellationToken ct)
    {
        var head = (await Git.RunAsync(repository, ["status", "-sb", "--porcelain=v1"], ct).ConfigureAwait(false)).Split('\n')[0].TrimStart('#', ' ');
        return head.Length > 0 ? head : "Fetched.";
    }

    internal async Task<string?> FetchAsync(string repository, CancellationToken ct)
    {
        var (code, _, error) = await FullAsync(repository, ["fetch", "--prune"], remote: true, ct).ConfigureAwait(false);
        return code == 0 ? await TrackingAsync(repository, ct).ConfigureAwait(false) : Failure(error);
    }

    internal async Task<string?> PullAsync(string repository, CancellationToken ct)
    {
        var (code, output, error) = await FullAsync(repository, ["pull", "--no-edit"], remote: true, ct).ConfigureAwait(false);
        if (code == 0)
        {
            return LastLine(output) ?? LastLine(error);
        }
        return IsMerging(repository) ? await ConflictMessageAsync(repository, ct).ConfigureAwait(false) : Failure(error + "\n" + output);
    }

    internal async Task<string?> PushAsync(string repository, IPluginDialogs dialogs, CancellationToken ct)
    {
        var (code, _, error) = await FullAsync(repository, ["push"], remote: true, ct).ConfigureAwait(false);
        if (code == 0)
        {
            return LastLine(error) ?? "Pushed.";
        }
        if (!error.Contains("has no upstream branch", StringComparison.OrdinalIgnoreCase))
        {
            return Failure(error);
        }
        var branch = (await Git.RunAsync(repository, ["branch", "--show-current"], ct).ConfigureAwait(false)).Trim();
        var remotes = (await Git.RunAsync(repository, ["remote"], ct).ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var remoteName = remotes.Contains("origin") ? "origin" : remotes.FirstOrDefault();
        if (remoteName is null)
        {
            return "This repository has no server (remote) to push to.";
        }
        if (!await dialogs.ConfirmAsync("Git: Push", $"The branch \"{branch}\" is not on {remoteName} yet. Push it there and follow it from now on?").ConfigureAwait(false))
        {
            return null;
        }
        (code, _, error) = await FullAsync(repository, ["push", "-u", remoteName, branch], remote: true, ct).ConfigureAwait(false);
        return code == 0 ? LastLine(error) ?? "Pushed." : Failure(error);
    }

    internal async Task<string?> MergeAsync(string repository, IPluginDialogs dialogs, CancellationToken ct)
    {
        var current = (await Git.RunAsync(repository, ["branch", "--show-current"], ct).ConfigureAwait(false)).Trim();
        var branches = (await Git.RunAsync(repository, ["branch", "-a", "--format=%(refname:short)"], ct).ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(b => b != current && !b.EndsWith("/HEAD", StringComparison.Ordinal) && b != "origin")
            .ToList();
        if (branches.Count == 0)
        {
            return "There is no other branch to merge.";
        }
        var chosen = await dialogs.ChooseAsync($"Git: Merge into {current}", branches).ConfigureAwait(false);
        if (chosen is null)
        {
            return null;
        }
        var (code, output, error) = await FullAsync(repository, ["merge", "--no-edit", chosen], remote: false, ct).ConfigureAwait(false);
        if (code == 0)
        {
            return LastLine(output);
        }
        return IsMerging(repository) ? await ConflictMessageAsync(repository, ct).ConfigureAwait(false) : Failure(error + "\n" + output);
    }

    internal async Task<string?> ConflictsAsync(string repository, IPluginDialogs dialogs, CancellationToken ct)
    {
        var files = await ConflictedAsync(repository, ct).ConfigureAwait(false);
        if (files.Count == 0)
        {
            return "No conflicts left: Git: Finish merge makes the merge commit.";
        }
        var chosen = await dialogs.ChooseAsync("Git: Conflicts", files).ConfigureAwait(false);
        if (chosen is null)
        {
            return null;
        }
        // both sides of the file, as git shows a conflict
        var diff = await Git.RunAsync(repository, ["diff", "--no-color", "--", chosen], ct).ConfigureAwait(false);
        await dialogs.ShowTextAsync(Path.GetFileName(chosen) + ".diff", diff).ConfigureAwait(false);
        return null;
    }

    internal async Task<string?> FinishMergeAsync(string repository, CancellationToken ct)
    {
        if (await ConflictedAsync(repository, ct).ConfigureAwait(false) is { Count: > 0 } files)
        {
            return $"Still conflicted: {string.Join(", ", files.Take(8))}. Edit and stage them first.";
        }
        var (code, output, error) = await FullAsync(repository, ["commit", "--no-edit"], remote: false, ct).ConfigureAwait(false);
        return code == 0 ? LastLine(output.Split('\n')[0]) : Failure(error + "\n" + output);
    }

    internal async Task<string?> AbortMergeAsync(string repository, IPluginDialogs dialogs, CancellationToken ct)
    {
        if (!await dialogs.ConfirmAsync("Git", "Abort the merge? The files go back as they were before it.").ConfigureAwait(false))
        {
            return null;
        }
        var (code, _, error) = await FullAsync(repository, ["merge", "--abort"], remote: false, ct).ConfigureAwait(false);
        return code == 0 ? null : Failure(error);
    }
}
