using System.Globalization;
using System.Text;
using NextCommander.Core.Config;

namespace NextCommander.Core.Net;

/// <summary>An entry of a connection's log: an informational step or an error.</summary>
public enum ConnectionLogLevel
{
    Info,
    Error,
}

/// <summary>One entry of a connection's log (<see cref="Details"/>: an error's causes and stack, or null).</summary>
public sealed record ConnectionLogEntry(DateTime Time, string Connection, string Address, ConnectionLogLevel Level, string Message, string? Details);

/// <summary>
/// The log of each connection (user's request, 18 November 2026): connecting, logging in, the server's key, the
/// session's end and every error with its causes, one file per connection in the log folder
/// (<c>logs/connections/&lt;name&gt;.log</c>). Passwords, keys and tokens are never written. The connection manager shows a
/// connection's log; <see cref="Written"/> lets the program log and the screen follow.
/// </summary>
public static class ConnectionLog
{
    private static readonly Lock s_lock = new();

    /// <summary>A log file is set aside (as .old.log) when it grows past this size.</summary>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>The folder of the log files (null: nothing is written to the disk).</summary>
    public static string? Folder { get; set; }

    /// <summary>Every entry, as it is written (on the thread that wrote it).</summary>
    public static event EventHandler<ConnectionLogEntry>? Written;

    /// <summary>The name a connection is logged under: its own name, else (an address typed, not saved) scheme and server.</summary>
    public static string NameOf(RemoteConnection connection) =>
        connection.Name.Trim() is { Length: > 0 } name && name != "New connection" ? name : $"{connection.Scheme} {connection.Host}".Trim();

    /// <summary>The address shown in the log (no password: the user, the server and the port).</summary>
    public static string AddressOf(RemoteConnection connection) =>
        connection.Protocol == RemoteProtocol.Serial ? $"serial://{connection.Host}"
        : connection.IsCloudDrive ? $"{connection.Scheme}://{connection.User}"
        : $"{connection.Scheme}://{(connection.User.Length > 0 ? connection.User + "@" : string.Empty)}{connection.Host}:{connection.EffectivePort}";

    /// <summary>The log file of a connection (null: no log folder).</summary>
    public static string? FileOf(RemoteConnection connection) =>
        Folder is null ? null : Path.Combine(Folder, SafeName(NameOf(connection)) + ".log");

    public static void Info(RemoteConnection connection, string message) => Write(connection, ConnectionLogLevel.Info, message, null);

    public static void Error(RemoteConnection connection, string message, Exception? exception = null) =>
        Write(connection, ConnectionLogLevel.Error, message, exception);

    /// <summary>
    /// The messages of an exception and its inner exceptions, outermost first, each once ("Could not connect ... ←
    /// No such host is known. (SocketException)").
    /// </summary>
    public static string Causes(Exception exception)
    {
        var lines = new List<string>();
        for (var e = exception; e is not null; e = e.InnerException)
        {
            var message = e.Message.Trim();
            if (message.Length > 0 && !lines.Any(l => l.Contains(message, StringComparison.Ordinal)))
            {
                lines.Add(lines.Count == 0 ? message : $"{message} ({e.GetType().Name})");
            }
            if (e is AggregateException { InnerExceptions.Count: > 1 } aggregate)
            {
                lines.AddRange(aggregate.InnerExceptions.Select(inner => $"{inner.Message.Trim()} ({inner.GetType().Name})").Where(l => !lines.Contains(l)));
                break;
            }
        }
        return string.Join("\n← ", lines);
    }

    /// <summary>The text of a connection's log (its last part when it is long; empty: nothing logged yet).</summary>
    public static string Read(RemoteConnection connection, int maxChars = 64 * 1024)
    {
        if (FileOf(connection) is not { } file)
        {
            return string.Empty;
        }
        lock (s_lock)
        {
            try
            {
                if (!File.Exists(file))
                {
                    return string.Empty;
                }
                var text = File.ReadAllText(file);
                if (text.Length <= maxChars)
                {
                    return text;
                }
                var start = text.IndexOf('\n', text.Length - maxChars);
                return start < 0 ? text[^maxChars..] : text[(start + 1)..];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }
        }
    }

    /// <summary>Removes a connection's log (and the part set aside).</summary>
    public static void Clear(RemoteConnection connection)
    {
        if (FileOf(connection) is not { } file)
        {
            return;
        }
        lock (s_lock)
        {
            try
            {
                File.Delete(file);
                File.Delete(OldFile(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // in use: kept
            }
        }
    }

    private static void Write(RemoteConnection connection, ConnectionLogLevel level, string message, Exception? exception)
    {
        var entry = new ConnectionLogEntry(DateTime.Now, NameOf(connection), AddressOf(connection), level, message,
            exception is null ? null : (Causes(exception) is var causes && causes != message ? causes : null));
        if (FileOf(connection) is { } file)
        {
            var text = new StringBuilder()
                .Append(entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(level == ConnectionLogLevel.Error ? " ERROR " : " INFO  ")
                .Append(entry.Address).Append("  ").Append(message.ReplaceLineEndings(" "));
            if (exception is not null)
            {
                text.Append('\n').Append(Indent(exception.ToString()));
            }
            text.Append('\n');
            lock (s_lock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    if (File.Exists(file) && new FileInfo(file).Length > MaxBytes)
                    {
                        File.Move(file, OldFile(file), overwrite: true);
                    }
                    File.AppendAllText(file, text.ToString());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // the log cannot be written (a full disk, no rights): the connection goes on
                }
            }
        }
        Written?.Invoke(null, entry);
    }

    private static string Indent(string text) => string.Join('\n', text.ReplaceLineEndings("\n").Split('\n').Select(l => "    " + l));

    private static string OldFile(string file) => Path.ChangeExtension(file, ".old.log");

    /// <summary>A connection's name as a file name (characters a file name cannot have become _).</summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var safe = string.Concat(name.Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch)).Trim().TrimEnd('.');
        return safe.Length > 0 ? safe : "connection";
    }
}
