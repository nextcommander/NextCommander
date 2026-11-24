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
        Assert.Equal(["Git: Commit...", "Git: Switch branch...", "Git: New branch..."], git.ActionsFor(folder, [], dialogs).Select(x => x.Name));

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
}
