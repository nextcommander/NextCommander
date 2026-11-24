using System.Globalization;
using System.Text;
using System.Text.Json;
using Jint;
using NextCommander.Core.Plugins;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Scripting;

/// <summary>
/// A file system written in JavaScript (user's request, 24 November 2026): a file of <c>scripts/filesystems</c> in the
/// settings folder, opened from the saved connections' menu like a plugin's place (<c>js-&lt;name&gt;:///</c>).
/// <para>The script defines <c>list(path)</c> (entries <c>{ name, isDir, size, modified }</c>) and <c>read(path)</c> (a text, or
/// <c>{ base64 }</c>); writing needs <c>write(path, data)</c> (<c>data.text</c>, <c>data.base64</c>), and can have
/// <c>mkdir(path)</c>, <c>remove(path, isDir)</c>, <c>rename(from, to)</c>. <c>var title = "..."</c> names the entry. Paths
/// are "/" separated from "/". The program gives <c>http(method, url, body, headers)</c> → <c>{ status, body, headers }</c>,
/// <c>toBase64(text)</c>, <c>fromBase64(base64)</c> and <c>readText(path, maxChars)</c> for local files.</para>
/// One engine per script, used by one call at a time, on a thread of its own; an error of the script is an
/// <see cref="IOException"/> with its message.
/// </summary>
public sealed class ScriptFileSystemProvider : IFileSystemProvider, IPluginEntryPoint
{
    private const string Prelude = """
        function __list(p) { return JSON.stringify(list(p)); }
        function __read(p) { var r = read(p); return JSON.stringify(typeof r === "string" ? { text: r } : r); }
        function __http(method, url, body, headers) { return JSON.parse(__httpRaw(method, url, body === undefined ? null : body, JSON.stringify(headers || {}))); }
        var http = __http;
        """;

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly Engine _engine;
    private readonly Lock _gate = new();
    private readonly bool _canWrite;
    private readonly bool _canRename;
    private readonly bool _canMkdir;
    private readonly bool _canRemove;

    private ScriptFileSystemProvider(string scheme, string title, Engine engine)
    {
        Scheme = scheme;
        EntryName = title;
        _engine = engine;
        bool Has(string name) => engine.GetValue(name) is Jint.Native.Function.Function;
        _canWrite = Has("write");
        _canRename = Has("rename");
        _canMkdir = Has("mkdir");
        _canRemove = Has("remove");
    }

    public string Scheme { get; }

    public string EntryName { get; }

    public string EntryGroup => "JavaScript";

    public VfsPath EntryRoot => VfsPath.Create(Scheme, string.Empty, "/");

    public string? Unavailable => null;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Read | (_canWrite ? ProviderCapabilities.Write : 0) | (_canRename ? ProviderCapabilities.Rename : 0);

    /// <summary>The file systems of every script in <paramref name="folder"/>; scripts that do not load go to <paramref name="error"/>.</summary>
    public static IReadOnlyList<ScriptFileSystemProvider> LoadAll(string folder, Action<string, string>? error = null)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }
        var result = new List<ScriptFileSystemProvider>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.js").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                result.Add(Load(file, File.ReadAllText(file)));
            }
            catch (Exception ex) when (ScriptColumnProvider.IsScriptError(ex) || ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                error?.Invoke(file, ex.Message);
            }
        }
        return result;
    }

    /// <summary>The file system of one script (scheme "js-notes" for notes.js).</summary>
    /// <exception cref="InvalidDataException">The script has no list() or read().</exception>
    public static ScriptFileSystemProvider Load(string file, string source)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var scheme = "js-" + new string([.. name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
        var engine = new Engine(o => o.TimeoutInterval(TimeSpan.FromSeconds(60)).LimitRecursion(256).LimitMemory(512L * 1024 * 1024).Strict(false));
        engine.SetValue("readText", new Func<string, int?, string?>(ScriptColumnProvider.ReadText));
        engine.SetValue("toBase64", new Func<string, string>(text => Convert.ToBase64String(Encoding.UTF8.GetBytes(text))));
        engine.SetValue("fromBase64", new Func<string, string>(base64 => Encoding.UTF8.GetString(Convert.FromBase64String(base64))));
        engine.SetValue("__httpRaw", new Func<string, string, string?, string, string>(Http));
        engine.Execute(Prelude);
        engine.Execute(source, file);
        if (engine.GetValue("list") is not Jint.Native.Function.Function || engine.GetValue("read") is not Jint.Native.Function.Function)
        {
            throw new InvalidDataException("A file system script defines list(path) and read(path).");
        }
        var title = engine.GetValue("title") is { } t && t.IsString() ? t.AsString() : name;
        return new ScriptFileSystemProvider(scheme, title, engine);
    }

    /// <summary>An HTTP request for the script (blocking: the script runs on a thread of its own).</summary>
    private static string Http(string method, string url, string? body, string headersJson)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), url);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
        }
        foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson) ?? [])
        {
            if (!request.Headers.TryAddWithoutValidation(key, value) && request.Content is not null)
            {
                request.Content.Headers.Remove(key);
                request.Content.Headers.TryAddWithoutValidation(key, value);
            }
        }
        using var response = s_http.Send(request);
        using var reader = new StreamReader(response.Content.ReadAsStream());
        var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(", ", h.Value));
        return JsonSerializer.Serialize(new { status = (int)response.StatusCode, body = reader.ReadToEnd(), headers });
    }

    /// <summary>Calls the script on a thread of its own, one call at a time; its errors become IOExceptions.</summary>
    private Task<T> CallAsync<T>(Func<Engine, T> call, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            lock (_gate)
            {
                try
                {
                    return call(_engine);
                }
                catch (Exception ex) when (ScriptColumnProvider.IsScriptError(ex) || ex is HttpRequestException or TaskCanceledException or FormatException)
                {
                    throw new IOException($"{EntryName}: {(ex is Jint.Runtime.JavaScriptException js ? js.Error.ToString() : ex.Message)}", ex);
                }
            }
        }, cancellationToken);

    private static string PathOf(VfsPath path) => string.IsNullOrEmpty(path.Path) ? "/" : path.Path;

    public Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) =>
        CallAsync<IReadOnlyList<VfsEntry>>(engine =>
        {
            using var json = JsonDocument.Parse(engine.Invoke("__list", PathOf(directory)).AsString());
            var entries = new List<VfsEntry>();
            foreach (var item in json.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name && name is not ("." or ".."))
                {
                    entries.Add(EntryOf(directory.Combine(name), name, item));
                }
            }
            return entries;
        }, cancellationToken);

    private static VfsEntry EntryOf(VfsPath path, string name, JsonElement item)
    {
        var isDir = item.TryGetProperty("isDir", out var d) && d.ValueKind == JsonValueKind.True;
        var size = item.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? (long)s.GetDouble() : 0;
        var modified = DateTime.MinValue;
        if (item.TryGetProperty("modified", out var m))
        {
            if (m.ValueKind == JsonValueKind.Number)
            {
                modified = DateTimeOffset.FromUnixTimeMilliseconds((long)m.GetDouble()).LocalDateTime;
            }
            else if (m.ValueKind == JsonValueKind.String && DateTime.TryParse(m.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                modified = parsed.Kind == DateTimeKind.Utc ? parsed.ToLocalTime() : parsed;
            }
        }
        return new VfsEntry { Name = name, Path = path, IsDirectory = isDir, Size = isDir ? 0 : size, LastWriteTime = modified };
    }

    public async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        if (path.Parent is not { } parent || PathOf(path) == "/")
        {
            return new VfsEntry { Name = EntryName, Path = path, IsDirectory = true, LastWriteTime = DateTime.Now };
        }
        try
        {
            return (await ListAsync(parent, cancellationToken).ConfigureAwait(false)).FirstOrDefault(e => e.Name == path.Name);
        }
        catch (IOException)
        {
            return null; // a folder the script does not know
        }
    }

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) =>
        CallAsync<Stream>(engine =>
        {
            using var json = JsonDocument.Parse(engine.Invoke("__read", PathOf(path)).AsString());
            var root = json.RootElement;
            var bytes = root.TryGetProperty("base64", out var b) && b.GetString() is { } base64 ? Convert.FromBase64String(base64)
                : root.TryGetProperty("text", out var t) ? Encoding.UTF8.GetBytes(t.GetString() ?? string.Empty)
                : [];
            return new MemoryStream(bytes, writable: false);
        }, cancellationToken);

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken)
    {
        if (!_canWrite)
        {
            throw new NotSupportedException($"{EntryName} is read-only: its script has no write(path, data).");
        }
        return Task.FromResult<Stream>(new WriteBack(this, path));
    }

    /// <summary>What is written is kept, and handed to the script's write() when the stream is closed.</summary>
    private sealed class WriteBack(ScriptFileSystemProvider owner, VfsPath path) : MemoryStream
    {
        private bool _sent;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_sent)
            {
                _sent = true;
                var bytes = ToArray();
                owner.CallAsync(engine =>
                {
                    var data = new Jint.Native.JsObject(engine);
                    data.Set("base64", Convert.ToBase64String(bytes));
                    data.Set("text", Encoding.UTF8.GetString(bytes));
                    engine.Invoke("write", PathOf(path), data);
                    return true;
                }, CancellationToken.None).GetAwaiter().GetResult();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_sent)
            {
                _sent = true;
                var bytes = ToArray();
                await owner.CallAsync(engine =>
                {
                    var data = new Jint.Native.JsObject(engine);
                    data.Set("base64", Convert.ToBase64String(bytes));
                    data.Set("text", Encoding.UTF8.GetString(bytes));
                    engine.Invoke("write", PathOf(path), data);
                    return true;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) =>
        _canMkdir
            ? CallAsync(engine => engine.Invoke("mkdir", PathOf(path)), cancellationToken)
            : throw new NotSupportedException($"{EntryName}: its script makes no folders (no mkdir(path)).");

    public async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken)
    {
        if (!_canRemove)
        {
            throw new NotSupportedException($"{EntryName}: its script deletes nothing (no remove(path, isDir)).");
        }
        var entry = await GetEntryAsync(path, cancellationToken).ConfigureAwait(false);
        await CallAsync(engine => engine.Invoke("remove", PathOf(path), entry?.IsDirectory == true), cancellationToken).ConfigureAwait(false);
    }

    public Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken) =>
        _canRename
            ? CallAsync(engine => engine.Invoke("rename", PathOf(source), PathOf(destination)), cancellationToken)
            : throw new CrossDeviceMoveException("Copy and delete"); // the job copies and deletes instead

    public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) => Task.CompletedTask;
}
