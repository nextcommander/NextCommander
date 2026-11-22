using System.Text;
using System.Text.Json;
using NextCommander.Core.Vfs;
using NextCommander.Plugin.Sqlite;

namespace NextCommander.Core.Tests;

/// <summary>The SQLite plugin against a real database made through the system's SQLite (skipped when there is none).</summary>
public sealed class SqlitePluginTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nc-sqlite-" + Guid.NewGuid().ToString("N")[..8]);

    public SqlitePluginTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <summary>people (3 rows: a quote, a comma, a line break, a NULL, a real, a BLOB), the view adults, a table "a/b", an index.</summary>
    private string MakeDatabase(string name = "shop.sqlite")
    {
        var path = Path.Combine(_folder, name);
        using var db = SqliteDatabase.OpenForWriting(path);
        db.Execute("""
            CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT NOT NULL, age INTEGER DEFAULT 0, score REAL, photo BLOB);
            INSERT INTO people VALUES (1, 'Ayşe "the first"', 34, 1.5, x'CAFE');
            INSERT INTO people VALUES (2, 'Smith, John', NULL, NULL, NULL);
            INSERT INTO people VALUES (3, 'two' || char(10) || 'lines', 12, 2.25, NULL);
            CREATE VIEW adults AS SELECT name FROM people WHERE age >= 18;
            CREATE TABLE "a/b" (x);
            CREATE INDEX people_name ON people(name);
            """);
        return path;
    }

    private static async Task<string> ReadAsync(IFileSystemProvider provider, VfsPath path)
    {
        await using var stream = await provider.OpenReadAsync(path, CancellationToken.None);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task A_database_opens_like_a_folder_of_its_tables()
    {
        if (!SqliteDatabase.IsAvailable)
        {
            return;
        }
        var path = MakeDatabase();
        var sqlite = new SqliteFileSystemProvider();
        var file = new VfsEntry { Name = "shop.sqlite", Path = VfsPath.Local(path) };
        Assert.True(sqlite.CanOpen(file, force: false));

        // the root: a folder per table and view, and the schema; ".." goes back to the file
        var root = sqlite.GetRoot(file.Path);
        Assert.Equal(VfsPath.Local(_folder), sqlite.OriginOf(root)!.Value.Folder);
        Assert.Equal("shop.sqlite", sqlite.OriginOf(root)!.Value.FocusName);
        var entries = await sqlite.ListAsync(root, CancellationToken.None);
        Assert.Equal(["a⁄b", "adults", "people", "schema.sql"], entries.Select(e => e.Name));
        Assert.Contains("CREATE TABLE people", await ReadAsync(sqlite, root.Combine("schema.sql")), StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX people_name", await ReadAsync(sqlite, root.Combine("schema.sql")), StringComparison.Ordinal);

        // a table: CSV, JSON and its own schema
        var people = root.Combine("people");
        Assert.Equal(["people.csv", "people.json", "schema.sql"], (await sqlite.ListAsync(people, CancellationToken.None)).Select(e => e.Name));
        var tableSchema = await ReadAsync(sqlite, people.Combine("schema.sql"));
        Assert.StartsWith("CREATE TABLE people", tableSchema, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX people_name", tableSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE VIEW", tableSchema, StringComparison.Ordinal);
        var csv = await ReadAsync(sqlite, people.Combine("people.csv"));
        Assert.Equal(
            "id,name,age,score,photo\r\n1,\"Ayşe \"\"the first\"\"\",34,1.5,0xCAFE\r\n2,\"Smith, John\",,,\r\n3,\"two\nlines\",12,2.25,\r\n",
            csv);

        using var json = JsonDocument.Parse(await ReadAsync(sqlite, people.Combine("people.json")));
        var first = json.RootElement[0];
        Assert.Equal(3, json.RootElement.GetArrayLength());
        Assert.Equal("Ayşe \"the first\"", first.GetProperty("name").GetString());
        Assert.Equal(34, first.GetProperty("age").GetInt64());
        Assert.Equal(1.5, first.GetProperty("score").GetDouble());
        Assert.Equal([0xCA, 0xFE], first.GetProperty("photo").GetBytesFromBase64());
        Assert.Equal(JsonValueKind.Null, json.RootElement[1].GetProperty("age").ValueKind);

        // a view gives its rows too; a slash in a table's name stays one folder
        Assert.Contains("\"name\": \"Ayşe", await ReadAsync(sqlite, root.Combine("adults").Combine("adults.json")), StringComparison.Ordinal);
        Assert.Equal("x\r\n", await ReadAsync(sqlite, root.Combine("a⁄b").Combine("a⁄b.csv")));
        Assert.NotNull(await sqlite.GetEntryAsync(people.Combine("people.csv"), CancellationToken.None));
        Assert.Null(await sqlite.GetEntryAsync(root.Combine("missing"), CancellationToken.None));

        // read-only
        await Assert.ThrowsAsync<NotSupportedException>(() => sqlite.DeleteAsync(people.Combine("people.csv"), false, CancellationToken.None));
    }

    [Fact]
    public void A_db_file_opens_only_when_it_is_a_database()
    {
        if (!SqliteDatabase.IsAvailable)
        {
            return;
        }
        var sqlite = new SqliteFileSystemProvider();
        var database = MakeDatabase("data.db");
        var thumbs = Path.Combine(_folder, "Thumbs.db");
        File.WriteAllBytes(thumbs, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        var renamed = Path.Combine(_folder, "data.bin");
        File.Copy(database, renamed);

        Assert.True(sqlite.CanOpen(new VfsEntry { Name = "data.db", Path = VfsPath.Local(database) }, force: false));
        Assert.False(sqlite.CanOpen(new VfsEntry { Name = "Thumbs.db", Path = VfsPath.Local(thumbs) }, force: false));
        // any name on Ctrl+PgDn when it is a database
        Assert.False(sqlite.CanOpen(new VfsEntry { Name = "data.bin", Path = VfsPath.Local(renamed) }, force: false));
        Assert.True(sqlite.CanOpen(new VfsEntry { Name = "data.bin", Path = VfsPath.Local(renamed) }, force: true));
        Assert.False(sqlite.CanOpen(new VfsEntry { Name = "Thumbs.db", Path = VfsPath.Local(thumbs) }, force: true));
    }

    [Fact]
    public async Task The_lister_shows_tables_columns_and_first_rows()
    {
        if (!SqliteDatabase.IsAvailable)
        {
            return;
        }
        var path = MakeDatabase();
        var viewer = new SqliteViewer();
        var header = File.ReadAllBytes(path).AsSpan(0, 100).ToArray();
        Assert.True(viewer.CanView("shop.sqlite", header));
        Assert.False(viewer.CanView("notes.txt", "hello"u8));

        // a local file is read in place, any other stream through a temporary copy
        foreach (var local in new[] { true, false })
        {
            await using Stream stream = local ? File.OpenRead(path) : new MemoryStream(File.ReadAllBytes(path));
            var content = await viewer.ViewAsync(stream, "shop.sqlite", dark: false, CancellationToken.None);
            var html = content.Html!;
            Assert.Contains("<h1>shop.sqlite</h1>", html, StringComparison.Ordinal);
            Assert.Contains("<h2>people</h2>", html, StringComparison.Ordinal);
            Assert.Contains("<h2>adults</h2>", html, StringComparison.Ordinal);
            Assert.Contains("Rows: 3", html, StringComparison.Ordinal);
            Assert.Contains("Primary key", html, StringComparison.Ordinal);
            Assert.Contains("Smith, John", html, StringComparison.Ordinal);
            Assert.Contains("BLOB, 2 bytes", html, StringComparison.Ordinal);
            Assert.Contains("people_name", html, StringComparison.Ordinal);
            Assert.Contains("&quot;the first&quot;", html, StringComparison.Ordinal);
        }
    }
}
