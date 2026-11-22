using System.Globalization;
using System.Text;
using System.Text.Json;
using NextCommander.Core.Vfs;

namespace NextCommander.Plugin.Sqlite;

/// <summary>
/// A SQLite database file opened like a folder (Enter on .sqlite / .sqlite3 / .db3, and on a .db file that is a SQLite
/// database; Ctrl+PgDn on any file that is one): <c>sqlite://&lt;database&gt;/</c> holds a folder per table and view
/// with <c>&lt;table&gt;.csv</c>, <c>&lt;table&gt;.json</c> and <c>schema.sql</c>, and <c>schema.sql</c> of the whole
/// database. Read-only: copying a CSV out exports the table. The database file is the authority with its separators as
/// <c>|</c>; ".." from the root goes back to the folder of the file.
/// </summary>
public sealed class SqliteFileSystemProvider : IFileSystemProvider, IContainerProvider, IOriginProvider
{
    public const string SqliteScheme = "sqlite";

    internal const string SchemaFile = "schema.sql";

    private static readonly string[] s_extensions = [".sqlite", ".sqlite3", ".db3", ".sdb"];

    /// <summary>The first 16 bytes of every SQLite database.</summary>
    private static readonly byte[] s_header = Encoding.ASCII.GetBytes("SQLite format 3\0");

    public string Scheme => SqliteScheme;

    public ProviderCapabilities Capabilities => ProviderCapabilities.Read;

    // ---------------------------------------------------------------- opening a file like a folder

    public bool CanOpen(VfsEntry file, bool force)
    {
        if (file.IsDirectory || !file.Path.IsLocal || !SqliteDatabase.IsAvailable)
        {
            return false;
        }
        var extension = Path.GetExtension(file.Name);
        // a .db file only when it is a SQLite database (Windows' Thumbs.db is not); any file on Ctrl+PgDn
        return s_extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            || ((force || extension.Equals(".db", StringComparison.OrdinalIgnoreCase)) && IsDatabase(file.Path.Path));
    }

    public bool CanOpen(string fileName) => s_extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    public VfsPath GetRoot(VfsPath file) => VfsPath.Create(SqliteScheme, Encode(file.Path), "/");

    /// <summary>Whether a file starts like a SQLite database.</summary>
    public static bool IsDatabase(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> start = stackalloc byte[16];
            return stream.ReadAtLeast(start, 16, throwOnEndOfStream: false) == 16 && start.SequenceEqual(s_header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether a header (the first bytes of a file) is a SQLite database's.</summary>
    public static bool IsDatabase(ReadOnlySpan<byte> header) => header.Length >= 16 && header[..16].SequenceEqual(s_header);

    public (VfsPath Folder, string FocusName)? OriginOf(VfsPath root)
    {
        if (root.Scheme != SqliteScheme || root.Authority.Length == 0)
        {
            return null;
        }
        var file = Decode(root.Authority);
        return Path.GetDirectoryName(file) is { } folder ? (VfsPath.Local(folder), Path.GetFileName(file)) : null;
    }

    internal static string Encode(string file) => file.Replace('\\', '|').Replace('/', '|');

    internal static string Decode(string authority) => authority.Replace('|', OperatingSystem.IsWindows() ? '\\' : '/');

    // ---------------------------------------------------------------- the folders

    /// <summary>The tables and views: name, kind ("table" / "view") and the statement that made them.</summary>
    private static List<(string Name, string Kind, string Sql)> Objects(SqliteDatabase db) =>
        [.. db.Query("SELECT name, type, sql FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY name", out _)
            .Select(r => ((string)r[0]!, (string)r[1]!, r[2] as string ?? string.Empty))];

    private static (string File, string? Table, string? Name) Split(VfsPath path)
    {
        if (path.Authority.Length == 0)
        {
            throw new DirectoryNotFoundException(path.ToString());
        }
        var parts = path.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => (Decode(path.Authority), null, null),
            1 => (Decode(path.Authority), parts[0], null),
            2 => (Decode(path.Authority), parts[0], parts[1]),
            _ => throw new DirectoryNotFoundException(path.ToString()),
        };
    }

    private static SqliteDatabase OpenDatabase(string file) =>
        File.Exists(file) ? SqliteDatabase.Open(file) : throw new FileNotFoundException(file);

    public Task<IReadOnlyList<VfsEntry>> ListAsync(VfsPath directory, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var (file, table, name) = Split(directory);
        if (name is not null)
        {
            throw new DirectoryNotFoundException(directory.ToString());
        }
        var time = File.GetLastWriteTime(file);
        using var db = OpenDatabase(file);
        var objects = Objects(db);
        if (table is null)
        {
            var entries = objects.Select(o => new VfsEntry { Name = FolderName(o.Name), Path = directory.Combine(FolderName(o.Name)), IsDirectory = true, LastWriteTime = time }).ToList();
            entries.Add(FileEntry(directory, SchemaFile, Encoding.UTF8.GetByteCount(Schema(db)), time));
            return (IReadOnlyList<VfsEntry>)entries;
        }
        if (objects.FirstOrDefault(o => FolderName(o.Name) == table) is not { Name.Length: > 0 } found)
        {
            throw new DirectoryNotFoundException(directory.ToString());
        }
        return [FileEntry(directory, table + ".csv", 0, time), FileEntry(directory, table + ".json", 0, time), FileEntry(directory, SchemaFile, Encoding.UTF8.GetByteCount(Schema(db, found.Name)), time)];
    }, cancellationToken);

    /// <summary>A table's folder name: a slash in the name (allowed in SQLite) shown as "⁄" so it stays one folder.</summary>
    internal static string FolderName(string table) => table.Replace('/', '⁄');

    private static VfsEntry FileEntry(VfsPath folder, string name, long size, DateTime time) =>
        new() { Name = name, Path = folder.Combine(name), Size = size, LastWriteTime = time };

    /// <summary>The statements that make the database (or one table with its indexes and triggers): tables, views, indexes, triggers.</summary>
    internal static string Schema(SqliteDatabase db, string? table = null) =>
        string.Concat(db.Query(
            "SELECT sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%'"
            + (table is null ? string.Empty : " AND tbl_name = '" + table.Replace("'", "''", StringComparison.Ordinal) + "'")
            + " ORDER BY CASE type WHEN 'table' THEN 0 WHEN 'view' THEN 1 WHEN 'index' THEN 2 ELSE 3 END, name", out _)
            .Select(r => (string)r[0]! + ";\n\n"));

    public async Task<VfsEntry?> GetEntryAsync(VfsPath path, CancellationToken cancellationToken)
    {
        if (path.Path == "/")
        {
            var file = Decode(path.Authority);
            return File.Exists(file) ? new VfsEntry { Name = Path.GetFileName(file), Path = path, IsDirectory = true, LastWriteTime = File.GetLastWriteTime(file) } : null;
        }
        if (path.Parent is not { } parent)
        {
            return null;
        }
        try
        {
            var siblings = await ListAsync(parent, cancellationToken).ConfigureAwait(false);
            return siblings.FirstOrDefault(e => e.Name == path.Name);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- the files

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var (file, table, name) = Split(path);
        using var db = OpenDatabase(file);
        var objects = Objects(db);
        if (table == SchemaFile && name is null)
        {
            return (Stream)new MemoryStream(Encoding.UTF8.GetBytes(Schema(db)));
        }
        if (table is null || name is null || objects.FirstOrDefault(o => FolderName(o.Name) == table) is not { Name.Length: > 0 } found)
        {
            throw new FileNotFoundException(path.ToString());
        }
        if (name == SchemaFile)
        {
            return new MemoryStream(Encoding.UTF8.GetBytes(Schema(db, found.Name)));
        }
        // a table's rows go through a temporary file (a big table does not sit in memory), removed when read
        var temp = new FileStream(Path.Combine(Path.GetTempPath(), "nc-sqlite-" + Guid.NewGuid().ToString("N") + ".tmp"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
        try
        {
            if (name == table + ".csv")
            {
                WriteCsv(db, found.Name, temp, cancellationToken);
            }
            else if (name == table + ".json")
            {
                WriteJson(db, found.Name, temp, cancellationToken);
            }
            else
            {
                throw new FileNotFoundException(path.ToString());
            }
            temp.Position = 0;
            return temp;
        }
        catch
        {
            temp.Dispose();
            throw;
        }
    }, cancellationToken);

    /// <summary>The rows as CSV (RFC 4180): a header line, commas, quotes where needed, UTF-8 with a BOM (Excel reads it so).</summary>
    internal static void WriteCsv(SqliteDatabase db, string table, Stream output, CancellationToken cancellationToken)
    {
        using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 1 << 16, leaveOpen: true) { NewLine = "\r\n" };
        var rows = db.Query("SELECT * FROM " + SqliteDatabase.Quote(table), out var columns);
        writer.WriteLine(string.Join(',', columns.Select(Csv)));
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteLine(string.Join(',', row.Select(v => Csv(Text(v)))));
        }
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;

    /// <summary>A value as text: numbers in the invariant culture, BLOBs as hex.</summary>
    internal static string Text(object? value) => value switch
    {
        null => string.Empty,
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>The rows as a JSON array of objects (BLOBs as base64).</summary>
    internal static void WriteJson(SqliteDatabase db, string table, Stream output, CancellationToken cancellationToken)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions
        {
            Indented = true,
            // letters of every language as they are ("Ayşe", not "Ayşe"); a file, not a web page
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        var rows = db.Query("SELECT * FROM " + SqliteDatabase.Quote(table), out var columns);
        writer.WriteStartArray();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            for (var i = 0; i < columns.Count; i++)
            {
                writer.WritePropertyName(columns[i]);
                switch (row[i])
                {
                    case null:
                        writer.WriteNullValue();
                        break;
                    case long l:
                        writer.WriteNumberValue(l);
                        break;
                    case double d when double.IsFinite(d):
                        writer.WriteNumberValue(d);
                        break;
                    case byte[] bytes:
                        writer.WriteBase64StringValue(bytes);
                        break;
                    default:
                        writer.WriteStringValue(Text(row[i]));
                        break;
                }
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    // ---------------------------------------------------------------- read-only

    private static NotSupportedException ReadOnly() => new("The database is opened read-only: copy a table out (CSV or JSON) to use its rows.");

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite, CancellationToken cancellationToken) => throw ReadOnly();

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken cancellationToken) => throw ReadOnly();

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken cancellationToken) => throw ReadOnly();

    public Task MoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken cancellationToken) => throw ReadOnly();

    public Task SetTimesAsync(VfsPath path, DateTime? creationTime, DateTime? lastWriteTime, CancellationToken cancellationToken) => Task.CompletedTask;
}
