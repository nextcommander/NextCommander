using System.Runtime.InteropServices;
using System.Text;

namespace NextCommander.Plugin.Sqlite;

/// <summary>
/// A SQLite database file through the system's SQLite library (Windows' winsqlite3.dll, Linux' libsqlite3.so.0,
/// macOS' libsqlite3.dylib), called through function pointers: only what the plugin needs (open, prepare, step, read
/// the columns, exec). Opened read-only for the plugin; one connection per use.
/// </summary>
public sealed unsafe class SqliteDatabase : IDisposable
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int OpenReadOnly = 0x1;
    private const int OpenReadWrite = 0x2;
    private const int OpenCreate = 0x4;

    private nint _db;

    private SqliteDatabase(nint db) => _db = db;

    /// <summary>The system's SQLite (null when there is none); found once.</summary>
    private static readonly Lazy<Native?> s_native = new(Native.Load);

    /// <summary>The library the plugin's settings name (read when SQLite is first used: a change takes effect after a restart).</summary>
    public static string? ConfiguredLibrary { get; set; }

    /// <summary>Whether the system has a SQLite library the plugin can use.</summary>
    public static bool IsAvailable => s_native.Value is not null;

    private static Native Api => s_native.Value ?? throw new IOException("SQLite is not available on this system (no winsqlite3.dll / libsqlite3).");

    /// <summary>The library's version ("3.45.1").</summary>
    public static string Version => Text(Api.LibVersion());

    /// <summary>Opens a database file read-only (the plugin never changes it).</summary>
    public static SqliteDatabase Open(string path) => OpenWith(path, OpenReadOnly);

    /// <summary>Opens or makes a database file to write (tests).</summary>
    internal static SqliteDatabase OpenForWriting(string path) => OpenWith(path, OpenReadWrite | OpenCreate);

    private static SqliteDatabase OpenWith(string path, int flags)
    {
        var api = Api;
        nint db;
        int result;
        fixed (byte* name = Utf8(path))
        {
            result = api.Open(name, &db, flags, null);
        }
        if (result != SqliteOk)
        {
            var message = db != 0 ? Text(api.ErrorMessage(db)) : "error " + result;
            if (db != 0)
            {
                api.Close(db);
            }
            throw new IOException($"{Path.GetFileName(path)}: {message}");
        }
        return new SqliteDatabase(db);
    }

    /// <summary>Runs statements that give no rows (tests make databases with it).</summary>
    public void Execute(string sql)
    {
        byte* error = null;
        fixed (byte* text = Utf8(sql))
        {
            if (Api.Exec(_db, text, 0, 0, &error) != SqliteOk)
            {
                throw new IOException(error is not null ? Text(error) : Text(Api.ErrorMessage(_db)));
            }
        }
    }

    /// <summary>The rows of a query, one after the other, with the column names first.</summary>
    public IEnumerable<object?[]> Query(string sql, out IReadOnlyList<string> columns)
    {
        var api = Api;
        nint statement;
        fixed (byte* text = Utf8(sql))
        {
            if (api.Prepare(_db, text, -1, &statement, null) != SqliteOk)
            {
                throw new IOException(Text(api.ErrorMessage(_db)));
            }
        }
        var count = api.ColumnCount(statement);
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            names[i] = Text(api.ColumnName(statement, i));
        }
        columns = names;
        return Rows(statement, count);
    }

    private IEnumerable<object?[]> Rows(nint statement, int count)
    {
        try
        {
            while (Next(statement))
            {
                var row = new object?[count];
                for (var i = 0; i < count; i++)
                {
                    row[i] = Value(statement, i);
                }
                yield return row;
            }
        }
        finally
        {
            Finish(statement);
        }
    }

    // (an iterator cannot call through pointers itself)

    /// <summary>Steps to the next row: false at the end.</summary>
    private bool Next(nint statement) => Api.Step(statement) switch
    {
        SqliteRow => true,
        SqliteDone => false,
        _ => throw new IOException(Text(Api.ErrorMessage(_db))),
    };

    private static void Finish(nint statement) => Api.Finalize(statement);

    /// <summary>A column of the current row: long, double, string, byte[] or null.</summary>
    private static object? Value(nint statement, int column)
    {
        var api = Api;
        switch (api.ColumnType(statement, column))
        {
            case 1:
                return api.ColumnInt64(statement, column);
            case 2:
                return api.ColumnDouble(statement, column);
            case 3:
                return Text(api.ColumnText(statement, column), api.ColumnBytes(statement, column));
            case 4:
                var length = api.ColumnBytes(statement, column);
                var blob = api.ColumnBlob(statement, column);
                return length == 0 || blob is null ? [] : new ReadOnlySpan<byte>(blob, length).ToArray();
            default:
                return null;
        }
    }

    /// <summary>One value (the first column of the first row), or null.</summary>
    public object? Scalar(string sql) => Query(sql, out _).FirstOrDefault()?[0];

    public void Dispose()
    {
        if (_db != 0)
        {
            Api.Close(_db);
            _db = 0;
        }
    }

    /// <summary>A name quoted for SQL ("table" with its quotes doubled).</summary>
    public static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");

    private static string Text(byte* text) => text is null ? string.Empty : Marshal.PtrToStringUTF8((nint)text) ?? string.Empty;

    private static string Text(byte* text, int length) => text is null ? string.Empty : Encoding.UTF8.GetString(text, length);

    /// <summary>The functions of the library (the C API's own names in the comments).</summary>
    private sealed class Native
    {
        public delegate* unmanaged<byte*, nint*, int, byte*, int> Open = null; // sqlite3_open_v2
        public delegate* unmanaged<nint, int> Close = null; // sqlite3_close_v2
        public delegate* unmanaged<nint, byte*, int, nint*, byte**, int> Prepare = null; // sqlite3_prepare_v2
        public delegate* unmanaged<nint, int> Step = null; // sqlite3_step
        public delegate* unmanaged<nint, int> Finalize = null; // sqlite3_finalize
        public delegate* unmanaged<nint, int> ColumnCount = null; // sqlite3_column_count
        public delegate* unmanaged<nint, int, byte*> ColumnName = null; // sqlite3_column_name
        public delegate* unmanaged<nint, int, int> ColumnType = null; // sqlite3_column_type
        public delegate* unmanaged<nint, int, long> ColumnInt64 = null; // sqlite3_column_int64
        public delegate* unmanaged<nint, int, double> ColumnDouble = null; // sqlite3_column_double
        public delegate* unmanaged<nint, int, byte*> ColumnText = null; // sqlite3_column_text
        public delegate* unmanaged<nint, int, byte*> ColumnBlob = null; // sqlite3_column_blob
        public delegate* unmanaged<nint, int, int> ColumnBytes = null; // sqlite3_column_bytes
        public delegate* unmanaged<nint, byte*> ErrorMessage = null; // sqlite3_errmsg
        public delegate* unmanaged<nint, byte*, nint, nint, byte**, int> Exec = null; // sqlite3_exec
        public delegate* unmanaged<byte*> LibVersion = null; // sqlite3_libversion

        /// <summary>The system's library: FM_SQLITE, then the names each system has it under.</summary>
        public static Native? Load()
        {
            string[] names = OperatingSystem.IsWindows() ? ["winsqlite3.dll", "sqlite3.dll", "e_sqlite3.dll"]
                : OperatingSystem.IsMacOS() ? ["/usr/lib/libsqlite3.dylib", "libsqlite3.dylib", "libsqlite3.0.dylib"]
                : ["libsqlite3.so.0", "libsqlite3.so"];
            if (ConfiguredLibrary is { Length: > 0 } chosen)
            {
                names = [chosen, .. names];
            }
            else if (Environment.GetEnvironmentVariable("FM_SQLITE") is { Length: > 0 } configured)
            {
                names = [configured, .. names];
            }
            foreach (var name in names)
            {
                if (NativeLibrary.TryLoad(name, out var library) && Bind(library) is { } native)
                {
                    return native;
                }
            }
            return null;
        }

        private static Native? Bind(nint library)
        {
            nint F(string name) => NativeLibrary.TryGetExport(library, name, out var address) ? address : 0;
            var addresses = new[]
            {
                F("sqlite3_open_v2"), F("sqlite3_close_v2"), F("sqlite3_prepare_v2"), F("sqlite3_step"), F("sqlite3_finalize"),
                F("sqlite3_column_count"), F("sqlite3_column_name"), F("sqlite3_column_type"), F("sqlite3_column_int64"),
                F("sqlite3_column_double"), F("sqlite3_column_text"), F("sqlite3_column_blob"), F("sqlite3_column_bytes"),
                F("sqlite3_errmsg"), F("sqlite3_exec"), F("sqlite3_libversion"),
            };
            if (addresses.Any(a => a == 0))
            {
                return null;
            }
            return new Native
            {
                Open = (delegate* unmanaged<byte*, nint*, int, byte*, int>)addresses[0],
                Close = (delegate* unmanaged<nint, int>)addresses[1],
                Prepare = (delegate* unmanaged<nint, byte*, int, nint*, byte**, int>)addresses[2],
                Step = (delegate* unmanaged<nint, int>)addresses[3],
                Finalize = (delegate* unmanaged<nint, int>)addresses[4],
                ColumnCount = (delegate* unmanaged<nint, int>)addresses[5],
                ColumnName = (delegate* unmanaged<nint, int, byte*>)addresses[6],
                ColumnType = (delegate* unmanaged<nint, int, int>)addresses[7],
                ColumnInt64 = (delegate* unmanaged<nint, int, long>)addresses[8],
                ColumnDouble = (delegate* unmanaged<nint, int, double>)addresses[9],
                ColumnText = (delegate* unmanaged<nint, int, byte*>)addresses[10],
                ColumnBlob = (delegate* unmanaged<nint, int, byte*>)addresses[11],
                ColumnBytes = (delegate* unmanaged<nint, int, int>)addresses[12],
                ErrorMessage = (delegate* unmanaged<nint, byte*>)addresses[13],
                Exec = (delegate* unmanaged<nint, byte*, nint, nint, byte**, int>)addresses[14],
                LibVersion = (delegate* unmanaged<byte*>)addresses[15],
            };
        }
    }
}
