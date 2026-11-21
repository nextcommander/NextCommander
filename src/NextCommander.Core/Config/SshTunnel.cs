using System.Globalization;

namespace NextCommander.Core.Config;

/// <summary>Which way an SSH tunnel forwards.</summary>
public enum TunnelKind
{
    /// <summary>A port of this computer leads to a host and port as the server sees them (a database behind it).</summary>
    Local,

    /// <summary>A port of the server leads to a host and port as this computer sees them (the server reaches a local service).</summary>
    Remote,

    /// <summary>A SOCKS proxy on this computer: every connection through it leaves from the server.</summary>
    Dynamic,
}

/// <summary>
/// A saved SSH tunnel (the tunnel manager of the Net menu; MobaXterm's SSH tunnels, user's request, 21 November 2026):
/// port forwarding through a saved SSH, SFTP or SCP connection, started by hand or with the program.
/// </summary>
public sealed class SshTunnel
{
    public string Name { get; set; } = string.Empty;

    public TunnelKind Kind { get; set; } = TunnelKind.Local;

    /// <summary>The name of the saved connection the tunnel goes through (SSH, SFTP or SCP).</summary>
    public string Connection { get; set; } = string.Empty;

    /// <summary>Where the tunnel listens: on this computer (local, SOCKS) or on the server (remote).</summary>
    public string ListenAddress { get; set; } = "127.0.0.1";

    public int ListenPort { get; set; }

    /// <summary>Where it leads (not for SOCKS): seen from the server (local) or from this computer (remote).</summary>
    public string TargetHost { get; set; } = string.Empty;

    public int TargetPort { get; set; }

    /// <summary>Started when NextCommander starts.</summary>
    public bool StartWithApp { get; set; }

    public SshTunnel Clone() => (SshTunnel)MemberwiseClone();

    /// <summary>The connections a tunnel can go through: SSH logins.</summary>
    public static bool CanCarry(RemoteConnection connection) => connection.Protocol is RemoteProtocol.Ssh or RemoteProtocol.Sftp or RemoteProtocol.Scp;

    /// <summary>"127.0.0.1:5432 → db:5432", "SOCKS 127.0.0.1:1080".</summary>
    public string Route => Kind == TunnelKind.Dynamic
        ? "SOCKS " + Endpoint(ListenAddress, ListenPort)
        : Endpoint(ListenAddress, ListenPort) + " → " + Endpoint(TargetHost, TargetPort);

    private static string Endpoint(string host, int port) => (host.Contains(':', StringComparison.Ordinal) ? "[" + host + "]" : host) + ":" + port.ToString(CultureInfo.InvariantCulture);

    /// <summary>What is wrong with the tunnel (field, English text), empty when it can start.</summary>
    public IReadOnlyList<(string Field, string Text)> Problems(IEnumerable<SshTunnel> others, IEnumerable<RemoteConnection> connections)
    {
        var problems = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            problems.Add(("Name", "Enter a name for the tunnel."));
        }
        else if (others.Any(o => !ReferenceEquals(o, this) && string.Equals(o.Name.Trim(), Name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add(("Name", "Another tunnel has this name."));
        }
        if (!connections.Any(c => CanCarry(c) && c.Name == Connection))
        {
            problems.Add(("Connection", "Choose the SSH connection the tunnel goes through."));
        }
        if (string.IsNullOrWhiteSpace(ListenAddress))
        {
            problems.Add(("ListenAddress", "Enter the address to listen on (127.0.0.1: this computer only)."));
        }
        if (ListenPort is < 1 or > 65535)
        {
            problems.Add(("ListenPort", "Enter a port from 1 to 65535."));
        }
        if (Kind != TunnelKind.Dynamic)
        {
            if (string.IsNullOrWhiteSpace(TargetHost))
            {
                problems.Add(("TargetHost", "Enter the host the tunnel leads to."));
            }
            if (TargetPort is < 1 or > 65535)
            {
                problems.Add(("TargetPort", "Enter a port from 1 to 65535."));
            }
        }
        return problems;
    }
}
