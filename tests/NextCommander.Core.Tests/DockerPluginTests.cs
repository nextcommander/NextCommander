using System.Text;
using NextCommander.Core.Vfs;
using NextCommander.Plugin.Docker;

namespace NextCommander.Core.Tests;

/// <summary>
/// The Docker plugin with a fake docker (the commands it runs, what it makes of their output and errors), and against
/// a real container when FM_TEST_DOCKER=1 and Docker runs (it starts an alpine container and removes it).
/// </summary>
public sealed class DockerPluginTests
{
    /// <summary>Answers each docker command with the first matching reply and records the commands and their input.</summary>
    private sealed class FakeDocker : IDockerRunner
    {
        public List<(Func<IReadOnlyList<string>, bool> When, DockerResult Result)> Replies { get; } = [];

        public List<(IReadOnlyList<string> Arguments, byte[]? Input)> Calls { get; } = [];

        public void Reply(Func<IReadOnlyList<string>, bool> when, string output, int exitCode = 0, string error = "") =>
            Replies.Add((when, new DockerResult(exitCode, output, error)));

        public async Task<DockerResult> RunAsync(IReadOnlyList<string> arguments, Stream? input, CancellationToken cancellationToken)
        {
            byte[]? data = null;
            if (input is not null)
            {
                using var copy = new MemoryStream();
                await input.CopyToAsync(copy, cancellationToken);
                data = copy.ToArray();
            }
            Calls.Add((arguments, data));
            return Replies.FirstOrDefault(r => r.When(arguments)).Result ?? new DockerResult(0, string.Empty, string.Empty);
        }

        public Stream OpenOutput(IReadOnlyList<string> arguments)
        {
            Calls.Add((arguments, null));
            return new MemoryStream(Encoding.UTF8.GetBytes("file content"));
        }
    }

    private const string PsOutput = """
        {"Command":"\"nginx\"","CreatedAt":"2026-11-22 10:15:30 +0300 +03","ID":"abc","Image":"nginx:1.27","Names":"web","State":"running","Status":"Up 2 hours"}
        {"CreatedAt":"2026-11-20 08:00:00 +0000 UTC","Image":"postgres:17","Names":"db,web/db","State":"exited","Status":"Exited (0) 2 days ago"}
        not json
        """;

    [Fact]
    public async Task The_root_lists_the_containers_and_a_container_its_files()
    {
        var docker = new FakeDocker();
        docker.Reply(a => a[0] == "ps", PsOutput);
        docker.Reply(a => a is ["exec", "web", "sh", "-c", _] && a[4].StartsWith("cd -- '/etc'", StringComparison.Ordinal),
            "1/4096/1763800000/directory/nginx\n0/12/1763800000/regular file/hostname\n0/7/1763800000/symbolic link/my file\n");
        var provider = new DockerFileSystemProvider(docker);

        var containers = await provider.ListAsync(DockerFileSystemProvider.Root, CancellationToken.None);
        Assert.Equal(["db", "web"], containers.Select(e => e.Name));
        Assert.All(containers, c => Assert.True(c.IsDirectory));
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 10, 15, 30, TimeSpan.FromHours(3)).LocalDateTime, containers[1].LastWriteTime);
        Assert.Equal(VfsPath.Create("docker", "web", "/"), containers[1].Path);

        // ".." from a container goes back to the list, onto the container
        Assert.Equal((DockerFileSystemProvider.Root, "web"), provider.OriginOf(containers[1].Path));
        Assert.Null(provider.OriginOf(containers[1].Path.Combine("etc")));

        var etc = await provider.ListAsync(VfsPath.Create("docker", "web", "/etc"), CancellationToken.None);
        Assert.Equal(["nginx", "hostname", "my file"], etc.Select(e => e.Name));
        Assert.True(etc[0].IsDirectory);
        Assert.Equal(12, etc[1].Size);
        Assert.True(etc[2].IsLink);
        Assert.Equal("/etc/my file", etc[2].Path.Path);
    }

    [Fact]
    public async Task Files_are_read_with_cat_and_written_through_exec_with_their_time()
    {
        var docker = new FakeDocker();
        docker.Reply(a => a[^1].StartsWith("f='/app/a.txt'", StringComparison.Ordinal), "0/12/1763800000/regular file/a.txt\n");
        var provider = new DockerFileSystemProvider(docker);
        var file = VfsPath.Create("docker", "web", "/app/a.txt");

        await using (var read = await provider.OpenReadAsync(file, CancellationToken.None))
        {
            Assert.Equal("file content", await new StreamReader(read).ReadToEndAsync());
        }
        Assert.Equal(["exec", "web", "cat", "--", "/app/a.txt"], docker.Calls[^1].Arguments);

        // a new file: its bytes go to "cat > path" through exec -i, then the copy's time is set with touch
        var target = VfsPath.Create("docker", "web", "/app/it's new.txt");
        var time = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        provider.PrepareUpload(target, time);
        await using (var write = await provider.OpenWriteAsync(target, overwrite: false, CancellationToken.None))
        {
            await write.WriteAsync("héllo"u8.ToArray());
        }
        var upload = docker.Calls.Single(c => c.Arguments is ["exec", "-i", ..]);
        Assert.Equal(["exec", "-i", "web", "sh", "-c", "cat > '/app/it'\\''s new.txt'"], upload.Arguments);
        Assert.Equal("héllo"u8.ToArray(), upload.Input);
        Assert.Equal(PosixShell.TouchCommand("/app/it's new.txt", time), docker.Calls[^1].Arguments[^1]);

        // an existing file is not overwritten unless asked
        await Assert.ThrowsAsync<IOException>(() => provider.OpenWriteAsync(file, overwrite: false, CancellationToken.None));

        // changes are shell commands; containers themselves are left to docker
        docker.Calls.Clear();
        await provider.DeleteAsync(file, recursive: false, CancellationToken.None);
        Assert.Equal("rm -f -- '/app/a.txt'", docker.Calls[^1].Arguments[^1]);
        await provider.MoveAsync(file, VfsPath.Create("docker", "web", "/app/b.txt"), overwrite: false, CancellationToken.None);
        Assert.Equal("mv -f -- '/app/a.txt' '/app/b.txt'", docker.Calls[^1].Arguments[^1]);
        await Assert.ThrowsAsync<CrossDeviceMoveException>(() =>
            provider.MoveAsync(file, VfsPath.Create("docker", "db", "/a.txt"), overwrite: false, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DeleteAsync(VfsPath.Create("docker", "web", "/"), true, CancellationToken.None));
    }

    [Fact]
    public async Task The_settings_choose_the_user_commands_run_as()
    {
        var docker = new FakeDocker();
        docker.Reply(a => a[^1].StartsWith("f='/a.txt'", StringComparison.Ordinal), "0/3/1763800000/regular file/a.txt\n");
        var provider = new DockerFileSystemProvider(docker);
        Assert.Equal("Docker", provider.SettingsTitle);
        var file = VfsPath.Create("docker", "web", "/a.txt");
        try
        {
            provider.Apply(new NextCommander.Core.Plugins.PluginSettingValues(provider.Settings, new Dictionary<string, string> { ["user"] = "root" }));
            await (await provider.OpenReadAsync(file, CancellationToken.None)).DisposeAsync();
            Assert.Equal(["exec", "-u", "root", "web", "cat", "--", "/a.txt"], docker.Calls[^1].Arguments);
            await using (var write = await provider.OpenWriteAsync(VfsPath.Create("docker", "web", "/b.txt"), overwrite: false, CancellationToken.None))
            {
                await write.WriteAsync("x"u8.ToArray());
            }
            Assert.Contains(docker.Calls, c => c.Arguments is ["exec", "-i", "-u", "root", "web", "sh", "-c", "cat > '/b.txt'"]);

            // the default: the container's own user; a program that is not there is refused
            provider.Apply(new NextCommander.Core.Plugins.PluginSettingValues(provider.Settings, null));
            await (await provider.OpenReadAsync(file, CancellationToken.None)).DisposeAsync();
            Assert.Equal(["exec", "web", "cat", "--", "/a.txt"], docker.Calls[^1].Arguments);
            Assert.NotNull(provider.Validate(new NextCommander.Core.Plugins.PluginSettingValues(provider.Settings,
                new Dictionary<string, string> { ["program"] = Path.Combine(Path.GetTempPath(), "no-docker.exe") })));
        }
        finally
        {
            DockerTool.ConfiguredPath = null;
        }
    }

    [Fact]
    public void Docker_errors_say_what_to_do()
    {
        var path = VfsPath.Create("docker", "web", "/app");
        Exception Of(string error) => DockerFileSystemProvider.ErrorOf(new DockerResult(1, string.Empty, error), path);

        Assert.Contains("start Docker Desktop", Of("failed to connect to the docker API at npipe:////./pipe/dockerDesktopLinuxEngine").Message, StringComparison.Ordinal);
        Assert.Contains("start Docker Desktop", Of("Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?").Message, StringComparison.Ordinal);
        Assert.Contains("docker start web", Of("Error response from daemon: container abc is not running").Message, StringComparison.Ordinal);
        Assert.IsType<DirectoryNotFoundException>(Of("Error response from daemon: No such container: web"));
        Assert.Contains("no shell", Of("OCI runtime exec failed: exec failed: unable to start container process: exec: \"sh\": executable file not found in $PATH: unknown").Message, StringComparison.Ordinal);
        Assert.IsType<FileNotFoundException>(Of("sh: cd: can't cd to /app: No such file or directory"));
        Assert.IsType<UnauthorizedAccessException>(Of("sh: can't create /etc/x: Permission denied"));
        Assert.Equal("docker: something else", Of("something else").Message);

        Assert.NotNull(new DockerFileSystemProvider(null).Unavailable);
        Assert.Equal(DateTime.MinValue, DockerFileSystemProvider.ParseCreated("yesterday"));
    }

    [Fact]
    public async Task A_real_container_works_like_a_folder()
    {
        if (Environment.GetEnvironmentVariable("FM_TEST_DOCKER") != "1" || DockerTool.Installed is not { } docker)
        {
            return;
        }
        var name = "nc-test-" + Guid.NewGuid().ToString("N")[..8];
        var started = await docker.RunAsync(["run", "-d", "--name", name, "alpine:3", "sleep", "600"], null, CancellationToken.None);
        Assert.True(started.ExitCode == 0, started.Error);
        try
        {
            var provider = new DockerFileSystemProvider(docker);
            Assert.Contains(name, (await provider.ListAsync(DockerFileSystemProvider.Root, CancellationToken.None)).Select(e => e.Name));
            var root = VfsPath.Create("docker", name, "/");
            Assert.Contains("etc", (await provider.ListAsync(root, CancellationToken.None)).Select(e => e.Name));

            var folder = root.Combine("tmp").Combine("a folder");
            await provider.CreateDirectoryAsync(folder, CancellationToken.None);
            var file = folder.Combine("it's ğ.txt");
            var time = new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Local);
            provider.PrepareUpload(file, time);
            var bytes = Enumerable.Range(0, 70000).Select(i => (byte)i).ToArray();
            await using (var write = await provider.OpenWriteAsync(file, overwrite: false, CancellationToken.None))
            {
                await write.WriteAsync(bytes);
            }
            var entry = await provider.GetEntryAsync(file, CancellationToken.None);
            Assert.Equal(bytes.Length, entry!.Size);
            Assert.Equal(time, entry.LastWriteTime);
            await using (var read = await provider.OpenReadAsync(file, CancellationToken.None))
            {
                using var copy = new MemoryStream();
                await read.CopyToAsync(copy);
                Assert.Equal(bytes, copy.ToArray());
            }
            var renamed = folder.Combine("b.txt");
            await provider.MoveAsync(file, renamed, overwrite: false, CancellationToken.None);
            Assert.Equal(["b.txt"], (await provider.ListAsync(folder, CancellationToken.None)).Select(e => e.Name));
            await provider.DeleteAsync(folder, recursive: true, CancellationToken.None);
            Assert.Null(await provider.GetEntryAsync(folder, CancellationToken.None));

            // a stopped container says how to start it
            await docker.RunAsync(["stop", "-t", "0", name], null, CancellationToken.None);
            var error = await Assert.ThrowsAsync<IOException>(() => provider.ListAsync(root, CancellationToken.None));
            Assert.Contains("docker start " + name, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            await docker.RunAsync(["rm", "-f", name], null, CancellationToken.None);
        }
    }
}
