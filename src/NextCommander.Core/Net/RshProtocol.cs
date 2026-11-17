using System.Text;
using NextCommander.Core.Config;

namespace NextCommander.Core.Net;

/// <summary>
/// The BSD remote shell protocols of an rsh connection: rlogin (RFC 1282, port 513) for a login session when no command
/// is given, rsh (port 514) to run one command. The server trusts the client by the user names and its .rhosts /
/// hosts.equiv; it usually also wants the connection to come from a port below 1024. Nothing is encrypted.
/// </summary>
public static class RshProtocol
{
    /// <summary>The rlogin port.</summary>
    public const int LoginPort = 513;

    /// <summary>The rsh port (also the type's default port).</summary>
    public const int ShellPort = 514;

    /// <summary>The bit of the server's urgent control byte that asks for the window size (TIOCPKT_WINDOW).</summary>
    public const byte WindowRequest = 0x80;

    /// <summary>
    /// rlogin's window size message: the magic FF FF 's' 's', then rows, columns, width and height in pixels (0: not
    /// known), each a 16-bit number, most significant byte first.
    /// </summary>
    public static byte[] WindowSize(int columns, int rows)
    {
        static (byte, byte) Word(int value) => ((byte)(Math.Clamp(value, 0, 65535) >> 8), (byte)Math.Clamp(value, 0, 65535));
        var (rowHigh, rowLow) = Word(rows);
        var (columnHigh, columnLow) = Word(columns);
        return [0xFF, 0xFF, (byte)'s', (byte)'s', rowHigh, rowLow, columnHigh, columnLow, 0, 0, 0, 0];
    }

    /// <summary>Whether the connection opens a login session (no command): rlogin.</summary>
    public static bool IsLogin(RemoteConnection connection) => string.IsNullOrWhiteSpace(connection.RemoteCommand);

    /// <summary>The port connected to: the connection's own, else 513 for a login and 514 for a command.</summary>
    public static int PortOf(RemoteConnection connection) =>
        connection.Port > 0 && connection.Port != ShellPort ? connection.Port : IsLogin(connection) ? LoginPort : ShellPort;

    /// <summary>The user on this computer the server checks (the connection's, else the one logged in).</summary>
    public static string LocalUserOf(RemoteConnection connection) =>
        string.IsNullOrWhiteSpace(connection.LocalUser) ? Environment.UserName : connection.LocalUser.Trim();

    /// <summary>The user on the server (the connection's, else the same as here).</summary>
    public static string RemoteUserOf(RemoteConnection connection) =>
        string.IsNullOrWhiteSpace(connection.User) ? LocalUserOf(connection) : connection.User.Trim();

    /// <summary>
    /// What the client sends first. rlogin: NUL, local user, remote user, terminal type/speed; rsh: the stderr port
    /// ("0": none, the errors come with the output), local user, remote user, the command; each ends with a NUL.
    /// </summary>
    public static byte[] Handshake(RemoteConnection connection)
    {
        var parts = new List<string>();
        if (IsLogin(connection))
        {
            var terminal = string.IsNullOrWhiteSpace(connection.TerminalType) ? "xterm" : connection.TerminalType.Trim().ToLowerInvariant();
            parts.AddRange([string.Empty, LocalUserOf(connection), RemoteUserOf(connection), terminal + "/38400"]);
        }
        else
        {
            parts.AddRange(["0", LocalUserOf(connection), RemoteUserOf(connection), connection.RemoteCommand!.Trim()]);
        }
        var bytes = new List<byte>();
        foreach (var part in parts)
        {
            bytes.AddRange(Encoding.UTF8.GetBytes(part));
            bytes.Add(0);
        }
        return [.. bytes];
    }
}
