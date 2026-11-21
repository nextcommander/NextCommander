using System.Text;
using NextCommander.Core.Vfs;
using NextCommander.Plugin.Git;

namespace NextCommander.Core.Tests;

/// <summary>The Git plugin against a real repository made with the installed git (skipped without git).</summary>
public sealed class GitPluginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nc-git-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly GitTool? _git = GitTool.Installed;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal); // git's objects are read-only
            }
            Directory.Delete(_root, recursive: true);
        }
    }

    private Task<string> Git(params string[] arguments) => _git!.RunAsync(_root, arguments, CancellationToken.None);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>main: a.txt "one", sub/b.txt; tag v1; branch feature/x with a.txt "two"; then a.txt changed, c.txt new, build/ ignored.</summary>
    private async Task MakeRepositoryAsync()
    {
        Directory.CreateDirectory(_root);
        await Git("init", "-q", "-b", "main");
        await Git("config", "user.name", "Test User");
        await Git("config", "user.email", "test@example.com");
        await Git("config", "commit.gpgsign", "false");
        Write("a.txt", "one");
        Write(Path.Combine("sub", "b.txt"), "bee");
        Write(".gitignore", "build/\n");
        await Git("add", ".");
        await Git("commit", "-q", "-m", "First: the files");
        await Git("tag", "v1");
        await Git("checkout", "-q", "-b", "feature/x");
        Write("a.txt", "two");
        await Git("commit", "-q", "-am", "Second on feature");
        await Git("checkout", "-q", "main");
        Write("a.txt", "changed here");
        Write("c.txt", "new");
        Write(Path.Combine("build", "out.bin"), "x");
    }

    [Fact]
    public async Task The_history_has_branches_tags_and_commits_with_their_files()
    {
        if (_git is null)
        {
            return;
        }
        await MakeRepositoryAsync();
        var git = new GitFileSystemProvider(_git);

        // a folder of the repository: its history root; ".." from there goes back to the repository
        var root = git.RootFor(VfsPath.Local(Path.Combine(_root, "sub")))!;
        Assert.Equal(GitFileSystemProvider.RootOf(_root), root);
        Assert.Null(git.RootFor(VfsPath.Local(Path.GetTempPath())));
        Assert.Equal(VfsPath.Local(_root.TrimEnd('\\', '/')), git.OriginOf(root)!.Value.Folder);
        Assert.Equal(["branches", "tags", "commits"], (await git.ListAsync(root, CancellationToken.None)).Select(e => e.Name));

        // branches: a slash in a name stays one folder; a branch holds its files as they are there
        var branches = await git.ListAsync(root.Combine("branches"), CancellationToken.None);
        Assert.Equal(["feature⁄x", "main"], branches.Select(e => e.Name).Order(StringComparer.Ordinal));
        var feature = root.Combine("branches").Combine("feature⁄x");
        Assert.Equal([".gitignore", "a.txt", "sub"], (await git.ListAsync(feature, CancellationToken.None)).Select(e => e.Name));
        Assert.Equal("two", await ReadAsync(git, feature.Combine("a.txt")));
        Assert.Equal("one", await ReadAsync(git, root.Combine("branches").Combine("main").Combine("a.txt")));
        var b = (await git.GetEntryAsync(feature.Combine("sub").Combine("b.txt"), CancellationToken.None))!;
        Assert.Equal((3L, false), (b.Size, b.IsDirectory));
        Assert.Null(await git.GetEntryAsync(feature.Combine("none.txt"), CancellationToken.None));

        // a tag and a commit
        Assert.Equal("one", await ReadAsync(git, root.Combine("tags").Combine("v1").Combine("a.txt")));
        var commits = await git.ListAsync(root.Combine("commits"), CancellationToken.None);
        var first = Assert.Single(commits); // main has one commit
        Assert.EndsWith(" First  the files", first.Name, StringComparison.Ordinal);
        Assert.Equal("bee", await ReadAsync(git, root.Combine("commits").Combine(first.Name).Combine("sub").Combine("b.txt")));

        // read-only
        await Assert.ThrowsAsync<NotSupportedException>(() => git.DeleteAsync(feature.Combine("a.txt"), false, CancellationToken.None));
    }

    private static async Task<string> ReadAsync(GitFileSystemProvider git, VfsPath path)
    {
        await using var stream = await git.OpenReadAsync(path, CancellationToken.None);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task The_columns_show_the_state_and_the_last_commit()
    {
        if (_git is null)
        {
            return;
        }
        await MakeRepositoryAsync();
        var columns = new GitColumns(_git);
        var registry = new ProviderRegistry([]);
        string[] all = [GitColumns.StatusField, GitColumns.SubjectField, GitColumns.AuthorField, GitColumns.DateField, GitColumns.HashField];
        async Task<IReadOnlyDictionary<string, object?>> Values(string relative, bool directory = false) =>
            await columns.GetValuesAsync(new VfsEntry { Name = Path.GetFileName(relative), Path = VfsPath.Local(Path.Combine(_root, relative)), IsDirectory = directory },
                all, registry, CancellationToken.None);

        var a = await Values("a.txt");
        Assert.Equal(("Modified", "First: the files", "Test User"), (a[GitColumns.StatusField], a[GitColumns.SubjectField], a[GitColumns.AuthorField]));
        Assert.IsType<DateTime>(a[GitColumns.DateField]);
        Assert.Equal("Untracked", (await Values("c.txt"))[GitColumns.StatusField]);
        Assert.Equal("Ignored", (await Values("build", directory: true))[GitColumns.StatusField]);
        var sub = await Values("sub", directory: true);
        Assert.Null(sub[GitColumns.StatusField]); // unchanged
        Assert.Equal("First: the files", sub[GitColumns.SubjectField]);
        Assert.Null((await Values(Path.Combine("sub", "b.txt")))[GitColumns.StatusField]);

        // outside a repository: no values
        var outside = await columns.GetValuesAsync(new VfsEntry { Name = "x", Path = VfsPath.Local(Path.Combine(Path.GetTempPath(), "x")) }, all, registry, CancellationToken.None);
        Assert.Empty(outside);
    }

    [Fact]
    public void Status_codes_are_named()
    {
        var status = GitColumns.ParseStatus(" M a.txt\0R  new.txt\0old.txt\0?? c.txt\0!! build/\0UU both.txt\0");
        Assert.Equal("Modified", GitColumns.StateOf(status, "a.txt", false));
        Assert.Equal("Renamed", GitColumns.StateOf(status, "new.txt", false));
        Assert.False(status.Files.ContainsKey("old.txt"));
        Assert.Equal("Untracked", GitColumns.StateOf(status, "c.txt", false));
        Assert.Equal("Ignored", GitColumns.StateOf(status, "build/out.bin", false));
        Assert.Equal("Conflict", GitColumns.StateOf(status, "both.txt", false));
        Assert.Equal("Modified (staged)", GitColumns.Describe("M "));
    }
}
