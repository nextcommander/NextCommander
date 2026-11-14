using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Platform.Adb;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

/// <summary>
/// Android phones over ADB: the device list (adb's text and the server's host protocol), finding adb, errors, and the
/// provider against a fake adb that runs the phone's shell commands in a real POSIX shell on a temporary folder.
/// </summary>
public sealed class AdbTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fm-adb-" + Guid.NewGuid().ToString("N")[..8]);

    public AdbTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Device_list()
    {
        var devices = AdbBridge.ParseDevices("""
            * daemon started successfully
            List of devices attached
            R58M12345      device usb:1-1 product:beyond1lte model:SM_G973F device:beyond1 transport_id:3
            192.168.1.5:5555 unauthorized usb:1-2 transport_id:4
            emulator-5554  offline

            """);
        Assert.Equal(["R58M12345", "192.168.1.5:5555", "emulator-5554"], devices.Select(d => d.Serial));
        Assert.Equal(["SM G973F", "192.168.1.5:5555", "emulator-5554"], devices.Select(d => d.Name));
        Assert.Equal([true, false, false], devices.Select(d => d.IsReady));
    }

    [Fact]
    public void Adb_is_found_in_the_usual_places()
    {
        var env = new Dictionary<string, string?> { ["PATH"] = @"C:\Tools;C:\Other", ["LOCALAPPDATA"] = @"C:\Users\me\AppData\Local", ["USERPROFILE"] = @"C:\Users\me" };
        var sdk = Path.Combine(@"C:\Users\me\AppData\Local", "Android", "Sdk", "platform-tools", "adb.exe");
        Assert.Equal(sdk, AdbBridge.FindTool(n => env.GetValueOrDefault(n), p => p == sdk, windows: true, mac: false));
        Assert.Equal(Path.Combine(@"C:\Other", "adb.exe"), AdbBridge.FindTool(n => env.GetValueOrDefault(n), p => p == sdk || p == Path.Combine(@"C:\Other", "adb.exe"), windows: true, mac: false));
        env["FM_ADB"] = @"D:\adb\adb.exe";
        Assert.Equal(@"D:\adb\adb.exe", AdbBridge.FindTool(n => env.GetValueOrDefault(n), _ => true, windows: true, mac: false));
        Assert.Null(AdbBridge.FindTool(n => n == "HOME" ? "/home/me" : null, _ => false, windows: false, mac: false));
        Assert.Equal("/home/me/Android/Sdk/platform-tools/adb".Replace('/', Path.DirectorySeparatorChar),
            AdbBridge.FindTool(n => n == "HOME" ? "/home/me" : null, p => p.EndsWith("adb", StringComparison.Ordinal), windows: false, mac: false)?.Replace('/', Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task Devices_come_from_the_adb_server()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var header = new byte[4];
            await stream.ReadExactlyAsync(header);
            var request = new byte[Convert.ToInt32(Encoding.ASCII.GetString(header), 16)];
            await stream.ReadExactlyAsync(request);
            Assert.Equal("host:devices-l", Encoding.ASCII.GetString(request));
            var payload = "R58M12345\tdevice product:x model:Pixel_7 device:y\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes("OKAY" + payload.Length.ToString("x4", System.Globalization.CultureInfo.InvariantCulture) + payload));
        });
        var text = await Task.Run(() => AdbBridge.QueryServer("host:devices-l", port));
        await server;
        Assert.Equal("Pixel 7", AdbBridge.ParseDevices(text!).Single().Name);

        listener.Stop();
        Assert.Null(AdbBridge.QueryServer("host:devices-l", port)); // no server: nothing, no waiting
    }

    [Theory]
    [InlineData("adb: error: failed to stat remote object '/sdcard/x': No such file or directory", typeof(FileNotFoundException))]
    [InlineData("rm: /system/a: Read-only file system", typeof(UnauthorizedAccessException))]
    [InlineData("error: device 'R58' not found", typeof(IOException))]
    [InlineData("error: device unauthorized.", typeof(UnauthorizedAccessException))]
    public void Errors_become_exceptions(string message, Type type) =>
        Assert.IsType(type, AdbFileSystemProvider.ErrorOf(new AdbResult(1, string.Empty, message), VfsPath.Create("adb", "R58", "/sdcard/x")));

    [Fact]
    public void Free_space_from_df()
    {
        var space = AdbFileSystemProvider.ParseDf("""
            Filesystem     1K-blocks     Used Available Use% Mounted on
            /dev/fuse      115164992 61234560  53799040  54% /storage/emulated
            """);
        Assert.Equal((115164992L * 1024, 53799040L * 1024, "/storage/emulated"), (space!.Total, space.Free, space.Label));
        Assert.Null(AdbFileSystemProvider.ParseDf("df: /x: No such file or directory"));
    }

    // ---------------------------------------------------------------- the provider against a fake adb

    /// <summary>A POSIX shell here: /bin/sh, or Git for Windows' sh.</summary>
    private static string? Shell() =>
        new[] { "/bin/sh", @"C:\Program Files\Git\usr\bin\sh.exe" }.FirstOrDefault(File.Exists);

    /// <summary>adb for one phone ("PHONE") whose file system is this machine's, through a POSIX shell.</summary>
    private sealed class FakeAdb(string shell) : IAdbRunner
    {
        public List<string> Commands { get; } = [];

        public string Run(string command, out int exitCode, out string error)
        {
            var info = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(command);
            if (OperatingSystem.IsWindows())
            {
                info.Environment["PATH"] = Path.GetDirectoryName(shell) + ";" + info.Environment["PATH"];
            }
            using var process = Process.Start(info)!;
            var errors = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            (exitCode, error) = (process.ExitCode, errors.Result);
            return output;
        }

        /// <summary>The machine's path of a path in the shell (Git for Windows: /c/Users/... → C:\Users\...).</summary>
        public string NativePath(string path) => OperatingSystem.IsWindows() ? Run("cygpath -w " + PosixShell.Quote(path), out _, out _).Trim() : path;

        public Task<AdbResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Assert.Equal(["-s", "PHONE"], arguments.Take(2));
            Commands.Add(arguments[2]);
            switch (arguments[2])
            {
                case "shell":
                    var output = Run(arguments[3], out var exit, out var error);
                    return Task.FromResult(new AdbResult(exit, output, error));
                case "push":
                    var target = NativePath(arguments[4]);
                    if (!Directory.Exists(Path.GetDirectoryName(target)))
                    {
                        return Task.FromResult(new AdbResult(1, string.Empty, $"adb: error: failed to copy '{arguments[3]}' to '{arguments[4]}': remote couldn't create file: No such file or directory"));
                    }
                    File.Copy(arguments[3], target, overwrite: true);
                    File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(arguments[3])); // push keeps the time
                    return Task.FromResult(new AdbResult(0, "1 file pushed.", string.Empty));
            }
            throw new InvalidOperationException(arguments[2]);
        }

        public Task<Stream> OpenOutputAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Assert.Equal(["-s", "PHONE", "exec-out"], arguments.Take(3));
            Commands.Add("exec-out");
            var command = arguments[3];
            Assert.StartsWith("cat -- '", command, StringComparison.Ordinal);
            var path = command[8..^1].Replace("'\\''", "'", StringComparison.Ordinal); // the quoted word back
            return Task.FromResult<Stream>(new MemoryStream(File.ReadAllBytes(NativePath(path))));
        }
    }

    private static async Task WriteAsync(IFileSystemProvider provider, VfsPath path, byte[] data, bool overwrite = true)
    {
        await using var stream = await provider.OpenWriteAsync(path, overwrite, CancellationToken.None);
        await stream.WriteAsync(data);
    }

    private static async Task<byte[]> ReadAsync(IFileSystemProvider provider, VfsPath path)
    {
        await using var stream = await provider.OpenReadAsync(path, CancellationToken.None);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    [Fact]
    public async Task Phone_files_round_trip()
    {
        if (Shell() is not { } shell)
        {
            return; // no POSIX shell on this machine
        }
        var adb = new FakeAdb(shell);
        var bridge = new AdbBridge(adb, () => [new AdbDevice("PHONE", "Pixel 7", "device"), new AdbDevice("OTHER", "Old", "unauthorized")]);
        var provider = new AdbFileSystemProvider(bridge);
        var ct = CancellationToken.None;
        var folder = OperatingSystem.IsWindows() ? adb.Run("cygpath -u " + PosixShell.Quote(_root), out _, out _).Trim() : _root;
        VfsPath P(string relative) => VfsPath.Create("adb", "PHONE", folder + relative);

        // the device list: only the phone that allowed debugging
        Assert.Equal(["Pixel 7"], (await provider.ListAsync(AdbFileSystemProvider.Root, ct)).Select(e => e.Name));
        Assert.Equal(AdbFileSystemProvider.DeviceRoot("PHONE"), (await provider.ListAsync(AdbFileSystemProvider.Root, ct)).Single().Path);

        await provider.CreateDirectoryAsync(P("/DCIM/Camera"), ct);
        var photo = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();
        var time = new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Local);
        provider.PrepareUpload(P("/DCIM/Camera/it's a photo.jpg"), time);
        await WriteAsync(provider, P("/DCIM/Camera/it's a photo.jpg"), photo);
        await WriteAsync(provider, P("/DCIM/note.txt"), Encoding.UTF8.GetBytes("not"));
        await Assert.ThrowsAsync<IOException>(() => WriteAsync(provider, P("/DCIM/note.txt"), [1], overwrite: false));
        await Assert.ThrowsAsync<FileNotFoundException>(() => WriteAsync(provider, P("/missing/x.txt"), [1]));

        var list = await provider.ListAsync(P("/DCIM"), ct);
        Assert.Equal(["Camera", "note.txt"], list.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.True(list.Single(e => e.Name == "Camera").IsDirectory);
        var entry = (await provider.GetEntryAsync(P("/DCIM/Camera/it's a photo.jpg"), ct))!;
        Assert.Equal((photo.Length, time), (entry.Size, entry.LastWriteTime)); // push kept the time
        Assert.Equal(photo, await ReadAsync(provider, P("/DCIM/Camera/it's a photo.jpg")));
        Assert.Null(await provider.GetEntryAsync(P("/DCIM/none.jpg"), ct));
        await Assert.ThrowsAsync<FileNotFoundException>(() => ReadAsync(provider, P("/DCIM/none.jpg")));

        // times, renames, deletes
        var later = new DateTime(2022, 1, 2, 3, 4, 5, DateTimeKind.Local);
        await provider.SetTimesAsync(P("/DCIM/note.txt"), null, later, ct);
        Assert.Equal(later, (await provider.GetEntryAsync(P("/DCIM/note.txt"), ct))!.LastWriteTime);
        await provider.MoveAsync(P("/DCIM/note.txt"), P("/DCIM/Camera/note 2.txt"), overwrite: false, ct);
        Assert.Equal("not", Encoding.UTF8.GetString(await ReadAsync(provider, P("/DCIM/Camera/note 2.txt"))));
        await Assert.ThrowsAsync<CrossDeviceMoveException>(() => provider.MoveAsync(P("/DCIM/Camera"), VfsPath.Create("adb", "OTHER", "/sdcard/x"), false, ct));
        await Assert.ThrowsAsync<IOException>(() => provider.DeleteAsync(P("/DCIM/Camera"), recursive: false, ct));
        await provider.DeleteAsync(P("/DCIM/Camera/note 2.txt"), recursive: false, ct);
        await provider.DeleteAsync(P("/DCIM"), recursive: true, ct);
        Assert.False(Directory.Exists(Path.Combine(_root, "DCIM")));
        Assert.Contains("exec-out", adb.Commands);

        // a copy from the computer keeps the time (announced before the push)
        var local = new LocalFileSystemProvider();
        var source = Path.Combine(_root, "kaynak.txt");
        await File.WriteAllTextAsync(source, "içerik", ct);
        File.SetLastWriteTime(source, new DateTime(2020, 2, 3, 4, 5, 6));
        await provider.CreateDirectoryAsync(P("/Download"), ct);
        var job = new CopyMoveJob(new ProviderRegistry([local, provider]), [(await local.GetEntryAsync(VfsPath.Local(source), ct))!], P("/Download"), move: false);
        await job.RunAsync();
        Assert.True(job.State == JobState.Completed, job.Failure?.ToString() ?? string.Join("; ", job.Errors.Select(e => e.Message)));
        Assert.Equal(new DateTime(2020, 2, 3, 4, 5, 6), (await provider.GetEntryAsync(P("/Download/kaynak.txt"), ct))!.LastWriteTime);
    }
}
