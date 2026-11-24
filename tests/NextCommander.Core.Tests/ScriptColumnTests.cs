using NextCommander.Core.Columns;
using NextCommander.Core.Scripting;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

/// <summary>Custom columns written in JavaScript (user's request, 24 November 2026).</summary>
public sealed class ScriptColumnTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private async Task<VfsEntry> EntryAsync(string name) =>
        (await new LocalFileSystemProvider().GetEntryAsync(_dir.VPath(name), CancellationToken.None))!;

    [Fact]
    public async Task A_script_gives_its_columns_and_values()
    {
        var provider = ScriptColumnProvider.Load(Path.Combine(_dir.Root, "Line count.js"), """
            function columns() { return [{ id: "lines", name: "Lines", kind: "number" }, { id: "upper", name: "Name" }]; }
            function supports(file) { return !file.isDir; }
            function value(file, id) {
              if (id === "upper") return file.name.toUpperCase() + " " + file.ext;
              var text = readText(file.path, 1000);
              return text === null ? null : text.split("\n").length;
            }
            """);
        Assert.Equal("js_line_count", provider.Id);
        Assert.Equal(["js_line_count.lines", "js_line_count.upper"], provider.Fields.Select(f => f.Id));
        Assert.Equal(ColumnKind.Number, provider.Fields[0].Kind);
        Assert.Equal("js_line_count", provider.Fields[0].Provider);

        _dir.Write("a.txt", "one\ntwo\nthree");
        var entry = await EntryAsync("a.txt");
        Assert.True(provider.Supports(entry));
        Directory.CreateDirectory(Path.Combine(_dir.Root, "dir"));
        Assert.False(provider.Supports(await EntryAsync("dir")));
        var values = await provider.GetValuesAsync(entry, ["js_line_count.lines", "js_line_count.upper"], null!, CancellationToken.None);
        Assert.Equal(3.0, values["js_line_count.lines"]);
        Assert.Equal("A.TXT txt", values["js_line_count.upper"]);
    }

    [Fact]
    public async Task A_failing_value_is_empty_and_reported_once()
    {
        var provider = ScriptColumnProvider.Load("bad.js", """
            function columns() { return [{ id: "x", name: "X" }]; }
            function value(file, id) { throw new Error("broken " + file.name); }
            """);
        var reports = new List<string>();
        void OnFailed(object? sender, (string Script, string Message) e) => reports.Add(e.Message);
        ScriptColumnProvider.Failed += OnFailed;
        try
        {
            _dir.Write("a.txt", "a");
            var entry = await EntryAsync("a.txt");
            Assert.Null((await provider.GetValuesAsync(entry, ["js_bad.x"], null!, CancellationToken.None))["js_bad.x"]);
            Assert.Null((await provider.GetValuesAsync(entry, ["js_bad.x"], null!, CancellationToken.None))["js_bad.x"]);
            Assert.Single(reports);
        }
        finally
        {
            ScriptColumnProvider.Failed -= OnFailed;
        }
    }

    [Fact]
    public void Scripts_that_do_not_load_are_reported_and_skipped()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir.Root, "columns")).FullName;
        File.WriteAllText(Path.Combine(folder, "good.js"), "function columns() { return [{ id: 'a' }]; } function value() { return 1; }");
        File.WriteAllText(Path.Combine(folder, "syntax.js"), "function columns( {");
        File.WriteAllText(Path.Combine(folder, "nothing.js"), "var x = 1;");
        File.WriteAllText(Path.Combine(folder, "loop.js"), "while (true) {}"); // stopped by the time limit
        var errors = new List<string>();
        var providers = ScriptColumnProvider.LoadAll(folder, (file, _) => errors.Add(Path.GetFileName(file)));
        Assert.Equal(["js_good"], providers.Select(p => p.Id));
        Assert.Equal(["loop.js", "nothing.js", "syntax.js"], errors.Order());
        Assert.Empty(ScriptColumnProvider.LoadAll(Path.Combine(_dir.Root, "none")));
    }
}
