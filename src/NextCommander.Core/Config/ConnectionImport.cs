using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace NextCommander.Core.Config;

/// <summary>A connection read from another program, with its saved password when it had one.</summary>
public sealed record ImportedConnection(RemoteConnection Connection, string? Password);

/// <summary>
/// Saved sites of FileZilla (sitemanager.xml) and WinSCP (WinSCP.ini, or its registry key on Windows) as connections:
/// their folders, protocol, server, user, key file, start folder and the options the connection manager has. Saved
/// passwords come along (FileZilla's base64 ones; WinSCP's obfuscated ones); FileZilla's master-password-encrypted
/// ones cannot be read and are left out.
/// </summary>
public static partial class ConnectionImport
{
    // ---------------------------------------------------------------- FileZilla

    /// <summary>FileZilla's site manager file of this user.</summary>
    public static string FileZillaFile => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileZilla", "sitemanager.xml")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "filezilla", "sitemanager.xml");

    /// <summary>The sites of a sitemanager.xml (folders become connection folders under <paramref name="baseFolder"/>).</summary>
    public static IReadOnlyList<ImportedConnection> FromFileZilla(string xml, string baseFolder = "FileZilla")
    {
        var result = new List<ImportedConnection>();
        var servers = XDocument.Parse(xml).Root?.Element("Servers");
        if (servers is not null)
        {
            ReadFileZillaFolder(servers, baseFolder, result);
        }
        return result;
    }

    private static void ReadFileZillaFolder(XElement folder, string path, List<ImportedConnection> result)
    {
        foreach (var element in folder.Elements())
        {
            if (element.Name == "Folder")
            {
                var name = string.Concat(element.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
                ReadFileZillaFolder(element, Join(path, name.Length > 0 ? name : "Folder"), result);
            }
            else if (element.Name == "Server" && FileZillaServer(element, path) is { } server)
            {
                result.Add(server);
            }
        }
    }

    private static ImportedConnection? FileZillaServer(XElement server, string folder)
    {
        string Value(string name) => server.Element(name)?.Value.Trim() ?? string.Empty;
        var protocol = Value("Protocol") switch
        {
            "1" => RemoteProtocol.Sftp,
            "3" => RemoteProtocol.FtpsImplicit,
            "4" => RemoteProtocol.FtpsExplicit,
            "0" or "6" or "" => RemoteProtocol.Ftp,
            _ => (RemoteProtocol?)null, // HTTP, S3 and the cloud kinds of FileZilla Pro: not imported
        };
        var host = Value("Host");
        if (protocol is null || host.Length == 0)
        {
            return null;
        }
        var connection = new RemoteConnection
        {
            Name = Value("Name") is { Length: > 0 } name ? name : host,
            Protocol = protocol.Value,
            Host = host,
            Port = int.TryParse(Value("Port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port != RemoteConnection.DefaultPort(protocol.Value) ? port : 0,
            User = Value("Logontype") == "1" ? string.Empty : Value("User"),
            Folder = folder,
            Passive = Value("PasvMode") != "MODE_ACTIVE",
            InitialPath = FileZillaPath(Value("RemoteDir")),
            FtpEncoding = Value("EncodingType") switch
            {
                "UTF-8" => "utf-8",
                "Custom" when Value("CustomEncoding") is { Length: > 0 } custom => custom,
                _ => null,
            },
        };
        if (Value("Logontype") == "5" && Value("Keyfile") is { Length: > 0 } key)
        {
            connection.KeyFile = key;
        }
        string? password = null;
        var pass = server.Element("Pass");
        if (pass is not null && (string?)pass.Attribute("encoding") == "base64")
        {
            try
            {
                password = Encoding.UTF8.GetString(Convert.FromBase64String(pass.Value.Trim()));
            }
            catch (FormatException)
            {
            }
        }
        else if (pass is not null && pass.Attribute("encoding") is null)
        {
            password = pass.Value; // very old files: plain text
        }
        connection.SavePassword = password is not null;
        return new ImportedConnection(connection, password);
    }

    /// <summary>FileZilla's start folder: "1 0 3 var 3 www" (type, prefix length, then length-prefixed parts) → "/var/www".</summary>
    public static string FileZillaPath(string encoded)
    {
        var parts = encoded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return "/";
        }
        var names = new List<string>();
        // the parts may contain spaces: re-read by the lengths from the original text
        var rest = encoded[(encoded.IndexOf(' ', encoded.IndexOf(' ', StringComparison.Ordinal) + 1) + 1)..];
        while (rest.Length > 0)
        {
            var space = rest.IndexOf(' ', StringComparison.Ordinal);
            if (space < 0 || !int.TryParse(rest[..space], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) || space + 1 + length > rest.Length)
            {
                break;
            }
            names.Add(rest.Substring(space + 1, length));
            rest = rest[Math.Min(rest.Length, space + 1 + length)..].TrimStart();
        }
        return "/" + string.Join('/', names);
    }

    // ---------------------------------------------------------------- WinSCP

    /// <summary>WinSCP's settings file of this user (when it keeps them in a file rather than the registry).</summary>
    public static string WinScpFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinSCP.ini");

    /// <summary>The sessions of a WinSCP.ini ("[Sessions\Folder/Name]" sections).</summary>
    public static IReadOnlyList<ImportedConnection> FromWinScpIni(string ini, string baseFolder = "WinSCP")
    {
        var sessions = new List<(string Name, Dictionary<string, string> Values)>();
        Dictionary<string, string>? current = null;
        foreach (var raw in ini.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var section = line[1..^1];
                current = null;
                if (section.StartsWith(@"Sessions\", StringComparison.OrdinalIgnoreCase))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sessions.Add((section[@"Sessions\".Length..], current));
                }
                continue;
            }
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (current is not null && equals > 0)
            {
                current[line[..equals]] = line[(equals + 1)..];
            }
        }
        return [.. sessions.Select(s => FromWinScpSession(s.Name, s.Values, baseFolder)).OfType<ImportedConnection>()];
    }

    /// <summary>The sessions of WinSCP's registry key (Windows).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static IReadOnlyList<ImportedConnection> FromWinScpRegistry(string baseFolder = "WinSCP")
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Martin Prikryl\WinSCP 2\Sessions");
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
            if (FromWinScpSession(name, values, baseFolder) is { } imported)
            {
                result.Add(imported);
            }
        }
        return result;
    }

    /// <summary>One WinSCP session ("Work/Prod%20web" → folder Work, name "Prod web").</summary>
    public static ImportedConnection? FromWinScpSession(string encodedName, IReadOnlyDictionary<string, string> values, string baseFolder = "WinSCP")
    {
        string Value(string name) => values.TryGetValue(name, out var v) ? Uri.UnescapeDataString(v.Trim()) : string.Empty;
        var host = Value("HostName");
        if (host.Length == 0 || encodedName.Equals("Default%20Settings", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var ftps = Value("Ftps");
        RemoteProtocol? protocol = Value("FSProtocol") switch
        {
            "0" => RemoteProtocol.Scp,
            "5" => ftps switch { "1" => RemoteProtocol.FtpsImplicit, "2" or "3" => RemoteProtocol.FtpsExplicit, _ => RemoteProtocol.Ftp },
            "6" => ftps is "1" or "2" or "3" ? RemoteProtocol.WebDavs : RemoteProtocol.WebDav,
            "7" => RemoteProtocol.S3,
            "1" or "2" or "" => RemoteProtocol.Sftp,
            _ => null,
        };
        if (protocol is null)
        {
            return null;
        }
        var parts = Uri.UnescapeDataString(encodedName).Split('/');
        var user = Value("UserName");
        var connection = new RemoteConnection
        {
            Name = parts[^1],
            Folder = Join(baseFolder, string.Join('/', parts[..^1])),
            Protocol = protocol.Value,
            Host = host,
            Port = int.TryParse(Value("PortNumber"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port != RemoteConnection.DefaultPort(protocol.Value) ? port : 0,
            User = user,
            KeyFile = Value("PublicKeyFile") is { Length: > 0 } key ? key : null,
            InitialPath = Value("RemoteDirectory") is { Length: > 0 } dir ? dir : "/",
            Passive = Value("FtpPasvMode") != "0",
        };
        if (Value("Tunnel") == "1" && Value("TunnelHostName") is { Length: > 0 } tunnelHost)
        {
            var tunnelUser = Value("TunnelUserName");
            var tunnelPort = Value("TunnelPortNumber");
            connection.JumpHost = (tunnelUser.Length > 0 ? tunnelUser + "@" : string.Empty) + tunnelHost + (tunnelPort is { Length: > 0 } and not "22" ? ":" + tunnelPort : string.Empty);
        }
        if (int.TryParse(Value("Timeout"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout) && timeout is > 0 and not 15)
        {
            connection.TimeoutSeconds = timeout;
        }
        var password = values.TryGetValue("Password", out var obfuscated) && obfuscated.Length > 0 ? WinScpPassword(obfuscated, user, host) : null;
        connection.SavePassword = password is not null;
        // WinSCP tries the agent by default: kept for SSH sessions that have neither a key file nor a saved password
        connection.UseSshAgent = protocol is RemoteProtocol.Sftp or RemoteProtocol.Scp && connection.KeyFile is null && password is null && Value("TryAgent") != "0";
        return new ImportedConnection(connection, password);
    }

    /// <summary>WinSCP's obfuscated password (hex; each byte ~(b ^ 0xA3)); the user and host are stripped from its front.</summary>
    public static string? WinScpPassword(string hex, string user, string host)
    {
        const int Magic = 0xA3;
        const int Flag = 0xFF;
        var position = 0;
        int Next()
        {
            if (position + 2 > hex.Length)
            {
                throw new FormatException();
            }
            var value = int.Parse(hex.AsSpan(position, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            position += 2;
            return ~(value ^ Magic) & 0xFF;
        }
        try
        {
            var flag = Next();
            int length;
            if (flag == Flag)
            {
                Next(); // reserved
                length = Next();
            }
            else
            {
                length = flag;
            }
            var padding = Next(); // random padding before the text (read first: Next moves the position)
            position += padding * 2;
            var text = new StringBuilder(length);
            for (var i = 0; i < length; i++)
            {
                text.Append((char)Next());
            }
            var result = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(text.ToString()));
            if (flag == Flag)
            {
                var key = user + host;
                if (!result.StartsWith(key, StringComparison.Ordinal))
                {
                    return null;
                }
                result = result[key.Length..];
            }
            return result;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Join(string folder, string name) =>
        string.Join('/', new[] { folder.Trim('/'), name.Trim('/') }.Where(p => p.Length > 0));
}
