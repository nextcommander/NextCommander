using System.Text;
using NextCommander.Core.Plugins;
using NextCommander.Core.Vfs;
using NextCommander.Plugin.Kubernetes;

namespace NextCommander.Core.Tests;

/// <summary>
/// The Kubernetes plugin with a fake kubectl (the commands it runs, what it makes of their output and errors), and
/// against a real cluster when FM_TEST_K8S=1 and kubectl reaches one (it starts an alpine pod and deletes it).
/// </summary>
public sealed class KubernetesPluginTests
{
    /// <summary>Answers each kubectl command with the first matching reply and records the commands and their input.</summary>
    private sealed class FakeKubectl : IKubectlRunner
    {
        public List<(Func<IReadOnlyList<string>, bool> When, KubectlResult Result)> Replies { get; } = [];

        public List<(IReadOnlyList<string> Arguments, byte[]? Input)> Calls { get; } = [];

        public void Reply(Func<IReadOnlyList<string>, bool> when, string output, int exitCode = 0, string error = "") =>
            Replies.Add((when, new KubectlResult(exitCode, output, error)));

        public async Task<KubectlResult> RunAsync(IReadOnlyList<string> arguments, Stream? input, CancellationToken cancellationToken)
        {
            byte[]? data = null;
            if (input is not null)
            {
                using var copy = new MemoryStream();
                await input.CopyToAsync(copy, cancellationToken);
                data = copy.ToArray();
            }
            lock (Calls)
            {
                Calls.Add((arguments, data));
            }
            return Replies.FirstOrDefault(r => r.When(arguments)).Result ?? new KubectlResult(0, string.Empty, string.Empty);
        }

        public Stream OpenOutput(IReadOnlyList<string> arguments)
        {
            lock (Calls)
            {
                Calls.Add((arguments, null));
            }
            return new MemoryStream(Encoding.UTF8.GetBytes("file content"));
        }
    }

    /// <summary>Says yes (or no) to every question and keeps the texts shown.</summary>
    private sealed class FakeDialogs(bool confirm = true) : IPluginDialogs
    {
        public List<string> Questions { get; } = [];

        public List<(string FileName, string Text)> Shown { get; } = [];

        public Task<string?> PromptAsync(string title, string message, string initialText) => Task.FromResult<string?>(null);

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> choices) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message)
        {
            Questions.Add(message);
            return Task.FromResult(confirm);
        }

        public Task ShowTextAsync(string fileName, string text)
        {
            Shown.Add((fileName, text));
            return Task.CompletedTask;
        }
    }

    private const string ConfigView = """
        {"kind":"Config","apiVersion":"v1","current-context":"kind-dev",
         "contexts":[
          {"name":"kind-dev","context":{"cluster":"kind-dev","user":"kind-dev"}},
          {"name":"arn:aws:eks:eu-west-1:123:cluster/prod","context":{"cluster":"prod","user":"prod","namespace":"shop"}}]}
        """;

    private const string Namespaces = """
        {"kind":"List","items":[
         {"metadata":{"name":"kube-system","creationTimestamp":"2026-11-01T08:00:00Z"},"status":{"phase":"Active"}},
         {"metadata":{"name":"default","creationTimestamp":"2026-11-01T08:00:00Z"},"status":{"phase":"Active"}}]}
        """;

    /// <summary>A pod of the deployment "web" (through its replica set) with two containers.</summary>
    private const string WebPod = """
        {"metadata":{"name":"web-6d4cf56db6-abcde","creationTimestamp":"2026-11-22T10:15:30Z",
                     "ownerReferences":[{"kind":"ReplicaSet","name":"web-6d4cf56db6","controller":true}]},
         "spec":{"nodeName":"node-1","containers":[{"name":"nginx"},{"name":"log-shipper"}]},
         "status":{"phase":"Running","containerStatuses":[
           {"name":"nginx","ready":true,"restartCount":1,"state":{"running":{"startedAt":"2026-11-22T10:16:00Z"}}},
           {"name":"log-shipper","ready":true,"restartCount":2,"state":{"running":{"startedAt":"2026-11-22T10:16:05Z"}}}]}}
        """;

    private const string Pods = """{"kind":"List","items":[""" + WebPod + """
        ,
         {"metadata":{"name":"batch-x","creationTimestamp":"2026-11-20T08:00:00Z","ownerReferences":[{"kind":"Job","name":"batch","controller":true}]},
          "spec":{"containers":[{"name":"main"}]},
          "status":{"phase":"Running","containerStatuses":[
            {"name":"main","ready":false,"restartCount":7,"state":{"waiting":{"reason":"CrashLoopBackOff"}}}]}},
         {"metadata":{"name":"old","creationTimestamp":"2026-11-19T08:00:00Z","deletionTimestamp":"2026-11-23T08:00:00Z"},
          "spec":{"containers":[{"name":"app"}]},"status":{"phase":"Running"}}]}
        """;

    private const string ReplicaSets = """
        {"kind":"List","items":[
         {"metadata":{"name":"web-6d4cf56db6","ownerReferences":[{"kind":"Deployment","name":"web","controller":true}]}}]}
        """;

    /// <summary>A kubectl that knows the contexts, namespaces, pods and replica sets above.</summary>
    private static FakeKubectl Cluster()
    {
        var kubectl = new FakeKubectl();
        kubectl.Reply(a => a.Contains("config") && a.Contains("view"), ConfigView);
        kubectl.Reply(a => a.Contains("get") && a.Contains("namespaces"), Namespaces);
        kubectl.Reply(a => a.Contains("get") && a.Contains("pods"), Pods);
        kubectl.Reply(a => a.Contains("get") && a.Contains("replicasets"), ReplicaSets);
        return kubectl;
    }

    private static VfsPath K8s(string context, string path) => VfsPath.Create("k8s", context, path);

    [Fact]
    public async Task Contexts_namespaces_pods_and_containers_are_folders()
    {
        var kubectl = Cluster();
        var provider = new KubernetesFileSystemProvider(kubectl);

        // the contexts, the '/' of an EKS name as '|'; the current one in the status column
        var contexts = await provider.ListAsync(KubernetesFileSystemProvider.Root, CancellationToken.None);
        Assert.Equal(["arn:aws:eks:eu-west-1:123:cluster|prod", "kind-dev"], contexts.Select(e => e.Name));
        Assert.Equal(K8s("arn:aws:eks:eu-west-1:123:cluster|prod", "/"), contexts[0].Path);
        var columns = new KubernetesColumns();
        Assert.Equal("Current context", (await columns.GetValuesAsync(contexts[1], [KubernetesColumns.StatusField], null!, CancellationToken.None))[KubernetesColumns.StatusField]);
        Assert.Null((await columns.GetValuesAsync(contexts[0], [KubernetesColumns.StatusField], null!, CancellationToken.None))[KubernetesColumns.StatusField]);
        // ".." from a context goes back to the list, onto it; deeper levels have parents of their own
        Assert.Equal((KubernetesFileSystemProvider.Root, "kind-dev"), provider.OriginOf(contexts[1].Path));
        Assert.Null(provider.OriginOf(K8s("kind-dev", "/default")));

        // the namespaces, with the context's name as kubectl knows it
        var namespaces = await provider.ListAsync(K8s("arn:aws:eks:eu-west-1:123:cluster|prod", "/"), CancellationToken.None);
        Assert.Equal(["default", "kube-system"], namespaces.Select(e => e.Name));
        Assert.Equal(["--context", "arn:aws:eks:eu-west-1:123:cluster/prod", "get", "namespaces", "-o", "json", "--request-timeout=30s"], kubectl.Calls[^1].Arguments);

        // the pods: their creation as the time, kubectl's status, ready containers, restarts and node in the columns
        var pods = await provider.ListAsync(K8s("kind-dev", "/default"), CancellationToken.None);
        Assert.Equal(["batch-x", "old", "web-6d4cf56db6-abcde"], pods.Select(e => e.Name));
        Assert.All(pods, p => Assert.True(p.IsDirectory));
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 10, 15, 30, TimeSpan.Zero).LocalDateTime, pods[2].LastWriteTime);
        Assert.Contains(kubectl.Calls, c => c.Arguments is ["--context", "kind-dev", "-n", "default", "get", "pods", "-o", "json", "--request-timeout=30s"]);
        async Task<object?> Column(VfsEntry entry, string field) => (await columns.GetValuesAsync(entry, [field], null!, CancellationToken.None))[field];
        Assert.Equal("CrashLoopBackOff", await Column(pods[0], KubernetesColumns.StatusField));
        Assert.Equal("Terminating", await Column(pods[1], KubernetesColumns.StatusField));
        Assert.Equal("Running", await Column(pods[2], KubernetesColumns.StatusField));
        Assert.Equal("2/2", await Column(pods[2], KubernetesColumns.ReadyField));
        Assert.Equal(3, await Column(pods[2], KubernetesColumns.RestartsField));
        Assert.Equal("node-1", await Column(pods[2], KubernetesColumns.NodeField));

        // a pod's containers: always a level of their own, also for one container
        kubectl.Reply(a => a.Contains("get") && a.Contains("pod") && a.Contains("web-6d4cf56db6-abcde"), "{\"kind\":\"Pod\"," + WebPod[1..]);
        var containers = await provider.ListAsync(pods[2].Path, CancellationToken.None);
        Assert.Equal(["nginx", "log-shipper"], containers.Select(e => e.Name));
        Assert.Equal(K8s("kind-dev", "/default/web-6d4cf56db6-abcde/nginx"), containers[0].Path);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 10, 16, 0, TimeSpan.Zero).LocalDateTime, containers[0].LastWriteTime);
        Assert.Equal(2, await Column(containers[1], KubernetesColumns.RestartsField));
        Assert.Equal("Kubernetes", (await provider.GetEntryAsync(KubernetesFileSystemProvider.Root, CancellationToken.None))!.Name);
        Assert.Equal("nginx", (await provider.GetEntryAsync(containers[0].Path, CancellationToken.None))!.Name);
    }

    [Fact]
    public async Task A_containers_files_are_listed_read_and_written_through_exec()
    {
        var kubectl = new FakeKubectl();
        kubectl.Reply(a => a is [.., "exec", "web", "-c", "nginx", "--", "sh", "-c", _] && a[^1].StartsWith("cd -- '/etc'", StringComparison.Ordinal),
            "1/4096/1763800000/directory/nginx\n0/12/1763800000/regular file/hostname\n0/7/1763800000/symbolic link/my file\n");
        kubectl.Reply(a => a[^1].StartsWith("f='/app/a.txt'", StringComparison.Ordinal), "0/12/1763800000/regular file/a.txt\n");
        var provider = new KubernetesFileSystemProvider(kubectl);
        var container = K8s("kind-dev", "/default/web/nginx");

        var etc = await provider.ListAsync(container.Combine("etc"), CancellationToken.None);
        Assert.Equal(["nginx", "hostname", "my file"], etc.Select(e => e.Name));
        Assert.True(etc[0].IsDirectory);
        Assert.Equal(12, etc[1].Size);
        Assert.True(etc[2].IsLink);
        Assert.Equal("/default/web/nginx/etc/my file", etc[2].Path.Path);
        Assert.Equal(["--context", "kind-dev", "-n", "default", "exec", "web", "-c", "nginx", "--", "sh", "-c", PosixShell.ListCommand("/etc")], kubectl.Calls[^1].Arguments);

        var file = container.Combine("app").Combine("a.txt");
        await using (var read = await provider.OpenReadAsync(file, CancellationToken.None))
        {
            Assert.Equal("file content", await new StreamReader(read).ReadToEndAsync());
        }
        Assert.Equal(["--context", "kind-dev", "-n", "default", "exec", "web", "-c", "nginx", "--", "cat", "--", "/app/a.txt"], kubectl.Calls[^1].Arguments);

        // a new file: its bytes go to "cat > path" through exec -i, then the copy's time is set with touch
        var target = container.Combine("app").Combine("it's new.txt");
        var time = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        provider.PrepareUpload(target, time);
        await using (var write = await provider.OpenWriteAsync(target, overwrite: false, CancellationToken.None))
        {
            await write.WriteAsync("héllo"u8.ToArray());
        }
        var upload = kubectl.Calls.Single(c => c.Arguments.Contains("-i"));
        Assert.Equal(["--context", "kind-dev", "-n", "default", "exec", "-i", "web", "-c", "nginx", "--", "sh", "-c", "cat > '/app/it'\\''s new.txt'"], upload.Arguments);
        Assert.Equal("héllo"u8.ToArray(), upload.Input);
        Assert.Equal(PosixShell.TouchCommand("/app/it's new.txt", time), kubectl.Calls[^1].Arguments[^1]);

        // an existing file is not overwritten unless asked
        await Assert.ThrowsAsync<IOException>(() => provider.OpenWriteAsync(file, overwrite: false, CancellationToken.None));

        // changes are shell commands; namespaces, pods and containers themselves are left to kubectl
        kubectl.Calls.Clear();
        await provider.CreateDirectoryAsync(container.Combine("new"), CancellationToken.None);
        Assert.Equal("mkdir -p -- '/new'", kubectl.Calls[^1].Arguments[^1]);
        await provider.DeleteAsync(file, recursive: false, CancellationToken.None);
        Assert.Equal("rm -f -- '/app/a.txt'", kubectl.Calls[^1].Arguments[^1]);
        await provider.MoveAsync(file, container.Combine("app").Combine("b.txt"), overwrite: false, CancellationToken.None);
        Assert.Equal("mv -f -- '/app/a.txt' '/app/b.txt'", kubectl.Calls[^1].Arguments[^1]);
        await Assert.ThrowsAsync<CrossDeviceMoveException>(() =>
            provider.MoveAsync(file, K8s("kind-dev", "/default/web/sidecar/a.txt"), overwrite: false, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DeleteAsync(container, true, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DeleteAsync(K8s("kind-dev", "/default/web"), true, CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CreateDirectoryAsync(K8s("kind-dev", "/new-namespace"), CancellationToken.None));
    }

    [Fact]
    public async Task Pods_have_logs_description_deletion_and_a_deployment_restart()
    {
        var kubectl = Cluster();
        kubectl.Reply(a => a.Contains("logs"), "line 1\nline 2\n");
        kubectl.Reply(a => a.Contains("describe"), "Name: web-6d4cf56db6-abcde\n");
        var provider = new KubernetesFileSystemProvider(kubectl);
        var folder = K8s("kind-dev", "/default");
        var pods = await provider.ListAsync(folder, CancellationToken.None);
        var dialogs = new FakeDialogs();
        var web = pods.Single(p => p.Name == "web-6d4cf56db6-abcde");
        var batch = pods.Single(p => p.Name == "batch-x");

        // only in the k8s file system, only for pods
        Assert.Empty(provider.ActionsFor(VfsPath.Local(Path.GetTempPath()), [], dialogs));
        Assert.Empty(provider.ActionsFor(K8s("kind-dev", "/"), [new VfsEntry { Name = "default", Path = folder, IsDirectory = true }], dialogs));
        Assert.Empty(provider.ActionsFor(folder, [], dialogs));

        // a pod of a deployment (through its replica set) can restart it; a job's cannot
        var actions = provider.ActionsFor(folder, [web], dialogs);
        Assert.Equal(["Pod logs", "Describe the pod", "Restart the deployment", "Delete the pod..."], actions.Select(a => a.Name));
        Assert.Equal(["Pod logs", "Describe the pod", "Delete the pod..."], provider.ActionsFor(folder, [batch], dialogs).Select(a => a.Name));
        Assert.Equal(["Delete the pods..."], provider.ActionsFor(folder, [web, batch], dialogs).Select(a => a.Name));

        // the logs of every container (two), the last lines as the settings say, in the viewer
        Assert.Null(await actions[0].RunAsync(CancellationToken.None));
        Assert.Equal(["--context", "kind-dev", "-n", "default", "logs", "web-6d4cf56db6-abcde", "--all-containers=true", "--prefix=true", "--tail=500"], kubectl.Calls[^1].Arguments);
        Assert.Equal(("web-6d4cf56db6-abcde.log", "line 1\nline 2\n"), dialogs.Shown[^1]);
        Assert.Null(await actions[1].RunAsync(CancellationToken.None));
        Assert.Equal(["--context", "kind-dev", "-n", "default", "describe", "pod", "web-6d4cf56db6-abcde"], kubectl.Calls[^1].Arguments);
        Assert.Equal("web-6d4cf56db6-abcde.txt", dialogs.Shown[^1].FileName);

        // restart and delete ask first
        Assert.Null(await actions[2].RunAsync(CancellationToken.None));
        Assert.Contains("web", dialogs.Questions[^1], StringComparison.Ordinal);
        Assert.Equal(["--context", "kind-dev", "-n", "default", "rollout", "restart", "deployment/web"], kubectl.Calls[^1].Arguments);
        Assert.Null(await provider.ActionsFor(folder, [web, batch], dialogs)[0].RunAsync(CancellationToken.None));
        Assert.Equal(["--context", "kind-dev", "-n", "default", "delete", "pod", "web-6d4cf56db6-abcde", "batch-x", "--wait=false"], kubectl.Calls[^1].Arguments);
        var calls = kubectl.Calls.Count;
        var no = new FakeDialogs(confirm: false);
        Assert.Null(await provider.ActionsFor(folder, [batch], no).Single(a => a.Name == "Delete the pod...").RunAsync(CancellationToken.None));
        Assert.Single(no.Questions);
        Assert.Equal(calls, kubectl.Calls.Count);

        // inside a pod: its logs (of the container the folder is in) and its description
        var inside = provider.ActionsFor(web.Path.Combine("nginx").Combine("etc"), [], dialogs);
        Assert.Equal(["Pod logs", "Describe the pod"], inside.Select(a => a.Name));
        await inside[0].RunAsync(CancellationToken.None);
        Assert.Equal(["--context", "kind-dev", "-n", "default", "logs", "web-6d4cf56db6-abcde", "-c", "nginx", "--tail=500"], kubectl.Calls[^1].Arguments);
        Assert.Equal("web-6d4cf56db6-abcde-nginx.log", dialogs.Shown[^1].FileName);

        // no lines: said instead of an empty viewer
        kubectl.Replies.Insert(0, (a => a.Contains("logs"), new KubectlResult(0, "\n", string.Empty)));
        Assert.Equal("The pod has written no log lines.", await provider.ActionsFor(folder, [batch], dialogs)[0].RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_account_that_may_not_list_namespaces_gets_its_contexts_own()
    {
        var kubectl = new FakeKubectl();
        kubectl.Reply(a => a.Contains("config") && a.Contains("view"), ConfigView);
        kubectl.Reply(a => a.Contains("namespaces"), string.Empty, 1,
            "Error from server (Forbidden): namespaces is forbidden: User \"dev\" cannot list resource \"namespaces\" in API group \"\" at the cluster scope");
        var provider = new KubernetesFileSystemProvider(kubectl);

        Assert.Equal(["shop"], (await provider.ListAsync(K8s("arn:aws:eks:eu-west-1:123:cluster|prod", "/"), CancellationToken.None)).Select(e => e.Name));
        Assert.Equal(["default"], (await provider.ListAsync(K8s("kind-dev", "/"), CancellationToken.None)).Select(e => e.Name));

        // no contexts at all: said how to get one
        var empty = new FakeKubectl();
        empty.Reply(a => a is ["config", ..], "{\"kind\":\"Config\",\"contexts\":null}");
        var error = await Assert.ThrowsAsync<IOException>(() => new KubernetesFileSystemProvider(empty).ListAsync(KubernetesFileSystemProvider.Root, CancellationToken.None));
        Assert.Contains("no contexts", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_settings_name_the_kubeconfig_and_the_log_lines()
    {
        var kubectl = Cluster();
        kubectl.Reply(a => a.Contains("logs"), "x\n");
        var provider = new KubernetesFileSystemProvider(kubectl);
        Assert.Equal("Kubernetes", provider.SettingsTitle);
        var config = Path.GetTempFileName();
        try
        {
            provider.Apply(new PluginSettingValues(provider.Settings, new Dictionary<string, string> { ["kubeconfig"] = config, ["logLines"] = "50" }));
            await provider.ListAsync(KubernetesFileSystemProvider.Root, CancellationToken.None);
            Assert.Equal(["--kubeconfig", config, "config", "view", "-o", "json"], kubectl.Calls[^1].Arguments);
            var folder = K8s("kind-dev", "/default");
            var pods = await provider.ListAsync(folder, CancellationToken.None);
            await provider.ActionsFor(folder, [pods.Single(p => p.Name == "batch-x")], new FakeDialogs())[0].RunAsync(CancellationToken.None);
            Assert.Equal("--tail=50", kubectl.Calls[^1].Arguments[^1]);

            // the defaults: kubectl's own kubeconfig, 500 lines; a program or file that is not there is refused
            provider.Apply(new PluginSettingValues(provider.Settings, null));
            await provider.ListAsync(KubernetesFileSystemProvider.Root, CancellationToken.None);
            Assert.Equal(["config", "view", "-o", "json"], kubectl.Calls[^1].Arguments);
            Assert.Equal(500, provider.LogLines);
            Assert.NotNull(provider.Validate(new PluginSettingValues(provider.Settings,
                new Dictionary<string, string> { ["program"] = Path.Combine(Path.GetTempPath(), "no-kubectl.exe") })));
            Assert.NotNull(provider.Validate(new PluginSettingValues(provider.Settings,
                new Dictionary<string, string> { ["kubeconfig"] = Path.Combine(Path.GetTempPath(), "no-kubeconfig") })));
            Assert.Null(provider.Validate(new PluginSettingValues(provider.Settings, new Dictionary<string, string> { ["kubeconfig"] = config })));
        }
        finally
        {
            KubectlTool.ConfiguredPath = null;
            File.Delete(config);
        }
    }

    [Fact]
    public void Kubectl_errors_say_what_to_do()
    {
        var path = K8s("kind-dev", "/default/web/nginx/app");
        Exception Of(string error) => KubernetesFileSystemProvider.ErrorOf(new KubectlResult(1, string.Empty, error), path);

        Assert.Contains("does not answer", Of("Unable to connect to the server: dial tcp 127.0.0.1:6443: connectex: No connection could be made").Message, StringComparison.Ordinal);
        Assert.Contains("log in again", Of("error: You must be logged in to the server (Unauthorized)").Message, StringComparison.Ordinal);
        Assert.IsType<DirectoryNotFoundException>(Of("error: context was not found for specified context: kind-dev"));
        var forbidden = Of("Error from server (Forbidden): pods \"web\" is forbidden: User \"dev\" cannot create resource \"pods/exec\" in API group \"\" in the namespace \"default\"");
        Assert.IsType<UnauthorizedAccessException>(forbidden, exactMatch: false);
        Assert.Contains("administrator", forbidden.Message, StringComparison.Ordinal);
        Assert.Contains("not running in the pod web", Of("error: cannot exec into a container in a completed pod; current phase is Succeeded").Message, StringComparison.Ordinal);
        Assert.Contains("not running", Of("error: unable to upgrade connection: container not found (\"nginx\")").Message, StringComparison.Ordinal);
        Assert.IsType<DirectoryNotFoundException>(Of("Error from server (NotFound): pods \"web\" not found"));
        Assert.Contains("no shell", Of("error: Internal error occurred: error executing command in container: failed to exec in container: OCI runtime exec failed: exec failed: unable to start container process: exec: \"sh\": executable file not found in $PATH: unknown").Message, StringComparison.Ordinal);
        Assert.IsType<FileNotFoundException>(Of("sh: cd: can't cd to /app: No such file or directory\ncommand terminated with exit code 1"));
        Assert.IsType<UnauthorizedAccessException>(Of("sh: can't create /etc/x: Permission denied\ncommand terminated with exit code 1"));
        Assert.Equal("kubectl: something else", Of("something else").Message);

        Assert.NotNull(new KubernetesFileSystemProvider(null).Unavailable);
        Assert.Equal(DateTime.MinValue, KubernetesFileSystemProvider.ParseTime("yesterday"));
        Assert.Equal("a/b", KubernetesFileSystemProvider.Decode(KubernetesFileSystemProvider.Encode("a/b")));
    }

    [Fact]
    public async Task A_real_pod_works_like_a_folder()
    {
        if (Environment.GetEnvironmentVariable("FM_TEST_K8S") != "1" || KubectlTool.Installed is not { } kubectl)
        {
            return;
        }
        var provider = new KubernetesFileSystemProvider(kubectl);
        var contexts = await provider.ContextsAsync(CancellationToken.None);
        var context = contexts.Single(c => c.IsCurrent).Name;
        var @namespace = Environment.GetEnvironmentVariable("FM_TEST_K8S_NAMESPACE") is { Length: > 0 } ns ? ns : "default";
        var name = "nc-test-" + Guid.NewGuid().ToString("N")[..8];
        string[] At(params string[] arguments) => ["--context", context, "-n", @namespace, .. arguments];
        var started = await kubectl.RunAsync(At("run", name, "--image=alpine:3", "--restart=Never", "--", "sleep", "600"), null, CancellationToken.None);
        Assert.True(started.ExitCode == 0, started.Error);
        try
        {
            var ready = await kubectl.RunAsync(At("wait", "--for=condition=Ready", "pod/" + name, "--timeout=180s"), null, CancellationToken.None);
            Assert.True(ready.ExitCode == 0, ready.Error);
            var folder = VfsPath.Create("k8s", KubernetesFileSystemProvider.Encode(context), "/" + @namespace);
            Assert.Contains(name, (await provider.ListAsync(folder, CancellationToken.None)).Select(e => e.Name));
            var root = folder.Combine(name).Combine(name); // "kubectl run" names the container like the pod
            Assert.Equal([name], (await provider.ListAsync(folder.Combine(name), CancellationToken.None)).Select(e => e.Name));
            Assert.Contains("etc", (await provider.ListAsync(root, CancellationToken.None)).Select(e => e.Name));

            var dir = root.Combine("tmp").Combine("a folder");
            await provider.CreateDirectoryAsync(dir, CancellationToken.None);
            var file = dir.Combine("it's ğ.txt");
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
            var renamed = dir.Combine("b.txt");
            await provider.MoveAsync(file, renamed, overwrite: false, CancellationToken.None);
            Assert.Equal(["b.txt"], (await provider.ListAsync(dir, CancellationToken.None)).Select(e => e.Name));
            await provider.DeleteAsync(dir, recursive: true, CancellationToken.None);
            Assert.Null(await provider.GetEntryAsync(dir, CancellationToken.None));

            // the description through the menu, then the pod is deleted through it
            var dialogs = new FakeDialogs();
            var pod = (await provider.ListAsync(folder, CancellationToken.None)).Single(e => e.Name == name);
            var actions = provider.ActionsFor(folder, [pod], dialogs);
            await actions.Single(a => a.Name == "Describe the pod").RunAsync(CancellationToken.None);
            Assert.Contains(name, dialogs.Shown[^1].Text, StringComparison.Ordinal);
            await actions.Single(a => a.Name == "Delete the pod...").RunAsync(CancellationToken.None);
        }
        finally
        {
            await kubectl.RunAsync(At("delete", "pod", name, "--wait=false", "--ignore-not-found"), null, CancellationToken.None);
        }
    }
}
