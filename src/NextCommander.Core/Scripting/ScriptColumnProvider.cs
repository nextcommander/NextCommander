using System.Globalization;
using System.Text.Json;
using Jint;
using Jint.Native;
using NextCommander.Core.Columns;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Scripting;

/// <summary>
/// A custom column written in JavaScript (user's request, 24 November 2026): a file of <c>scripts/columns</c> in the
/// settings folder, run in Jint. The script defines <c>columns()</c> (a list of <c>{ id, name, kind }</c>; kind "text",
/// "number", "size" or "date") and <c>value(file, id)</c>; optionally <c>supports(file)</c>. A file is
/// <c>{ name, ext, path, size, modified, isDir, isLocal }</c>; <c>readText(path, maxChars)</c> reads a local file.
/// The engine is used by one thread at a time; a value that fails is empty (and logged once).
/// </summary>
public sealed class ScriptColumnProvider : IColumnProvider
{
    private readonly Engine _engine;
    private readonly Lock _gate = new();
    private readonly bool _hasSupports;
    private bool _failedOnce;

    private ScriptColumnProvider(string id, string displayName, Engine engine, IReadOnlyList<ColumnField> fields, bool hasSupports)
    {
        Id = id;
        DisplayName = displayName;
        _engine = engine;
        Fields = fields;
        _hasSupports = hasSupports;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public IReadOnlyList<ColumnField> Fields { get; }

    /// <summary>A value of the script failed (the program's log gets the message once per script).</summary>
    public static event EventHandler<(string Script, string Message)>? Failed;

    /// <summary>The columns of every script in <paramref name="folder"/>; scripts that do not load are reported through <paramref name="error"/>.</summary>
    public static IReadOnlyList<ScriptColumnProvider> LoadAll(string folder, Action<string, string>? error = null)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }
        var result = new List<ScriptColumnProvider>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.js").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                result.Add(Load(file, File.ReadAllText(file)));
            }
            catch (Exception ex) when (IsScriptError(ex) || ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                error?.Invoke(file, ex.Message);
            }
        }
        return result;
    }

    /// <summary>The column provider of one script (named after its file: "js_lines" for lines.js).</summary>
    /// <exception cref="InvalidDataException">The script has no columns() or value().</exception>
    public static ScriptColumnProvider Load(string file, string source)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var id = "js_" + new string([.. name.Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_')]);
        var engine = NewEngine();
        engine.Execute(source, file);
        if (engine.GetValue("columns") is not Jint.Native.Function.Function || engine.GetValue("value") is not Jint.Native.Function.Function)
        {
            throw new InvalidDataException("A column script defines columns() and value(file, id).");
        }
        var list = JsonDocument.Parse(engine.Evaluate("JSON.stringify(columns())").AsString());
        var fields = new List<ColumnField>();
        foreach (var column in list.RootElement.EnumerateArray())
        {
            var columnId = column.TryGetProperty("id", out var i) ? i.GetString() : null;
            if (string.IsNullOrWhiteSpace(columnId))
            {
                continue;
            }
            var title = column.TryGetProperty("name", out var n) ? n.GetString() ?? columnId : columnId;
            var kind = column.TryGetProperty("kind", out var k) ? k.GetString()?.ToLowerInvariant() switch
            {
                "number" => ColumnKind.Number,
                "size" => ColumnKind.Size,
                "date" => ColumnKind.Date,
                _ => ColumnKind.Text,
            } : ColumnKind.Text;
            fields.Add(new ColumnField($"{id}.{columnId}", title, kind));
        }
        if (fields.Count == 0)
        {
            throw new InvalidDataException("columns() gave no column (each needs an id).");
        }
        return new ScriptColumnProvider(id, name, engine, fields, engine.GetValue("supports") is Jint.Native.Function.Function);
    }

    /// <summary>A Jint engine with limits and <c>readText(path, maxChars)</c> for local files.</summary>
    internal static Engine NewEngine(CancellationToken cancellationToken = default)
    {
        var engine = new Engine(o =>
        {
            o.TimeoutInterval(TimeSpan.FromSeconds(5)).LimitRecursion(256).LimitMemory(256L * 1024 * 1024).Strict(false);
            if (cancellationToken.CanBeCanceled)
            {
                o.CancellationToken(cancellationToken);
            }
        });
        engine.SetValue("readText", new Func<string, int?, string?>(ReadText));
        return engine;
    }

    /// <summary>The text of a local file (UTF-8 or the encoding its BOM names), at most <paramref name="maxChars"/> characters; null when it cannot be read.</summary>
    public static string? ReadText(string path, int? maxChars)
    {
        try
        {
            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            if (maxChars is not > 0)
            {
                return reader.ReadToEnd();
            }
            var buffer = new char[maxChars.Value];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The script's own errors (syntax, runtime, limits).</summary>
    public static bool IsScriptError(Exception ex) =>
        ex is Jint.Runtime.JavaScriptException or Jint.JintException or Acornima.ParseErrorException or Jint.Runtime.StatementsCountOverflowException
            or Jint.Runtime.MemoryLimitExceededException or Jint.Runtime.RecursionDepthOverflowException or TimeoutException or JsonException;

    private JsValue FileOf(VfsEntry entry)
    {
        var file = new JsObject(_engine);
        file.Set("name", entry.Name);
        file.Set("ext", entry.IsDirectory ? string.Empty : Path.GetExtension(entry.Name).TrimStart('.'));
        file.Set("path", entry.Path.IsLocal ? entry.Path.Path : entry.Path.ToDisplayString());
        file.Set("size", entry.Size);
        file.Set("modified", entry.LastWriteTime.ToString("o", CultureInfo.InvariantCulture));
        file.Set("isDir", entry.IsDirectory);
        file.Set("isLocal", entry.Path.IsLocal);
        return file;
    }

    public bool Supports(VfsEntry entry)
    {
        if (!_hasSupports)
        {
            return true;
        }
        lock (_gate)
        {
            try
            {
                return _engine.Invoke("supports", FileOf(entry)).AsBoolean();
            }
            catch (Exception ex) when (IsScriptError(ex) || ex is ArgumentException or InvalidOperationException)
            {
                Report(ex);
                return false;
            }
        }
    }

    public Task<IReadOnlyDictionary<string, object?>> GetValuesAsync(VfsEntry entry, IReadOnlyCollection<string> fieldIds, ProviderRegistry registry,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, object?>();
        lock (_gate)
        {
            var file = FileOf(entry);
            foreach (var fieldId in fieldIds)
            {
                var field = Fields.FirstOrDefault(f => f.Id == fieldId);
                if (field is null)
                {
                    continue;
                }
                try
                {
                    var value = _engine.Invoke("value", file, fieldId[(Id.Length + 1)..]);
                    values[fieldId] = Convert(value, field.Kind);
                }
                catch (Exception ex) when (IsScriptError(ex) || ex is ArgumentException or InvalidOperationException or FormatException)
                {
                    Report(ex);
                    values[fieldId] = null;
                }
            }
        }
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(values);
    }

    private static object? Convert(JsValue value, ColumnKind kind)
    {
        if (value.IsNull() || value.IsUndefined())
        {
            return null;
        }
        return kind switch
        {
            ColumnKind.Number => value.IsNumber() ? value.AsNumber() : double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null,
            ColumnKind.Size => value.IsNumber() ? (long)value.AsNumber() : null,
            ColumnKind.Date => value.IsDate() ? value.AsDate().ToDateTime().ToLocalTime()
                : DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null,
            _ => value.ToString(),
        };
    }

    private void Report(Exception ex)
    {
        if (!_failedOnce)
        {
            _failedOnce = true;
            Failed?.Invoke(this, (DisplayName, ex.Message));
        }
    }
}
