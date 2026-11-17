namespace NextCommander.Core.Net;

/// <summary>
/// The Telnet protocol of one connection (RFC 854 and the options a terminal needs): the received bytes are split into
/// the text to show and the option negotiation, which is answered. The client takes ECHO, SUPPRESS-GO-AHEAD and BINARY
/// from the server, offers TERMINAL-TYPE (RFC 1091, as <see cref="TerminalType"/>) and NAWS (RFC 1073, the window size)
/// when asked, and refuses every other option. Options are only answered when their state changes, so no loops arise.
/// </summary>
public sealed class TelnetProtocol(string terminalType = "XTERM")
{
    public const byte Se = 240;
    public const byte Sb = 250;
    public const byte Will = 251;
    public const byte Wont = 252;
    public const byte Do = 253;
    public const byte Dont = 254;
    public const byte Iac = 255;

    public const byte OptBinary = 0;
    public const byte OptEcho = 1;
    public const byte OptSuppressGoAhead = 3;
    public const byte OptTerminalType = 24;
    public const byte OptWindowSize = 31;

    private const byte TerminalTypeIs = 0;
    private const byte TerminalTypeSend = 1;

    private enum State { Data, Iac, Option, Sub, SubIac, AfterCr }

    private readonly HashSet<byte> _local = []; // options this side does (WILL)
    private readonly HashSet<byte> _remote = []; // options the server does (DO)
    private readonly List<byte> _sub = [];
    private State _state;
    private byte _command;
    private int _columns = 80;
    private int _rows = 24;

    public string TerminalType { get; } = terminalType;

    /// <summary>Whether the server echoes what is typed (it then said WILL ECHO).</summary>
    public bool RemoteEcho => _remote.Contains(OptEcho);

    /// <summary>Whether the server takes the window size (it said DO NAWS).</summary>
    public bool SendsWindowSize => _local.Contains(OptWindowSize);

    /// <summary>
    /// Reads received bytes: returns the text bytes to show and adds the answers to the server (option replies, the
    /// terminal type, the window size) to <paramref name="reply"/>.
    /// </summary>
    public byte[] Receive(ReadOnlySpan<byte> input, List<byte> reply)
    {
        var data = new List<byte>(input.Length);
        foreach (var b in input)
        {
            switch (_state)
            {
                case State.Data:
                case State.AfterCr:
                    var afterCr = _state == State.AfterCr;
                    _state = State.Data;
                    if (b == Iac)
                    {
                        _state = State.Iac;
                    }
                    else if (afterCr && b == 0)
                    {
                        // CR NUL is a bare carriage return
                    }
                    else
                    {
                        data.Add(b);
                        if (b == '\r')
                        {
                            _state = State.AfterCr;
                        }
                    }
                    break;
                case State.Iac:
                    switch (b)
                    {
                        case Iac:
                            data.Add(Iac);
                            _state = State.Data;
                            break;
                        case Will or Wont or Do or Dont:
                            _command = b;
                            _state = State.Option;
                            break;
                        case Sb:
                            _sub.Clear();
                            _state = State.Sub;
                            break;
                        default:
                            _state = State.Data; // NOP, GA, AYT ...: nothing to do
                            break;
                    }
                    break;
                case State.Option:
                    Negotiate(_command, b, reply);
                    _state = State.Data;
                    break;
                case State.Sub:
                    if (b == Iac)
                    {
                        _state = State.SubIac;
                    }
                    else
                    {
                        _sub.Add(b);
                    }
                    break;
                case State.SubIac:
                    if (b == Se)
                    {
                        Subnegotiation(reply);
                        _state = State.Data;
                    }
                    else
                    {
                        _sub.Add(b); // IAC IAC inside: one 255
                        _state = State.Sub;
                    }
                    break;
            }
        }
        return [.. data];
    }

    private void Negotiate(byte command, byte option, List<byte> reply)
    {
        switch (command)
        {
            case Do:
                if (option is OptTerminalType or OptWindowSize or OptSuppressGoAhead or OptBinary)
                {
                    if (_local.Add(option))
                    {
                        reply.AddRange([Iac, Will, option]);
                    }
                    if (option == OptWindowSize)
                    {
                        reply.AddRange(WindowSize(_columns, _rows));
                    }
                }
                else
                {
                    reply.AddRange([Iac, Wont, option]);
                }
                break;
            case Dont:
                if (_local.Remove(option))
                {
                    reply.AddRange([Iac, Wont, option]);
                }
                break;
            case Will:
                if (option is OptEcho or OptSuppressGoAhead or OptBinary)
                {
                    if (_remote.Add(option))
                    {
                        reply.AddRange([Iac, Do, option]);
                    }
                }
                else
                {
                    reply.AddRange([Iac, Dont, option]);
                }
                break;
            case Wont:
                if (_remote.Remove(option))
                {
                    reply.AddRange([Iac, Dont, option]);
                }
                break;
        }
    }

    private void Subnegotiation(List<byte> reply)
    {
        if (_sub is [OptTerminalType, TerminalTypeSend, ..] && _local.Contains(OptTerminalType))
        {
            reply.AddRange([Iac, Sb, OptTerminalType, TerminalTypeIs]);
            reply.AddRange(System.Text.Encoding.ASCII.GetBytes(TerminalType));
            reply.AddRange([Iac, Se]);
        }
    }

    /// <summary>
    /// The window size changed: the NAWS message to send (empty while the server has not asked for the size). The size
    /// is kept and sent when the server asks later.
    /// </summary>
    public byte[] Resize(int columns, int rows)
    {
        _columns = Math.Clamp(columns, 1, ushort.MaxValue);
        _rows = Math.Clamp(rows, 1, ushort.MaxValue);
        return SendsWindowSize ? WindowSize(_columns, _rows) : [];
    }

    private static byte[] WindowSize(int columns, int rows)
    {
        var message = new List<byte> { Iac, Sb, OptWindowSize };
        foreach (var b in new[] { (byte)(columns >> 8), (byte)columns, (byte)(rows >> 8), (byte)rows })
        {
            message.Add(b);
            if (b == Iac)
            {
                message.Add(Iac);
            }
        }
        message.AddRange([Iac, Se]);
        return [.. message];
    }

    /// <summary>
    /// What is typed, ready to send: 255 is doubled, and a carriage return not followed by a line feed becomes the
    /// Telnet new line CR LF (Enter in the terminal sends a bare CR).
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length + 4);
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            output.Add(b);
            if (b == Iac)
            {
                output.Add(Iac);
            }
            else if (b == '\r' && (i + 1 >= data.Length || data[i + 1] != '\n'))
            {
                output.Add((byte)'\n');
            }
        }
        return [.. output];
    }
}
