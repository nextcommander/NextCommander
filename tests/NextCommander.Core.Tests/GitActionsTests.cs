using NextCommander.Core.Plugins;
using NextCommander.Core.Vfs;
using NextCommander.Plugin.Git;

namespace NextCommander.Core.Tests;

/// <summary>Writing with the Git plugin: stage, commit, branches, changes and history (skipped without git).</summary>
[Collection("PluginStatics")]
public sealed class GitActionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nc-gitw-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly GitTool? _git = GitTool.Installed;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Answers the plugin's dialogs from queues; shown texts are kept.</summary>
    private sealed class Dialogs : IPluginDialogs
    {
        public Queue<string?> Prompts { get; } = new();

        public Queue<string?> Choices { get; } = new();

        public bool Confirm { get; set; } = true;

        public List<(string File, string Text)> Shown { get; } = [];

        public List<IReadOnlyList<string>> Offered { get; } = [];

        public Task<string?> PromptAsync(string title, string message, string initialText) => Task.FromResult(Prompts.Count > 0 ? Prompts.Dequeue() : null);

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> choices)
        {
            Offered.Add(choices);
            var wanted = Choices.Count > 0 ? Choices.Dequeue() : null;
            return Task.FromResult(wanted is null ? null : choices.FirstOrDefault(c => c.Contains(wanted, StringComparison.Ordinal)));
        }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(Confirm);

        public Task ShowTextAsync(string fileName, string text)
        {
            Shown.Add((fileName, text));
            return Task.CompletedTask;
        }
    }

    private Task<string> Git(params string[] arguments) => _git!.RunAsync(_root, arguments, CancellationToken.None);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static VfsEntry Entry(string path) => new() { Name = Path.GetFileName(path), Path = VfsPath.Local(path) };

    private static async Task<string?> Run(IReadOnlyList<PluginAction> actions, string name) =>
        await actions.Single(a => a.Name == name).RunAsync(CancellationToken.None);

    [Fact]
    public async Task Stage_commit_branches_changes_and_history()
    {
        if (_git is null)
        {
            return;
        }
        Directory.CreateDirectory(_root);
        await Git("init", "-q", "-b", "main");
        await Git("config", "user.name", "Test User");
        await Git("config", "user.email", "test@example.com");
        await Git("config", "commit.gpgsign", "false");
        var a = Write("a.txt", "one\n");
        var b = Write(Path.Combine("sub", "b.txt"), "bee\n");
        var git = new GitFileSystemProvider(_git);
        var dialogs = new Dialogs();
        var folder = VfsPath.Local(_root);

        // outside a repository: nothing
        Assert.Empty(git.ActionsFor(VfsPath.Local(Path.GetTempPath()), [], dialogs));
        // no selection: the repository's actions only
        Assert.Equal(["Git: Commit...", "Git: Switch branch...", "Git: New branch...", "Git: Fetch", "Git: Pull", "Git: Push", "Git: Merge branch..."],
            git.ActionsFor(folder, [], dialogs).Select(x => x.Name));

        var actions = git.ActionsFor(folder, [Entry(a), Entry(Path.Combine(_root, "sub"))], dialogs);
        await Run(actions, "Git: Stage");
        Assert.Equal(["A  a.txt", "A  sub/b.txt"], (await Git("status", "--porcelain=v1")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Order());
        dialogs.Prompts.Enqueue("First commit");
        Assert.Contains("First commit", await Run(actions, "Git: Commit..."), StringComparison.Ordinal);
        Assert.Equal("First commit", (await Git("log", "-1", "--format=%s")).Trim());
        Assert.Contains("Nothing to commit", await Run(actions, "Git: Commit..."), StringComparison.Ordinal);

        // a change: shown, staged, unstaged, committed with "everything" when nothing is staged
        File.WriteAllText(a, "two\n");
        var one = git.ActionsFor(folder, [Entry(a)], dialogs);
        await Run(one, "Git: Show changes");
        Assert.Equal("changes.diff", dialogs.Shown[^1].File);
        Assert.Contains("+two", dialogs.Shown[^1].Text, StringComparison.Ordinal);
        await Run(one, "Git: Stage");
        await Run(one, "Git: Unstage");
        Assert.Equal(" M a.txt", (await Git("status", "--porcelain=v1")).TrimEnd());
        dialogs.Prompts.Enqueue("Second");
        await Run(one, "Git: Commit...");
        Assert.Equal("Second", (await Git("log", "-1", "--format=%s")).Trim());

        // discarding puts the file back as in the last commit
        File.WriteAllText(a, "spoiled\n");
        await Run(one, "Git: Discard changes...");
        Assert.Equal("two", File.ReadAllText(a).Trim());

        // the history of the file: a commit chosen, its changes shown
        dialogs.Choices.Enqueue("First commit");
        await Run(one, "Git: File history...");
        Assert.Equal(2, dialogs.Offered[^1].Count);
        Assert.Contains("+one", dialogs.Shown[^1].Text, StringComparison.Ordinal);

        // branches
        Assert.Contains("no other branch", await Run(actions, "Git: Switch branch..."), StringComparison.Ordinal);
        dialogs.Prompts.Enqueue("topic");
        await Run(actions, "Git: New branch...");
        Assert.Equal("topic", (await Git("branch", "--show-current")).Trim());
        dialogs.Choices.Enqueue("main");
        await Run(actions, "Git: Switch branch...");
        Assert.Equal("main", (await Git("branch", "--show-current")).Trim());
        Assert.Equal(["main"], dialogs.Offered[^1]);

        // git's own refusal comes back as an error
        await Assert.ThrowsAsync<IOException>(() => Run(git.ActionsFor(folder, [Entry(Path.Combine(_root, "missing.txt"))], dialogs), "Git: Stage"));
        _ = b;
    }

    private async Task<string> GitIn(string folder, params string[] arguments) => await _git!.RunAsync(folder, arguments, CancellationToken.None);

    private async Task Identity(string folder)
    {
        await GitIn(folder, "config", "user.name", "Test User");
        await GitIn(folder, "config", "user.email", "test@example.com");
        await GitIn(folder, "config", "commit.gpgsign", "false");
    }

    [Fact]
    public async Task Push_pull_fetch_and_a_merge_with_conflicts()
    {
        if (_git is null)
        {
            return;
        }
        // a "server" (a bare repository on disk), and two working folders of it
        var server = Path.Combine(_root, "server.git");
        var mine = Path.Combine(_root, "mine");
        var theirs = Path.Combine(_root, "theirs");
        Directory.CreateDirectory(server);
        Directory.CreateDirectory(mine);
        await GitIn(server, "init", "-q", "--bare", "-b", "main");
        await GitIn(mine, "init", "-q", "-b", "main");
        await Identity(mine);
        await GitIn(mine, "remote", "add", "origin", server);
        File.WriteAllText(Path.Combine(mine, "a.txt"), "one\n");
        await GitIn(mine, "add", ".");
        await GitIn(mine, "commit", "-q", "-m", "First");

        var git = new GitFileSystemProvider(_git);
        var dialogs = new Dialogs();
        IReadOnlyList<PluginAction> In(string folder) => git.ActionsFor(VfsPath.Local(folder), [], dialogs);

        // the branch is not on the server yet: pushed and followed once the user agrees
        dialogs.Confirm = false;
        Assert.Null(await Run(In(mine), "Git: Push"));
        dialogs.Confirm = true;
        await Run(In(mine), "Git: Push");
        Assert.Equal("origin/main", (await GitIn(mine, "rev-parse", "--abbrev-ref", "main@{upstream}")).Trim());

        await GitIn(_root, "clone", "-q", server, theirs);
        await Identity(theirs);
        File.WriteAllText(Path.Combine(theirs, "b.txt"), "bee\n");
        await GitIn(theirs, "add", ".");
        await GitIn(theirs, "commit", "-q", "-m", "Theirs");
        await GitIn(theirs, "push", "-q");

        Assert.Contains("behind 1", await Run(In(mine), "Git: Fetch"), StringComparison.Ordinal);
        await Run(In(mine), "Git: Pull");
        Assert.True(File.Exists(Path.Combine(mine, "b.txt")));

        // a merge with a conflict: listed, aborted, done again, solved and finished
        await GitIn(mine, "switch", "-q", "-c", "topic");
        File.WriteAllText(Path.Combine(mine, "a.txt"), "topic\n");
        await GitIn(mine, "commit", "-q", "-am", "Topic");
        await GitIn(mine, "switch", "-q", "main");
        File.WriteAllText(Path.Combine(mine, "a.txt"), "main\n");
        await GitIn(mine, "commit", "-q", "-am", "Main");
        dialogs.Choices.Enqueue("topic");
        var message = await Run(In(mine), "Git: Merge branch...");
        Assert.Contains("Conflicts in 1 files: a.txt", message, StringComparison.Ordinal);
        Assert.DoesNotContain("origin/HEAD", dialogs.Offered[^1]);
        Assert.Equal(["Git: Conflicts...", "Git: Finish merge", "Git: Abort merge"], In(mine).Select(a => a.Name).Skip(3));
        dialogs.Choices.Enqueue("a.txt");
        await Run(In(mine), "Git: Conflicts...");
        Assert.Contains("<<<<<<<", dialogs.Shown[^1].Text, StringComparison.Ordinal);
        Assert.Contains("Still conflicted: a.txt", await Run(In(mine), "Git: Finish merge"), StringComparison.Ordinal);
        await Run(In(mine), "Git: Abort merge");
        Assert.False(GitActions.IsMerging(mine));
        Assert.Equal("main", File.ReadAllText(Path.Combine(mine, "a.txt")).Trim());

        dialogs.Choices.Enqueue("topic");
        await Run(In(mine), "Git: Merge branch...");
        File.WriteAllText(Path.Combine(mine, "a.txt"), "both\n");
        await Run(git.ActionsFor(VfsPath.Local(mine), [Entry(Path.Combine(mine, "a.txt"))], dialogs), "Git: Stage");
        await Run(In(mine), "Git: Finish merge");
        Assert.False(GitActions.IsMerging(mine));
        Assert.Equal(2, (await GitIn(mine, "log", "-1", "--format=%P")).Trim().Split(' ').Length); // a merge commit

        // a server that wants a password nobody can type is said plainly
        Assert.Contains("credential helper", GitActions.Failure("fatal: could not read Username for 'https://x': terminal prompts disabled"), StringComparison.Ordinal);
        Assert.Contains("SSH agent", GitActions.Failure("git@x: Permission denied (publickey)."), StringComparison.Ordinal);
        Assert.Equal("git: fatal: boom", GitActions.Failure("hint: x\nfatal: boom"));
    }
}
