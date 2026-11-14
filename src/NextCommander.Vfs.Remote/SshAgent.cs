using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace NextCommander.Vfs.Remote;

/// <summary>One key the agent holds: its public key blob (SSH wire format) and comment.</summary>
public sealed record AgentIdentity(byte[] Blob, string Comment)
{
    /// <summary>The key type the blob starts with ("ssh-ed25519", "ssh-rsa", "ecdsa-sha2-nistp256" ...).</summary>
    public string KeyType => SshAgent.ReadString(Blob, 0, out _);
}

/// <summary>
/// A client of the SSH agent protocol (draft-miller-ssh-agent): the keys stay in the agent, which signs the login.
/// Agents: SSH_AUTH_SOCK (a Unix socket; on Windows also a named pipe path), the Windows OpenSSH agent
/// (\\.\pipe\openssh-ssh-agent) and PuTTY's Pageant (its window message protocol).
/// </summary>
public sealed partial class SshAgent
{
    private const byte Failure = 5;
    private const byte RequestIdentities = 11;
    private const byte IdentitiesAnswer = 12;
    private const byte SignRequest = 13;
    private const byte SignResponse = 14;

    /// <summary>RSA signatures with SHA-256 / SHA-512 instead of SHA-1 (sign request flags).</summary>
    public const uint RsaSha256 = 2;

    public const uint RsaSha512 = 4;

    private readonly Func<byte[], byte[]> _transport;

    /// <param name="transport">Sends one request (type + payload) and returns the reply (type + payload).</param>
    public SshAgent(Func<byte[], byte[]> transport, string name)
    {
        _transport = transport;
        Name = name;
    }

    /// <summary>Where the agent was found ("OpenSSH agent", "Pageant", the socket path).</summary>
    public string Name { get; }

    /// <summary>The agent running for this user; null when none answers.</summary>
    public static SshAgent? Find()
    {
        var socket = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        if (!string.IsNullOrEmpty(socket))
        {
            if (OperatingSystem.IsWindows() && socket.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
            {
                return new SshAgent(r => OverPipe(socket[@"\\.\pipe\".Length..], r), socket);
            }
            if (!OperatingSystem.IsWindows() && File.Exists(socket))
            {
                return new SshAgent(r => OverSocket(socket, r), socket);
            }
        }
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(@"\\.\pipe\openssh-ssh-agent"))
            {
                return new SshAgent(r => OverPipe("openssh-ssh-agent", r), "OpenSSH agent");
            }
            if (Pageant.IsRunning)
            {
                return new SshAgent(Pageant.Query, "Pageant");
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- messages

    /// <summary>The keys the agent holds.</summary>
    public IReadOnlyList<AgentIdentity> Identities()
    {
        var reply = _transport([RequestIdentities]);
        if (reply.Length == 0 || reply[0] != IdentitiesAnswer)
        {
            throw new IOException("The SSH agent refused to list its keys.");
        }
        var count = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(1));
        var offset = 5;
        var result = new List<AgentIdentity>();
        for (var i = 0; i < count; i++)
        {
            var blob = ReadBytes(reply, offset, out offset);
            var comment = ReadString(reply, offset, out offset);
            result.Add(new AgentIdentity(blob, comment));
        }
        return result;
    }

    /// <summary>The agent's signature of <paramref name="data"/> (the SSH signature blob: algorithm + signature).</summary>
    public byte[] Sign(byte[] keyBlob, byte[] data, uint flags)
    {
        using var request = new MemoryStream();
        request.WriteByte(SignRequest);
        WriteBytes(request, keyBlob);
        WriteBytes(request, data);
        Span<byte> flagBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(flagBytes, flags);
        request.Write(flagBytes);
        var reply = _transport(request.ToArray());
        if (reply.Length == 0 || reply[0] != SignResponse)
        {
            throw new IOException(reply.Length > 0 && reply[0] == Failure ? "The SSH agent refused to sign (the key may need confirming)." : "The SSH agent did not sign.");
        }
        return ReadBytes(reply, 1, out _);
    }

    // ---------------------------------------------------------------- wire format

    internal static byte[] ReadBytes(byte[] buffer, int offset, out int next)
    {
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset));
        next = offset + 4 + length;
        return buffer[(offset + 4)..next];
    }

    internal static string ReadString(byte[] buffer, int offset, out int next) => Encoding.UTF8.GetString(ReadBytes(buffer, offset, out next));

    internal static void WriteBytes(Stream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
        stream.Write(length);
        stream.Write(value);
    }

    /// <summary>One framed request (uint32 length + message) over a stream and its framed reply.</summary>
    internal static byte[] Exchange(Stream stream, byte[] message)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)message.Length);
        stream.Write(length);
        stream.Write(message);
        stream.Flush();
        stream.ReadExactly(length);
        var size = BinaryPrimitives.ReadUInt32BigEndian(length);
        if (size > 256 * 1024)
        {
            throw new IOException("The SSH agent sent a reply too long.");
        }
        var reply = new byte[size];
        stream.ReadExactly(reply);
        return reply;
    }

    private static byte[] OverPipe(string name, byte[] message)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        pipe.Connect(3000);
        return Exchange(pipe, message);
    }

    private static byte[] OverSocket(string path, byte[] message)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(path));
        using var stream = new NetworkStream(socket);
        return Exchange(stream, message);
    }

    // ---------------------------------------------------------------- SSH.NET

    /// <summary>The agent's keys as login keys of SSH.NET (an RSA key is offered with SHA-512, SHA-256, then SHA-1).</summary>
    public IPrivateKeySource KeySource() => new AgentKeySource(this);

    private sealed class AgentKeySource(SshAgent agent) : IPrivateKeySource
    {
        private IReadOnlyCollection<HostAlgorithm>? _algorithms;

        public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms => _algorithms ??= Load();

        private List<HostAlgorithm> Load()
        {
            var result = new List<HostAlgorithm>();
            foreach (var identity in agent.Identities())
            {
                if (identity.KeyType == "ssh-rsa")
                {
                    result.Add(new AgentHostAlgorithm("rsa-sha2-512", identity.Blob, agent, RsaSha512));
                    result.Add(new AgentHostAlgorithm("rsa-sha2-256", identity.Blob, agent, RsaSha256));
                }
                result.Add(new AgentHostAlgorithm(identity.KeyType, identity.Blob, agent, 0));
            }
            return result;
        }
    }

    /// <summary>A login key whose signature the agent makes.</summary>
    private sealed class AgentHostAlgorithm(string name, byte[] blob, SshAgent agent, uint flags) : HostAlgorithm(name)
    {
        public override byte[] Data => blob;

        public override byte[] Sign(byte[] data) => agent.Sign(blob, data, flags);

        public override bool VerifySignature(byte[] data, byte[] signature) => throw new NotSupportedException("An agent key only signs.");
    }

    // ---------------------------------------------------------------- Pageant

    /// <summary>
    /// PuTTY's Pageant: a request is put in a named shared memory block and the window is asked to answer through
    /// WM_COPYDATA; the reply comes back in the same block.
    /// </summary>
    private static partial class Pageant
    {
        private const int WmCopyData = 0x004A;
        private const uint AgentCopyDataId = 0x804e50ba;
        private const int MaxMessage = 8192;

        public static bool IsRunning => OperatingSystem.IsWindows() && FindWindowW("Pageant", "Pageant") != IntPtr.Zero;

        public static byte[] Query(byte[] message)
        {
            var window = FindWindowW("Pageant", "Pageant");
            if (window == IntPtr.Zero)
            {
                throw new IOException("Pageant is not running.");
            }
            var name = "PageantRequest" + Environment.CurrentManagedThreadId.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)
                + Guid.NewGuid().ToString("N")[..8];
            var mapping = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, 0x04 /* PAGE_READWRITE */, 0, MaxMessage, name);
            if (mapping == IntPtr.Zero)
            {
                throw new IOException("Pageant: the shared memory could not be made.");
            }
            try
            {
                var view = MapViewOfFile(mapping, 0x0002 | 0x0004 /* write | read */, 0, 0, MaxMessage);
                if (view == IntPtr.Zero)
                {
                    throw new IOException("Pageant: the shared memory could not be mapped.");
                }
                try
                {
                    var framed = new byte[4 + message.Length];
                    BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)message.Length);
                    message.CopyTo(framed, 4);
                    Marshal.Copy(framed, 0, view, framed.Length);
                    var nameBytes = Encoding.ASCII.GetBytes(name + "\0");
                    var nameMemory = Marshal.AllocHGlobal(nameBytes.Length);
                    try
                    {
                        Marshal.Copy(nameBytes, 0, nameMemory, nameBytes.Length);
                        var data = new CopyDataStruct { DwData = new IntPtr(unchecked((int)AgentCopyDataId)), CbData = nameBytes.Length, LpData = nameMemory };
                        if (SendMessageW(window, WmCopyData, IntPtr.Zero, ref data) == IntPtr.Zero)
                        {
                            throw new IOException("Pageant did not answer.");
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(nameMemory);
                    }
                    var header = new byte[4];
                    Marshal.Copy(view, header, 0, 4);
                    var size = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
                    if (size is < 0 or > MaxMessage - 4)
                    {
                        throw new IOException("Pageant sent a reply too long.");
                    }
                    var reply = new byte[size];
                    Marshal.Copy(view + 4, reply, 0, size);
                    return reply;
                }
                finally
                {
                    UnmapViewOfFile(view);
                }
            }
            finally
            {
                CloseHandle(mapping);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CopyDataStruct
        {
            public IntPtr DwData;
            public int CbData;
            public IntPtr LpData;
        }

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr FindWindowW(string className, string windowName);

        [LibraryImport("user32.dll")]
        private static partial IntPtr SendMessageW(IntPtr window, int message, IntPtr wParam, ref CopyDataStruct lParam);

        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial IntPtr CreateFileMappingW(IntPtr file, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UnmapViewOfFile(IntPtr view);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr handle);
    }
}
