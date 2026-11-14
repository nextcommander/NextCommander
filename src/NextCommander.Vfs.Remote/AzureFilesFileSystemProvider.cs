using System.Globalization;
using System.Net;
using System.Xml.Linq;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;
using static NextCommander.Vfs.Remote.AzureBlobFileSystemProvider;

namespace NextCommander.Vfs.Remote;

/// <summary>
/// Azure Files (<c>azfile://account@account.file.core.windows.net/share/folder/file</c>) over the File service REST
/// API: shares at the root, real folders below. The secret is the account key (Shared Key) or a SAS token, as for Azure
/// Blob. Files are created at their size and written in ranges of <see cref="RangeSize"/>; moves inside a share are
/// renames on the server; the modification time is the file's own (SMB) last write time.
/// </summary>
public sealed class AzureFilesFileSystemProvider(RemoteHooks hooks) : RemoteProviderBase<HttpRemoteClient>(hooks)
{
    /// <summary>Bytes written by one Put Range (the service takes at most 4 MiB).</summary>
    public int RangeSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>A path is asked for as a file first, then as a folder.</summary>
    private static readonly bool[] s_fileThenFolder = [false, true];

    public override string Scheme => "azfile";

    /// <summary>Tests: the HTTP handler of all requests (a fake service).</summary>
    public Func<HttpMessageHandler>? HandlerFactory { get; init; }

    /// <summary>The account key or a SAS token is asked; an Entra ID account signs in in the browser instead.</summary>
    protected override bool NeedsPassword(RemoteConnection connection) => !connection.AzureEntraId;

    /// <summary>"/share/a/b.txt" → ("share", "a/b.txt").</summary>
    public static (string Share, string Name) Split(string path) => S3FileSystemProvider.Split(path);

    /// <summary>A time as the File service takes it (UTC, seven decimals).</summary>
    public static string FileTime(DateTime time) => time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    protected override Task<HttpRemoteClient> ConnectAsync(RemoteConnection connection, string? password, string? trustedFingerprint, CancellationToken cancellationToken) =>
        ConnectAzureAsync(connection, password, trustedFingerprint, Hooks, HandlerFactory?.Invoke(), files: true, cancellationToken);

    protected override bool IsAlive(HttpRemoteClient client) => true;

    protected override ValueTask CloseAsync(HttpRemoteClient client)
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    protected override bool IsAuthenticationFailure(Exception ex) =>
        ex is HttpStatusException { ErrorCode: "AuthenticationFailed" or "InvalidAuthenticationInfo" } or HttpStatusException { Status: HttpStatusCode.Unauthorized };

    protected override bool IsConnectionFailure(Exception ex) => ex is HttpRequestException or IOException and not FileNotFoundException;

    protected override Exception? Translate(Exception ex, string path) => ex switch
    {
        HttpStatusException { Status: HttpStatusCode.NotFound } => new FileNotFoundException($"Not found: {path}", path, ex),
        HttpStatusException { Status: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized } => new UnauthorizedAccessException($"{path}: {ex.Message}", ex),
        HttpStatusException { ErrorCode: "DirectoryNotEmpty" } => new IOException($"Folder is not empty: {path}", ex),
        HttpStatusException => new IOException($"Azure: {path}: {ex.Message}", ex),
        HttpRequestException => new IOException($"Azure: {ex.Message}", ex),
        IOException io when io is not FileNotFoundException and not UntrustedServerException => new IOException($"Azure: {ex.Message}", ex),
        _ => null,
    };

    // ---------------------------------------------------------------- requests

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() ?? string.Empty : string.Empty;

    /// <summary>The SMB properties a new file or folder gets (a permission inherited from its folder).</summary>
    private static void NewItemHeaders(HttpRequestMessage request, bool folder)
    {
        var now = FileTime(DateTime.UtcNow);
        request.Headers.TryAddWithoutValidation("x-ms-file-permission", "inherit");
        request.Headers.TryAddWithoutValidation("x-ms-file-attributes", folder ? "Directory" : "None");
        request.Headers.TryAddWithoutValidation("x-ms-file-creation-time", now);
        request.Headers.TryAddWithoutValidation("x-ms-file-last-write-time", now);
    }

    /// <summary>One folder's files and folders (every page), with their times.</summary>
    private static async Task<List<XElement>> EntriesAsync(HttpRemoteClient client, string share, string folder, CancellationToken ct)
    {
        var result = new List<XElement>();
        string? marker = null;
        do
        {
            using var response = await SendAsync(client, HttpMethod.Get, share, folder,
                Query(("restype", "directory"), ("comp", "list"), ("include", "Timestamps"), ("marker", marker)), ct).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "/" + share + "/" + folder).ConfigureAwait(false);
            var root = XDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Root!;
            result.AddRange(Child(root, "Entries")?.Elements() ?? []);
            marker = Child(root, "NextMarker")?.Value is { Length: > 0 } next ? next : null;
        }
        while (marker is not null);
        return result;
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : folder.TrimEnd('/') + "/" + name;

    // ---------------------------------------------------------------- file system

    public override Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) =>
        UseAsync<IReadOnlyList<VfsEntry>>(directory, async client =>
        {
            var (share, name) = Split(directory.Path);
            var result = new List<VfsEntry>();
            if (share.Length == 0)
            {
                string? marker = null;
                do
                {
                    using var response = await SendAsync(client, HttpMethod.Get, null, string.Empty, Query(("comp", "list"), ("marker", marker)), cancellationToken).ConfigureAwait(false);
                    await EnsureSuccessAsync(response, "/").ConfigureAwait(false);
                    var root = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Root!;
                    foreach (var s in Children(Child(root, "Shares"), "Share"))
                    {
                        if (Child(s, "Name")?.Value is { Length: > 0 } n && Child(s, "Snapshot") is null)
                        {
                            result.Add(ToEntry(directory, "/" + n, n, isDirectory: true, isLink: false, 0, TimeOf(Child(Child(s, "Properties"), "Last-Modified")?.Value)));
                        }
                    }
                    marker = Child(root, "NextMarker")?.Value is { Length: > 0 } next ? next : null;
                }
                while (marker is not null);
                return result;
            }
            foreach (var entry in await EntriesAsync(client, share, name, cancellationToken).ConfigureAwait(false))
            {
                if (Child(entry, "Name")?.Value is not { Length: > 0 } n)
                {
                    continue;
                }
                var properties = Child(entry, "Properties");
                var folder = entry.Name.LocalName == "Directory";
                long.TryParse(Child(properties, "Content-Length")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
                var modified = TimeOf(Child(properties, "LastWriteTime")?.Value ?? Child(properties, "Last-Modified")?.Value);
                result.Add(ToEntry(directory, "/" + share + "/" + Join(name, n), n, folder, isLink: false, folder ? 0 : size, modified));
            }
            return result;
        }, cancellationToken);

    public override async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var (share, name) = Split(path.Path);
        if (share.Length == 0)
        {
            return new VfsEntry { Name = path.Authority, Path = path, IsDirectory = true, LastWriteTime = DateTime.Now };
        }
        try
        {
            return await UseAsync(path, async client =>
            {
                if (name.Length == 0)
                {
                    using var head = await SendAsync(client, HttpMethod.Head, share, string.Empty, "restype=share", cancellationToken).ConfigureAwait(false);
                    return head.IsSuccessStatusCode || head.StatusCode == HttpStatusCode.Forbidden
                        ? ToEntry(path, path.Path, share, isDirectory: true, isLink: false, 0, head.Content.Headers.LastModified?.LocalDateTime ?? DateTime.MinValue)
                        : null;
                }
                foreach (var folder in s_fileThenFolder)
                {
                    using var head = await SendAsync(client, HttpMethod.Head, share, name, folder ? "restype=directory" : null, cancellationToken).ConfigureAwait(false);
                    if (head.IsSuccessStatusCode)
                    {
                        var modified = TimeOf(Header(head, "x-ms-file-last-write-time")) is var t && t != DateTime.MinValue
                            ? t : head.Content.Headers.LastModified?.LocalDateTime ?? DateTime.MinValue;
                        return ToEntry(path, path.Path, path.Name, folder, isLink: false, folder ? 0 : head.Content.Headers.ContentLength ?? 0, modified);
                    }
                    if (head.StatusCode != HttpStatusCode.NotFound)
                    {
                        await EnsureSuccessAsync(head, path.Path).ConfigureAwait(false);
                    }
                }
                return null;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public override Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) =>
        OpenStreamAsync(path, async client =>
        {
            var (share, name) = Split(path.Path);
            var response = await SendAsync(client, HttpMethod.Get, share, name, null, cancellationToken, headersOnly: true).ConfigureAwait(false);
            try
            {
                await EnsureSuccessAsync(response, path.Path).ConfigureAwait(false);
                return (Stream)new ResponseStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }, afterClose: null, cancellationToken);

    public override async Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken)
    {
        var (share, name) = Split(path.Path);
        if (share.Length == 0 || name.Length == 0)
        {
            throw new IOException("Azure: files are written into a share.");
        }
        var existing = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false);
        if (existing is not null && (!overwrite || existing.IsDirectory))
        {
            throw new IOException($"Already exists: {path.Name}");
        }
        return new UploadOnCloseStream((data, ct) => UseAsync(path, client => UploadAsync(client, share, name, data, ct), ct), cancellationToken);
    }

    /// <summary>Create File at the full size (an existing file is replaced), then Put Range for each part.</summary>
    private async Task UploadAsync(HttpRemoteClient client, string share, string name, Stream data, CancellationToken ct)
    {
        var where = "/" + share + "/" + name;
        using (var create = await SendAsync(client, HttpMethod.Put, share, name, null, ct, configure: r =>
        {
            r.Headers.TryAddWithoutValidation("x-ms-type", "file");
            r.Headers.TryAddWithoutValidation("x-ms-content-length", data.Length.ToString(CultureInfo.InvariantCulture));
            NewItemHeaders(r, folder: false);
        }).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(create, where).ConfigureAwait(false);
        }
        var buffer = new byte[Math.Min(RangeSize, Math.Max(1, data.Length))];
        data.Position = 0;
        for (long offset = 0; offset < data.Length;)
        {
            var read = 0;
            while (read < buffer.Length && await data.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false) is var n and > 0)
            {
                read += n;
            }
            if (read == 0)
            {
                break;
            }
            var (from, length) = (offset, read);
            using var range = await SendAsync(client, HttpMethod.Put, share, name, "comp=range", ct, () => new ByteArrayContent(buffer, 0, length), r =>
            {
                r.Headers.TryAddWithoutValidation("x-ms-range", $"bytes={from}-{from + length - 1}");
                r.Headers.TryAddWithoutValidation("x-ms-write", "update");
            }).ConfigureAwait(false);
            await EnsureSuccessAsync(range, where).ConfigureAwait(false);
            offset += read;
        }
    }

    public override async Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        var (share, name) = Split(path.Path);
        if (share.Length == 0)
        {
            return;
        }
        await UseAsync(path, async client =>
        {
            using var response = name.Length == 0
                ? await SendAsync(client, HttpMethod.Put, share, string.Empty, "restype=share", cancellationToken).ConfigureAwait(false)
                : await SendAsync(client, HttpMethod.Put, share, name, "restype=directory", cancellationToken, configure: r => NewItemHeaders(r, folder: true)).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Conflict) // ShareAlreadyExists / ResourceAlreadyExists
            {
                await EnsureSuccessAsync(response, path.Path).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public override async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}");
        var (share, name) = Split(path.Path);
        await UseAsync(path, async client =>
        {
            if (!entry.IsDirectory)
            {
                using var response = await SendAsync(client, HttpMethod.Delete, share, name, null, cancellationToken).ConfigureAwait(false);
                await EnsureSuccessAsync(response, path.Path).ConfigureAwait(false);
                return;
            }
            if (!recursive && (await EntriesAsync(client, share, name, cancellationToken).ConfigureAwait(false)).Count > 0)
            {
                throw new IOException($"Folder is not empty: {path.Name}");
            }
            if (name.Length == 0)
            {
                // the share goes with everything in it
                using var response = await SendAsync(client, HttpMethod.Delete, share, string.Empty, "restype=share", cancellationToken).ConfigureAwait(false);
                await EnsureSuccessAsync(response, path.Path).ConfigureAwait(false);
                return;
            }
            await DeleteFolderAsync(client, share, name, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A folder is deleted when empty: its files and folders first.</summary>
    private static async Task DeleteFolderAsync(HttpRemoteClient client, string share, string folder, CancellationToken ct)
    {
        foreach (var entry in await EntriesAsync(client, share, folder, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (Child(entry, "Name")?.Value is not { Length: > 0 } n)
            {
                continue;
            }
            var child = Join(folder, n);
            if (entry.Name.LocalName == "Directory")
            {
                await DeleteFolderAsync(client, share, child, ct).ConfigureAwait(false);
                continue;
            }
            using var response = await SendAsync(client, HttpMethod.Delete, share, child, null, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                await EnsureSuccessAsync(response, "/" + share + "/" + child).ConfigureAwait(false);
            }
        }
        using var self = await SendAsync(client, HttpMethod.Delete, share, folder, "restype=directory", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(self, "/" + share + "/" + folder).ConfigureAwait(false);
    }

    /// <summary>A rename on the server inside a share (a file or a whole folder); between shares the copy does it.</summary>
    public override async Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken)
    {
        if (!CanHandle(destination) || source.Authority != destination.Authority)
        {
            throw new CrossDeviceMoveException("Moving between different servers is done by copying.");
        }
        var (fromShare, fromName) = Split(source.Path);
        var (toShare, toName) = Split(destination.Path);
        if (fromName.Length == 0 || toName.Length == 0)
        {
            throw new IOException("Azure: shares cannot be renamed.");
        }
        if (fromShare != toShare)
        {
            throw new CrossDeviceMoveException("Moving between shares is done by copying.");
        }
        var entry = await GetEntryAsync(source, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {source.Path}");
        if (!string.Equals(source.Path, destination.Path, StringComparison.Ordinal) && await GetEntryAsync(destination, cancellationToken).ConfigureAwait(false) is { } existing
            && (!overwrite || existing.IsDirectory || entry.IsDirectory))
        {
            throw new IOException($"Already exists: {destination.Name}");
        }
        await UseAsync(source, async client =>
        {
            var from = UriOf(client, fromShare, fromName, null).AbsoluteUri;
            using var response = await SendAsync(client, HttpMethod.Put, toShare, toName, entry.IsDirectory ? "restype=directory&comp=rename" : "comp=rename", cancellationToken,
                configure: r =>
                {
                    r.Headers.TryAddWithoutValidation("x-ms-file-rename-source", from);
                    r.Headers.TryAddWithoutValidation("x-ms-file-rename-replace-if-exists", overwrite ? "true" : "false");
                }).ConfigureAwait(false);
            await EnsureSuccessAsync(response, source.Path).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Set File / Directory Properties with the new times; everything else is kept ("preserve").</summary>
    public override async Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken)
    {
        var (share, name) = Split(path.Path);
        if ((lastWriteTime ?? creationTime) is null || share.Length == 0 || name.Length == 0)
        {
            return;
        }
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException($"Not found: {path.Path}");
        static string TimeText(DateTime? time) => time is { Year: >= 1980 } t ? FileTime(t) : "preserve";
        await UseAsync(path, async client =>
        {
            using var response = await SendAsync(client, HttpMethod.Put, share, name, entry.IsDirectory ? "restype=directory&comp=properties" : "comp=properties", cancellationToken,
                configure: r =>
                {
                    r.Headers.TryAddWithoutValidation("x-ms-file-last-write-time", TimeText(lastWriteTime));
                    r.Headers.TryAddWithoutValidation("x-ms-file-creation-time", TimeText(creationTime));
                    r.Headers.TryAddWithoutValidation("x-ms-file-attributes", "preserve");
                    r.Headers.TryAddWithoutValidation("x-ms-file-permission", "preserve");
                }).ConfigureAwait(false);
            await EnsureSuccessAsync(response, path.Path).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }
}
