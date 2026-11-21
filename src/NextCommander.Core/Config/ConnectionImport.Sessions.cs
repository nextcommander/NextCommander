using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NextCommander.Core.Config;

/// <summary>
/// PuTTY sessions (the registry on Windows, ~/.putty/sessions elsewhere), MobaXterm's session files (.mxtsessions and the
/// bookmarks of MobaXterm.ini) and NextCommander's own connection files (export / import without passwords; MobaXterm's
/// session import / export, user's request, 21 November 2026).
/// </summary>
public static partial class ConnectionImport
{
    // ---------------------------------------------------------------- PuTTY

    /// <summary>PuTTY's session folder on Linux and macOS.</summary>
    public static string PuttyFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".putty", "sessions");

    /// <summary>The sessions of PuTTY's registry key (Windows).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static IReadOnlyList<ImportedConnection> FromPuttyRegistry(string baseFolder = "PuTTY")
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\SimonTatham\PuTTY\Sessions");
        if (key is null)
        {
            return [];
        }
        var result = new List<ImportedConnection>();
        foreach (var name in key.GetSubKeyNames())
        {
            using var session = key.OpenSubKey(name);
            if (session is null)
            {
                continue;
            }
            var values = session.GetValueNames().ToDictionary(v => v, v => Convert.ToString(session.GetValue(v), CultureInfo.InvariantCulture) ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
            if (FromPuttySession(name, values, baseFolder) is { } imported)
            {
                result.Add(imported);
            }
        }
        return result;
    }

    /// <summary>The session files of PuTTY on Linux and macOS (one file per session, "Key=Value" lines).</summary>
    public static IReadOnlyList<ImportedConnection> FromPuttyFolder(string folder, string baseFolder = "PuTTY")
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }
        var result = new List<ImportedConnection>();
        foreach (var file in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(file))
            {
                var equals = line.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                {
                    values[line[..equals]] = line[(equals + 1)..].TrimEnd('\r');
                }
            }
            if (FromPuttySession(Path.GetFileName(file), values, baseFolder) is { } imported)
            {
                result.Add(imported);
            }
        }
        return result;
    }

    /// <summary>
    /// One PuTTY session ("Web%20server" → "Web server"): SSH, Telnet, rlogin and serial; the user, key file, remote
    /// command, terminal type and proxy. PuTTY keeps no passwords.
    /// </summary>
    public static ImportedConnection? FromPuttySession(string encodedName, IReadOnlyDictionary<string, string> values, string baseFolder = "PuTTY")
    {
        string Value(string name) => values.TryGetValue(name, out var v) ? v.Trim() : string.Empty;
        int Number(string name) => int.TryParse(Value(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        var name = Uri.UnescapeDataString(encodedName);
        if (name.Equals("Default Settings", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        RemoteProtocol? protocol = Value("Protocol").ToLowerInvariant() switch
        {
            "ssh" or "" => RemoteProtocol.Ssh,
            "telnet" => RemoteProtocol.Telnet,
            "rlogin" => RemoteProtocol.Rsh,
            "serial" => RemoteProtocol.Serial,
            _ => null, // raw, SUPDUP, bare SSH connections
        };
        if (protocol is not { } kind)
        {
            return null;
        }
        var connection = new RemoteConnection { Name = name, Folder = baseFolder, Protocol = kind, InitialPath = "/" };
        if (kind == RemoteProtocol.Serial)
        {
            connection.Host = Value("SerialLine");
            if (connection.Host.Length == 0)
            {
                return null;
            }
            connection.BaudRate = Number("SerialSpeed") is > 0 and var speed ? speed : connection.BaudRate;
            connection.DataBits = Number("SerialDataBits") is >= 5 and <= 8 and var bits ? bits : connection.DataBits;
            connection.StopBits = Number("SerialStopHalfbits") switch { 3 => SerialStopBits.OnePointFive, 4 => SerialStopBits.Two, _ => SerialStopBits.One };
            connection.Parity = Number("SerialParity") switch { 1 => SerialParity.Odd, 2 => SerialParity.Even, 3 => SerialParity.Mark, 4 => SerialParity.Space, _ => SerialParity.None };
            connection.FlowControl = Number("SerialFlowControl") switch { 1 => SerialFlowControl.XOnXOff, 2 => SerialFlowControl.RtsCts, _ => SerialFlowControl.None };
            return new ImportedConnection(connection, null);
        }
        var host = Value("HostName");
        var user = Value("UserName");
        var at = host.LastIndexOf('@');
        if (at > 0)
        {
            user = user.Length > 0 ? user : host[..at];
            host = host[(at + 1)..];
        }
        if (host.Length == 0)
        {
            return null;
        }
        connection.Host = host;
        connection.User = user;
        var port = Number("PortNumber");
        connection.Port = port > 0 && port != RemoteConnection.DefaultPort(kind) ? port : 0;
        if (kind == RemoteProtocol.Ssh)
        {
            connection.KeyFile = Value("PublicKeyFile") is { Length: > 0 } key ? key : null;
            connection.UseSshAgent = connection.KeyFile is null && Value("TryAgent") != "0";
            connection.RemoteCommand = Value("RemoteCommand") is { Length: > 0 } command ? command : null;
        }
        if (kind == RemoteProtocol.Rsh && Value("LocalUserName") is { Length: > 0 } local)
        {
            connection.LocalUser = local;
        }
        if (Value("TerminalType") is { Length: > 0 } terminal && terminal != "xterm")
        {
            connection.TerminalType = terminal;
        }
        if (Number("PingIntervalSecs") is > 0 and var keepAlive)
        {
            connection.KeepAliveSeconds = keepAlive;
        }
        SetProxy(connection, Number("ProxyMethod"), Value("ProxyHost"), Number("ProxyPort"), Value("ProxyUsername"));
        return new ImportedConnection(connection, null);
    }

    /// <summary>
    /// The connection's own proxy from PuTTY's / MobaXterm's numbers (1 SOCKS4, 2 SOCKS5, 3 HTTP, 6 SSH); Telnet and local
    /// command proxies have no counterpart and are left out.
    /// </summary>
    private static void SetProxy(RemoteConnection connection, int method, string host, int port, string user)
    {
        Net.ProxyKind? kind = method switch
        {
            1 => Net.ProxyKind.Socks4,
            2 => Net.ProxyKind.Socks5,
            3 => Net.ProxyKind.Http,
            6 => Net.ProxyKind.Ssh,
            _ => null,
        };
        if (kind is null || host.Length == 0)
        {
            return;
        }
        connection.Proxy = Net.ConnectionProxy.Custom;
        connection.ProxyKind = kind.Value;
        connection.ProxyHost = host;
        connection.ProxyPort = port;
        connection.ProxyUser = user.Length > 0 ? user : null;
    }

    // ---------------------------------------------------------------- MobaXterm

    /// <summary>MobaXterm's settings file of this user (the installed version; the portable one keeps it next to the program).</summary>
    public static string MobaXtermIni => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MobaXterm", "MobaXterm.ini");

    /// <summary>
    /// The sessions of a MobaXterm session file (.mxtsessions) or of MobaXterm.ini: the "[Bookmarks]" / "[Bookmarks_N]"
    /// sections, their folder in "SubRep" (Folder\Sub), one "Name=#icon#settings#terminal#..." line per session. SSH,
    /// SFTP, RDP and VNC with their logins, key files, SSH gateways and proxies; Telnet, rsh and FTP by host, port and user.
    /// MobaXterm keeps its passwords elsewhere (encrypted): none are imported.
    /// </summary>
    public static IReadOnlyList<ImportedConnection> FromMobaXterm(string text, string baseFolder = "MobaXterm")
    {
        var result = new List<ImportedConnection>();
        string? folder = null;
        var inBookmarks = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var section = line[1..^1];
                inBookmarks = section.Equals("Bookmarks", StringComparison.OrdinalIgnoreCase)
                    || section.StartsWith("Bookmarks_", StringComparison.OrdinalIgnoreCase);
                folder = string.Empty;
                continue;
            }
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (!inBookmarks || equals <= 0)
            {
                continue;
            }
            var key = line[..equals];
            var value = line[(equals + 1)..];
            if (key.Equals("SubRep", StringComparison.OrdinalIgnoreCase))
            {
                folder = value.Replace('\\', '/').Trim('/');
                continue;
            }
            if (key.Equals("ImgNum", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (FromMobaXtermSession(key, value, Join(baseFolder, folder ?? string.Empty)) is { } imported)
            {
                result.Add(imported);
            }
        }
        return result;
    }

    /// <summary>One MobaXterm session line ("#109#0%host%22%user%..."): null for the kinds without a counterpart (shell, WSL, X11 ...).</summary>
    public static ImportedConnection? FromMobaXtermSession(string name, string value, string folder)
    {
        var groups = value.Split('#');
        if (groups.Length < 3)
        {
            return null;
        }
        var f = groups[2].Split('%');
        string At(int i) => i < f.Length ? MobaText(f[i]) : string.Empty;
        bool On(int i) => At(i) == "-1";
        int Number(int i) => int.TryParse(At(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        RemoteProtocol? protocol = At(0) switch
        {
            "0" => RemoteProtocol.Ssh,
            "1" => RemoteProtocol.Telnet,
            "2" => RemoteProtocol.Rsh,
            "4" => RemoteProtocol.Rdp,
            "5" => RemoteProtocol.Vnc,
            "6" => RemoteProtocol.Ftp,
            "7" => RemoteProtocol.Sftp,
            _ => null,
        };
        if (protocol is not { } kind || At(1).Length == 0)
        {
            return null;
        }
        var connection = new RemoteConnection
        {
            Name = name.Trim(),
            Folder = folder,
            Protocol = kind,
            Host = At(1),
            Port = Number(2) is > 0 and var port && port != RemoteConnection.DefaultPort(kind) ? port : 0,
            User = kind == RemoteProtocol.Vnc || At(3) == "<default>" ? string.Empty : At(3),
            InitialPath = "/",
        };
        switch (kind)
        {
            case RemoteProtocol.Ssh:
                connection.RemoteCommand = At(7) is { Length: > 0 } command ? command : null;
                connection.JumpHost = Gateway(At(8), At(9), At(10));
                connection.KeyFile = MobaPath(At(14));
                SetProxy(connection, Number(19), At(20), Number(21), At(22));
                connection.UseSshAgent = On(33) || connection.KeyFile is null;
                break;
            case RemoteProtocol.Sftp:
                connection.InitialPath = At(6) is { Length: > 0 } dir ? dir : "/";
                connection.KeyFile = MobaPath(At(9));
                connection.UseSshAgent = connection.KeyFile is null;
                SetProxy(connection, Number(10), At(11), Number(12), At(13));
                break;
            case RemoteProtocol.Rdp:
                connection.RdpAdmin = On(4);
                connection.RdpDrives = On(6);
                connection.RdpPrinters = On(7);
                connection.RdpClipboard = At(19) != "0";
                connection.RdpGateway = At(20) is { Length: > 0 } gateway ? gateway : null;
                connection.RdpMicrophone = On(25);
                connection.RdpSmartCards = On(29);
                break;
            case RemoteProtocol.Vnc:
                connection.VncViewOnly = On(4);
                break;
        }
        return new ImportedConnection(connection, null);
    }

    /// <summary>The first SSH gateway of MobaXterm's lists ("bastion", "22", "admin") as "admin@bastion" (the port when not 22).</summary>
    private static string? Gateway(string hosts, string ports, string users)
    {
        static string First(string list) => list.Split("__PIPE__")[0].Trim();
        var host = First(hosts);
        if (host.Length == 0)
        {
            return null;
        }
        var user = First(users);
        var port = First(ports);
        return (user.Length > 0 ? user + "@" : string.Empty) + host + (port is { Length: > 0 } and not "22" ? ":" + port : string.Empty);
    }

    /// <summary>MobaXterm's escapes in a value (__PTVIRG__ ";", __DBLQUO__ "\"", __PIPE__ is kept for the lists, __DIEZE__ "#", __PERCENT__ "%").</summary>
    private static string MobaText(string value) => value
        .Replace("__PTVIRG__", ";", StringComparison.Ordinal)
        .Replace("__DBLQUO__", "\"", StringComparison.Ordinal)
        .Replace("__DIEZE__", "#", StringComparison.Ordinal)
        .Replace("__PERCENT__", "%", StringComparison.Ordinal)
        .Trim();

    /// <summary>A path of MobaXterm: _CurrentDrive_ is the drive letter, _ProfileDir_ the user's folder, _MyDocuments_ the documents.</summary>
    private static string? MobaPath(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }
        var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) is { Length: >= 2 } root && root[1] == ':' ? root[..1] : "C";
        return value
            .Replace("_CurrentDrive_", drive, StringComparison.Ordinal)
            .Replace("_ProfileDir_", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.Ordinal)
            .Replace("_MyDocuments_", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- NextCommander's own file

    /// <summary>The kind written at the top of an exported file (a file of another kind is refused).</summary>
    public const string ExportFormat = "NextCommander connections";

    private sealed class ExportFile
    {
        public string Format { get; set; } = string.Empty;

        public int Version { get; set; }

        public List<ConnectionFolder> Folders { get; set; } = [];

        public List<RemoteConnection> Connections { get; set; } = [];
    }

    private static readonly JsonSerializerOptions s_exportJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The connections of a folder ("" all) with their folders as a file to give to another computer or person: no
    /// passwords (they stay in this computer's password store), no client secret, no saved-password flag.
    /// </summary>
    public static string Export(AppSettings settings, string folder = "")
    {
        var file = new ExportFile
        {
            Format = ExportFormat,
            Version = 1,
            Folders = [.. settings.ConnectionFolders.Where(f => ConnectionFolders.IsInside(f.Path, folder))],
            Connections = [.. settings.Connections.Where(c => ConnectionFolders.IsInside(c.Folder, folder)).Select(c =>
            {
                var copy = c.Clone();
                copy.SavePassword = false;
                copy.ClientSecret = null;
                return copy;
            })],
        };
        return JsonSerializer.Serialize(file, s_exportJson);
    }

    /// <summary>
    /// The connections of an exported file, put under <paramref name="baseFolder"/> ("" as they were); a name taken in
    /// the same folder gets " (2)", " (3)" ... A file that is not such a file throws <see cref="FormatException"/>.
    /// </summary>
    public static IReadOnlyList<ImportedConnection> FromExport(string json, AppSettings existing, string baseFolder = "")
    {
        ExportFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ExportFile>(json, s_exportJson);
        }
        catch (JsonException ex)
        {
            throw new FormatException("This is not a connections file of NextCommander.", ex);
        }
        if (file is null || file.Format != ExportFormat)
        {
            throw new FormatException("This is not a connections file of NextCommander.");
        }
        var taken = existing.Connections.Select(c => (c.Folder, c.Name)).ToHashSet();
        var result = new List<ImportedConnection>();
        foreach (var connection in file.Connections)
        {
            connection.Folder = Join(baseFolder, connection.Folder ?? string.Empty);
            connection.SavePassword = false;
            var name = connection.Name;
            for (var n = 2; taken.Contains((connection.Folder, name)); n++)
            {
                name = $"{connection.Name} ({n.ToString(CultureInfo.InvariantCulture)})";
            }
            connection.Name = name;
            taken.Add((connection.Folder, name));
            result.Add(new ImportedConnection(connection, null));
        }
        return result;
    }

    /// <summary>The folder colors of an exported file (for the folders that do not exist yet).</summary>
    public static IReadOnlyList<ConnectionFolder> FoldersOfExport(string json, string baseFolder = "")
    {
        try
        {
            var file = JsonSerializer.Deserialize<ExportFile>(json, s_exportJson);
            return file?.Format == ExportFormat
                ? [.. file.Folders.Select(f => new ConnectionFolder { Path = Join(baseFolder, f.Path), Color = f.Color })]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static readonly Encoding s_latin = Encoding.Latin1;

    /// <summary>A MobaXterm file's text: UTF-8 when it is valid UTF-8 (newer versions), else Windows-1252 (read as Latin-1).</summary>
    public static string ReadMobaXtermFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return s_latin.GetString(bytes);
        }
    }
}
