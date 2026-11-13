using System.Text;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Platform.Mtp;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

public sealed class MtpProviderTests : IDisposable
{
    private readonly FakeMtpSource _source = new();
    private readonly MtpFileSystemProvider _provider;
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "fm-mtp-" + Guid.NewGuid().ToString("N")[..8]);

    public MtpProviderTests()
    {
        _provider = new MtpFileSystemProvider(_source);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        _provider.Dispose();
        Directory.Delete(_tmp, recursive: true);
    }

    private static VfsPath P(string path, string device = "Phone") => VfsPath.Parse("mtp://" + device + path);

    private FakeMtpDevice Phone => _source.Devices["phone-1"];

    [Fact]
    public async Task ListsDevicesStoragesAndFolders()
    {
        Phone.AddFile("/Internal storage/DCIM/a.jpg", "photo");
        var devices = await _provider.ListAsync(MtpFileSystemProvider.Root, CancellationToken.None);
        Assert.Equal(["Phone"], devices.Select(d => d.Name));

        var root = await _provider.ListAsync(P("/"), CancellationToken.None);
        var storage = Assert.Single(root);
        Assert.Equal("Internal storage", storage.Name);
        Assert.True(storage.IsDirectory);

        var dcim = await _provider.ListAsync(P("/Internal storage/DCIM"), CancellationToken.None);
        var photo = Assert.Single(dcim);
        Assert.Equal(P("/Internal storage/DCIM/a.jpg"), photo.Path);
        Assert.Equal(5, photo.Size);
        Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 9), photo.LastWriteTime);
    }

    [Fact]
    public async Task ReadsAndWritesFiles()
    {
        Phone.AddFile("/Internal storage/Music/song.mp3", "la la");
        await using (var read = await _provider.OpenReadAsync(P("/Internal storage/Music/song.mp3"), CancellationToken.None))
        {
            using var reader = new StreamReader(read);
            Assert.Equal("la la", await reader.ReadToEndAsync());
        }

        _provider.PrepareUpload(P("/Internal storage/Music/new.txt"), new DateTime(2021, 1, 2, 3, 4, 5));
        await using (var write = await _provider.OpenWriteAsync(P("/Internal storage/Music/new.txt"), overwrite: false, CancellationToken.None))
        {
            await write.WriteAsync(Encoding.UTF8.GetBytes("hello"));
        }
        var created = Phone.Find("/Internal storage/Music/new.txt")!;
        Assert.Equal("hello", Encoding.UTF8.GetString(created.Data));
        Assert.Equal(new DateTime(2021, 1, 2, 3, 4, 5), created.Modified); // the time goes with the upload

        // the listing seen before the upload is not used for the new file
        Assert.Contains(await _provider.ListAsync(P("/Internal storage/Music"), CancellationToken.None), e => e.Name == "new.txt");

        await Assert.ThrowsAsync<IOException>(() => _provider.OpenWriteAsync(P("/Internal storage/Music/new.txt"), overwrite: false, CancellationToken.None));
        await using (var again = await _provider.OpenWriteAsync(P("/Internal storage/Music/new.txt"), overwrite: true, CancellationToken.None))
        {
            await again.WriteAsync(Encoding.UTF8.GetBytes("second"));
        }
        Assert.Equal("second", Encoding.UTF8.GetString(Phone.Find("/Internal storage/Music/new.txt")!.Data));
        Assert.Single(Phone.ChildrenOf("/Internal storage/Music"), n => n.Name == "new.txt");
    }

    [Fact]
    public async Task NothingIsWrittenNextToTheStorages()
    {
        await Assert.ThrowsAsync<IOException>(() => _provider.OpenWriteAsync(P("/loose.txt"), overwrite: false, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => _provider.CreateDirectoryAsync(P("/New"), CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _provider.DeleteAsync(P("/Internal storage"), recursive: true, CancellationToken.None));
    }

    [Fact]
    public async Task CreatesNestedFoldersDeletesAndRenames()
    {
        await _provider.CreateDirectoryAsync(P("/Internal storage/a/b/c"), CancellationToken.None);
        Assert.True(Phone.Find("/Internal storage/a/b/c")!.IsFolder);
        Assert.True((await _provider.GetEntryAsync(P("/Internal storage/a/b"), CancellationToken.None))!.IsDirectory);

        await Assert.ThrowsAsync<IOException>(() => _provider.DeleteAsync(P("/Internal storage/a"), recursive: false, CancellationToken.None));
        await _provider.MoveAsync(P("/Internal storage/a/b"), P("/Internal storage/a/renamed"), overwrite: false, CancellationToken.None);
        Assert.Null(Phone.Find("/Internal storage/a/b"));
        Assert.NotNull(Phone.Find("/Internal storage/a/renamed/c"));
        Assert.NotNull(await _provider.GetEntryAsync(P("/Internal storage/a/renamed/c"), CancellationToken.None));

        await Assert.ThrowsAsync<CrossDeviceMoveException>(() =>
            _provider.MoveAsync(P("/Internal storage/a/renamed"), P("/Internal storage/elsewhere"), overwrite: false, CancellationToken.None));

        await _provider.DeleteAsync(P("/Internal storage/a"), recursive: true, CancellationToken.None);
        Assert.Null(Phone.Find("/Internal storage/a"));
        Assert.Null(await _provider.GetEntryAsync(P("/Internal storage/a"), CancellationToken.None));
    }

    [Fact]
    public async Task FindsFilesAddedOnThePhoneAfterAListing()
    {
        Phone.AddFile("/Internal storage/DCIM/old.jpg", "1");
        await _provider.ListAsync(P("/Internal storage/DCIM"), CancellationToken.None);
        Phone.AddFile("/Internal storage/DCIM/new.jpg", "22"); // a photo taken meanwhile
        Assert.NotNull(await _provider.GetEntryAsync(P("/Internal storage/DCIM/new.jpg"), CancellationToken.None));
    }

    [Fact]
    public async Task UnpluggedDeviceIsReported()
    {
        Phone.AddFile("/Internal storage/x.txt", "x");
        await _provider.ListAsync(P("/Internal storage"), CancellationToken.None);
        _source.Unplug("phone-1");
        var error = await Assert.ThrowsAsync<IOException>(() => _provider.ListAsync(P("/Internal storage"), CancellationToken.None));
        Assert.Contains("disconnected", error.Message, StringComparison.Ordinal);
        Assert.Null(await _provider.GetEntryAsync(P("/Internal storage/x.txt"), CancellationToken.None));
    }

    [Fact]
    public async Task StorageSpaceForTheFooter()
    {
        Assert.Null(await _provider.GetSpaceAsync(P("/Internal storage"), CancellationToken.None)); // the device does not tell
        Phone.Space = (128_000_000_000, 12_000_000_000);
        Phone.AddFolder("/Internal storage/DCIM");
        Assert.Equal(new VolumeSpace("Internal storage", 128_000_000_000, 12_000_000_000),
            await _provider.GetSpaceAsync(P("/Internal storage/DCIM"), CancellationToken.None));
        Assert.Equal(new VolumeSpace("Phone", 128_000_000_000, 12_000_000_000), await _provider.GetSpaceAsync(P("/"), CancellationToken.None));
        _source.Unplug("phone-1");
        Assert.Null(await _provider.GetSpaceAsync(P("/Internal storage"), CancellationToken.None));
    }

    [Fact]
    public async Task WatchedFolderNoticesFilesAddedOnThePhone()
    {
        using var provider = new MtpFileSystemProvider(_source) { WatchInterval = TimeSpan.FromMilliseconds(30) };
        Phone.AddFile("/Internal storage/DCIM/old.jpg", "1");
        var changes = 0;
        using var watch = provider.Watch(P("/Internal storage/DCIM"), () => Interlocked.Increment(ref changes));
        Assert.NotNull(watch);
        await Task.Delay(250);
        Assert.Equal(0, Volatile.Read(ref changes)); // nothing new

        Phone.AddFile("/Internal storage/DCIM/new.jpg", "22"); // a photo taken
        var start = Environment.TickCount64;
        while (Volatile.Read(ref changes) == 0 && Environment.TickCount64 - start < 8000)
        {
            await Task.Delay(20);
        }
        Assert.True(Volatile.Read(ref changes) > 0, "the new photo was not noticed");
        Assert.Null(provider.Watch(MtpFileSystemProvider.Root, () => { })); // the device list is not watched
    }

    [Fact]
    public void DevicesWithTheSameNameGetDistinctNames()
    {
        var names = MtpDeviceNames.MakeUnique([new("a", "Galaxy"), new("b", "Galaxy"), new("c", "Cam/era:1")]).Select(d => d.Name);
        Assert.Equal(["Galaxy", "Galaxy (2)", "Cam era 1"], names);
    }

    [Theory]
    [InlineData("mtp:host=SAMSUNG_SAMSUNG_Android_R5CX10ABC", "SAMSUNG SAMSUNG Android R5CX10ABC")]
    [InlineData("gphoto2:host=%5Busb%3A001%2C007%5D", "Camera [usb:001,007]")]
    [InlineData("smb-share:server=nas,share=photos", null)]
    [InlineData("sftp:host=example.org", null)]
    public void GvfsFolderNames(string folder, string? expected) => Assert.Equal(expected, MtpDeviceNames.FromGvfsFolder(folder));

    [Fact]
    public async Task CopyJobToThePhoneAndBack()
    {
        var local = new LocalFileSystemProvider();
        var registry = new ProviderRegistry([local, _provider]);
        Directory.CreateDirectory(Path.Combine(_tmp, "src", "sub"));
        File.WriteAllText(Path.Combine(_tmp, "src", "a.txt"), "A");
        File.WriteAllText(Path.Combine(_tmp, "src", "sub", "b.txt"), "BB");
        Phone.AddFolder("/Internal storage/Download");

        var folder = await local.GetEntryAsync(VfsPath.Local(Path.Combine(_tmp, "src")), CancellationToken.None);
        await new CopyMoveJob(registry, [folder!], P("/Internal storage/Download"), move: false).RunAsync();
        Assert.Equal("A", Encoding.UTF8.GetString(Phone.Find("/Internal storage/Download/src/a.txt")!.Data));
        Assert.Equal("BB", Encoding.UTF8.GetString(Phone.Find("/Internal storage/Download/src/sub/b.txt")!.Data));

        // move back to the PC: copied, then deleted on the phone
        var onPhone = await _provider.GetEntryAsync(P("/Internal storage/Download/src"), CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(_tmp, "back"));
        await new CopyMoveJob(registry, [onPhone!], VfsPath.Local(Path.Combine(_tmp, "back")), move: true).RunAsync();
        Assert.Equal("BB", File.ReadAllText(Path.Combine(_tmp, "back", "src", "sub", "b.txt")));
        Assert.Null(Phone.Find("/Internal storage/Download/src"));
    }

    private sealed class FakeMtpSource : IMtpDeviceSource
    {
        public Dictionary<string, FakeMtpDevice> Devices { get; } = new() { ["phone-1"] = new FakeMtpDevice() };

        private readonly HashSet<string> _unplugged = [];

        public void Unplug(string id)
        {
            _unplugged.Add(id);
            Devices[id].Connected = false;
        }

        public IReadOnlyList<MtpDeviceInfo> GetDevices() =>
            MtpDeviceNames.MakeUnique(Devices.Keys.Where(id => !_unplugged.Contains(id)).Select(id => new MtpDeviceInfo(id, "Phone")));

        public IMtpDevice Open(string deviceId) => Devices[deviceId];
    }

    internal sealed class FakeMtpDevice : IMtpDevice
    {
        public sealed class Node
        {
            public required string Id { get; init; }

            public required string ParentId { get; set; }

            public required string Name { get; set; }

            public bool IsFolder { get; init; }

            public byte[] Data { get; set; } = [];

            public DateTime? Modified { get; set; }
        }

        private readonly Dictionary<string, Node> _nodes = [];
        private int _next;

        public FakeMtpDevice()
        {
            _nodes["s1"] = new Node { Id = "s1", ParentId = RootId, Name = "Internal storage", IsFolder = true };
        }

        public bool Connected { get; set; } = true;

        public string RootId => "DEVICE";

        private void CheckConnected()
        {
            if (!Connected)
            {
                throw new IOException("Could not list the folder (error 0x8007048F).");
            }
        }

        public IEnumerable<Node> ChildrenOf(string path) => Find(path) is { } folder ? _nodes.Values.Where(n => n.ParentId == folder.Id) : [];

        public Node? Find(string path)
        {
            var current = RootId;
            Node? node = null;
            foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                node = _nodes.Values.FirstOrDefault(n => n.ParentId == current && n.Name == segment);
                if (node is null)
                {
                    return null;
                }
                current = node.Id;
            }
            return node;
        }

        public Node AddFolder(string path)
        {
            var parentPath = path[..path.LastIndexOf('/')];
            var parent = Find(parentPath) ?? AddFolder(parentPath);
            return Find(path) ?? Add(parent.Id, path[(path.LastIndexOf('/') + 1)..], folder: true);
        }

        public void AddFile(string path, string text)
        {
            var parentPath = path[..path.LastIndexOf('/')];
            var parent = Find(parentPath) ?? AddFolder(parentPath);
            var node = Add(parent.Id, path[(path.LastIndexOf('/') + 1)..], folder: false);
            node.Data = Encoding.UTF8.GetBytes(text);
            node.Modified = new DateTime(2024, 5, 6, 7, 8, 9);
        }

        private Node Add(string parentId, string name, bool folder)
        {
            var node = new Node { Id = "o" + Interlocked.Increment(ref _next), ParentId = parentId, Name = name, IsFolder = folder };
            lock (_nodes)
            {
                _nodes[node.Id] = node;
            }
            return node;
        }

        public IReadOnlyList<MtpObject> ListChildren(string parentId)
        {
            CheckConnected();
            lock (_nodes) // a folder watch reads from a timer thread
            {
                return _nodes.Values.Where(n => n.ParentId == parentId).Select(n => new MtpObject(n.Id, n.Name, n.IsFolder, n.Data.Length, n.Modified)).ToList();
            }
        }

        /// <summary>Capacity and free space of the storage "s1" (null: the device does not tell).</summary>
        public (long Total, long Free)? Space { get; set; }

        public (long Total, long Free)? StorageSpace(string storageId)
        {
            CheckConnected();
            return storageId == "s1" ? Space : null;
        }

        public Stream OpenRead(string objectId)
        {
            CheckConnected();
            return new MemoryStream(_nodes[objectId].Data, writable: false);
        }

        public void CreateFile(string parentId, string name, long size, DateTime? modified, Stream data)
        {
            CheckConnected();
            using var buffer = new MemoryStream();
            data.CopyTo(buffer);
            Assert.Equal(size, buffer.Length); // the size given up front matches the data
            var node = Add(parentId, name, folder: false);
            node.Data = buffer.ToArray();
            node.Modified = modified;
        }

        public MtpObject CreateFolder(string parentId, string name)
        {
            CheckConnected();
            var node = Add(parentId, name, folder: true);
            return new MtpObject(node.Id, name, true, 0, null);
        }

        public void Delete(string objectId, bool recursive)
        {
            CheckConnected();
            var children = _nodes.Values.Where(n => n.ParentId == objectId).ToList();
            if (children.Count > 0 && !recursive)
            {
                throw new IOException("Not empty.");
            }
            foreach (var child in children)
            {
                Delete(child.Id, recursive: true);
            }
            _nodes.Remove(objectId);
        }

        public void Rename(string objectId, string newName)
        {
            CheckConnected();
            _nodes[objectId].Name = newName;
        }

        public void Dispose()
        {
        }
    }
}
