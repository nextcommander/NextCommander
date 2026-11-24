using NextCommander.Core.Plugins;
using NextCommander.Core.Vfs;

namespace NextCommander.Plugin.Git;

/// <summary>
/// Writing with Git (user's request, 24 November 2026): in a repository's working folder the selected files are staged,
/// unstaged or put back, a commit is made, a branch is switched to or made, and the changes of a file or its history
/// are shown. Everything goes through the installed git; its own messages are shown when it refuses.
/// </summary>
public sealed partial class GitActions(Func<GitTool?> tool)
{
    /// <summary>A repository was changed by an action: the Git columns read its status again.</summary>
    public static event Action<string>? RepositoryChanged;

    /// <summary>The actions for <paramref name="folder"/> and the selected entries (none outside a repository).</summary>
    public IReadOnlyList<PluginAction> ActionsFor(VfsPath folder, IReadOnlyList<VfsEntry> selected, IPluginDialogs dialogs)
    {
        if (!folder.IsLocal || GitTool.RepositoryOf(folder.Path) is not { } repository)
        {
            return [];
        }
        var files = selected.Where(e => e.Path.IsLocal && e.Name != ".git").Select(e => e.Path.Path).ToList();
        var actions = new List<PluginAction>();
        if (files.Count > 0)
        {
            actions.Add(new PluginAction("Git: Stage", PluginActionIcon.Start, ct => StageAsync(repository, files, ct)));
            actions.Add(new PluginAction("Git: Unstage", PluginActionIcon.Stop, ct => UnstageAsync(repository, files, ct)));
            actions.Add(new PluginAction("Git: Discard changes...", PluginActionIcon.Delete, ct => DiscardAsync(repository, files, dialogs, ct)));
            actions.Add(new PluginAction("Git: Show changes", PluginActionIcon.Info, ct => ShowChangesAsync(repository, files, dialogs, ct)));
            if (files.Count == 1 && File.Exists(files[0]))
            {
                actions.Add(new PluginAction("Git: File history...", PluginActionIcon.Info, ct => FileHistoryAsync(repository, files[0], dialogs, ct)));
            }
        }
        actions.Add(new PluginAction("Git: Commit...", PluginActionIcon.Start, ct => CommitAsync(repository, dialogs, ct)));
        actions.Add(new PluginAction("Git: Switch branch...", PluginActionIcon.Restart, ct => SwitchAsync(repository, dialogs, ct)));
        actions.Add(new PluginAction("Git: New branch...", PluginActionIcon.Other, ct => NewBranchAsync(repository, dialogs, ct)));
        actions.AddRange(RemoteActions(repository, dialogs));
        return actions;
    }

    private GitTool Git => tool() ?? throw new InvalidOperationException(GitTool.NotInstalled);

    private async Task<string> RunAsync(string repository, IEnumerable<string> arguments, CancellationToken ct)
    {
        try
        {
            return await Git.RunAsync(repository, arguments, ct).ConfigureAwait(false);
        }
        finally
        {
            RepositoryChanged?.Invoke(repository);
        }
    }

    /// <summary>"--" and the files relative to the repository (git takes them so on every system).</summary>
    private static IEnumerable<string> Paths(string repository, IEnumerable<string> files) =>
        ["--", .. files.Select(f => Path.GetRelativePath(repository, f).Replace('\\', '/'))];

    internal async Task<string?> StageAsync(string repository, IReadOnlyList<string> files, CancellationToken ct)
    {
        await RunAsync(repository, ["add", "-A", .. Paths(repository, files)], ct).ConfigureAwait(false);
        return null;
    }

    internal async Task<string?> UnstageAsync(string repository, IReadOnlyList<string> files, CancellationToken ct)
    {
        await RunAsync(repository, ["restore", "--staged", .. Paths(repository, files)], ct).ConfigureAwait(false);
        return null;
    }

    internal async Task<string?> DiscardAsync(string repository, IReadOnlyList<string> files, IPluginDialogs dialogs, CancellationToken ct)
    {
        var question = files.Count == 1
            ? $"Put \"{Path.GetFileName(files[0])}\" back as in the last commit? Its changes are lost."
            : $"Put {files.Count} items back as in the last commit? Their changes are lost (new files that Git does not know stay).";
        if (!await dialogs.ConfirmAsync("Git", question).ConfigureAwait(false))
        {
            return null;
        }
        await RunAsync(repository, ["restore", "--staged", "--worktree", "--source=HEAD", .. Paths(repository, files)], ct).ConfigureAwait(false);
        return null;
    }

    internal async Task<string?> ShowChangesAsync(string repository, IReadOnlyList<string> files, IPluginDialogs dialogs, CancellationToken ct)
    {
        // the changes against the last commit, staged or not
        var diff = await Git.RunAsync(repository, ["diff", "HEAD", "--no-color", .. Paths(repository, files)], ct).ConfigureAwait(false);
        if (diff.Trim().Length == 0)
        {
            return "No changes against the last commit.";
        }
        await dialogs.ShowTextAsync("changes.diff", diff).ConfigureAwait(false);
        return null;
    }

    internal async Task<string?> FileHistoryAsync(string repository, string file, IPluginDialogs dialogs, CancellationToken ct)
    {
        var relative = Path.GetRelativePath(repository, file).Replace('\\', '/');
        var log = await Git.RunAsync(repository, ["log", "--follow", "-n", "200", "--date=short", "--format=%h %ad %an: %s", "--", relative], ct).ConfigureAwait(false);
        var commits = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (commits.Length == 0)
        {
            return "This file has no commits yet.";
        }
        var chosen = await dialogs.ChooseAsync($"Git: {Path.GetFileName(file)}", commits).ConfigureAwait(false);
        if (chosen is null)
        {
            return null;
        }
        var hash = chosen.Split(' ', 2)[0];
        var show = await Git.RunAsync(repository, ["show", "--no-color", "--format=commit %H%nAuthor: %an <%ae>%nDate:   %ad%n%n    %s%n", hash, "--", relative], ct).ConfigureAwait(false);
        await dialogs.ShowTextAsync(hash + ".diff", show).ConfigureAwait(false);
        return null;
    }

    internal async Task<string?> CommitAsync(string repository, IPluginDialogs dialogs, CancellationToken ct)
    {
        var status = await Git.RunAsync(repository, ["status", "--porcelain=v1"], ct).ConfigureAwait(false);
        var lines = status.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var staged = lines.Count(l => l.Length > 1 && l[0] is not (' ' or '?' or '!'));
        if (lines.Length == 0)
        {
            return "Nothing to commit: the working folder is as in the last commit.";
        }
        var all = false;
        if (staged == 0)
        {
            // nothing staged: the changes of the files Git knows, as "git commit -a"
            if (!await dialogs.ConfirmAsync("Git", "Nothing is staged. Commit every change of the files Git knows (new files stay out)?").ConfigureAwait(false))
            {
                return null;
            }
            all = true;
        }
        var message = await dialogs.PromptAsync("Git: Commit", all ? "Message of the commit (every change of the known files):" : $"Message of the commit ({staged} staged):", string.Empty).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(message))
        {
            return message is null ? null : "A commit needs a message.";
        }
        var output = await RunAsync(repository, all ? ["commit", "-a", "-m", message.Trim()] : ["commit", "-m", message.Trim()], ct).ConfigureAwait(false);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
    }

    internal async Task<string?> SwitchAsync(string repository, IPluginDialogs dialogs, CancellationToken ct)
    {
        var branches = (await Git.RunAsync(repository, ["branch", "--format=%(HEAD) %(refname:short)"], ct).ConfigureAwait(false))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd()).ToList();
        var others = branches.Where(b => !b.StartsWith('*')).Select(b => b.Trim()).ToList();
        if (others.Count == 0)
        {
            return "There is no other branch. Git: New branch... makes one.";
        }
        var chosen = await dialogs.ChooseAsync("Git: Switch branch", others).ConfigureAwait(false);
        if (chosen is null)
        {
            return null;
        }
        await RunAsync(repository, ["switch", chosen], ct).ConfigureAwait(false);
        return null;
    }

    internal async Task<string?> NewBranchAsync(string repository, IPluginDialogs dialogs, CancellationToken ct)
    {
        var name = await dialogs.PromptAsync("Git: New branch", "Name of the new branch (made from the current one and switched to):", string.Empty).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        await RunAsync(repository, ["switch", "-c", name.Trim()], ct).ConfigureAwait(false);
        return null;
    }
}
