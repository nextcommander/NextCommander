using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NextCommander.Core.Config;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Remote;

namespace NextCommander.Core.Tests;

/// <summary>
/// The SSH agent: its protocol against an in-process agent holding a test key, and (with FM_TEST_SSH_AGENT=host:port of
/// tests/servers/sftp_server.py started with the authorized key below) a real login through the agent and through a
/// jump host.
/// </summary>
public sealed class SshAgentTests
{
    /// <summary>A test-only RSA key; its public half is <see cref="PublicKey"/> (for the test server's authorized_keys).</summary>
    public const string PrivateKey = """
        -----BEGIN RSA PRIVATE KEY-----
        MIIEpAIBAAKCAQEAzAGdFIFTsC/La/2m0PXpbd6tNewZAkqCdBFOHwrGBoYU5X7L
        HY2685EzWaiX654QjMC6MqhZR9eIR8kD38+ecYsawXChdTa0PhiqL5ASw4y/fGf7
        zM7u0zORwF9Pqviv2eRCpnhIGEY+NTXscbidxG1sfRaY4BSMuwDCPFsLZJRFF1Ld
        PMwdrjahbrn/1zHBBa+lz2dD/NaKMHpyVJmjLbYjG2683QCcjIEaqet879xlrKQL
        oKP7oXIjrpyRG2JwR8pNDLWOfgmiwEzy8fdCgejVQj/f/6kPNNLgHU2JfVetqJXc
        TeDSuoFze9iRSiAJDEghHWX9IpiXNlh8bsAzKwIDAQABAoIBABKAbvY7cxM+h8IS
        g2eIyz6ApyWFymWFIRGqZB2372pgt1JyTl+EHsQU6MWlNFSqcDEjutSkpKbkVY1q
        c08qrYxPTleO1xsh9p95wy2EcSnfq5olIBayCcIzzMXX9F9leyVVaJ9ft0IkVLik
        saiaqAxMKZOqp1C4lw7QAc2m1YCt3FAe6AY69lSK3g811CKy5SkpaBRer2koBjAf
        YM8dM0x0P1jhqU2mDmC/3Bv/G3Zuyvo94/VFmi7jw0CWKS0GTKm6+bpjwflyDXnd
        sQFB5uKbLWsBFk0UJnntAaB3RMspVQj5gnFsPEOjuwAFU1rSWXoryyHApBX5E39V
        KpP3LiECgYEA581q+a4zCKxga31hs3KfWmj54bmVfR7pwjDhasKPzpzkansZmdhI
        cBRdkXXmScK7mMBdfH1ivXtSUzMF3OwjGGMGVanCSpKRuFPguC6tBKYoL0PgVufw
        uBka0SYEauGQUaImLoymQnFCpJK372SrFVasoq6n+AlcbJM24eamtZECgYEA4U1i
        l5LkLICSbxvrFYMQmuXgrtxfbsAWL7i4tD/1e6c4q2mLuoPDeLajl5+z9mTk5lGr
        qsDtn0vI+h4QShBYUtpfSh/pKBn75zYH+uQDf5skrDd+C56ZZC8qkzAohJi6yeLd
        ZDTqx+HCm1nYQcFgQ9ebhh5pAS5u8QFBiZlaTvsCgYBUpZK28oH4XNoo5nRTDzxZ
        y5PUdey88Bj0rNzkG4BgNrLCn4lNwMlTBFzWzXL25IGU2kpJN5BjGTGK6ErvzdtT
        AHZDueYVTUihQyAAoirUU8jA2cyGb9Rbpe9BCNz8GrqU1N2Uig/8m1d2uY0pgl1n
        QWtxTM5RqTly/J9CBvRBwQKBgQDcinuwE8H3LgQpbxK3hAyFE/DUOjPQnYcaP5/O
        Ea2i1RXL9Q0qDkERsPaCPXpT/n9yVQ/Nnfz8qZqZ+0PGvCk5MTmFvabl8kl7EpDN
        LtWd26gSTF+90Aom32S1n7aJ1VdCuCgvJv2Lm5p7p37hHwG0ZyF+L9ZpqaI/mBMn
        Nk4IcQKBgQDlKT1gUuq/JI4225NjSRICMJyqMYkl7Y3/6yJbx6t7dGg5Ekw2Ji55
        T4I7gCry6u3s4mzl7fAzxyIv0bXUiEUcYZz0AGq5S2qGt4cc09MSY8flW7cFuvae
        2x79cP3DE5sKDUgkFqZmKZhN1cNxFIS8awhE15JSkV0hPRc6Y0pOfA==
        -----END RSA PRIVATE KEY-----
        """;

    public const string PublicKey = "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQDMAZ0UgVOwL8tr/abQ9elt3q017BkCSoJ0EU4fCsYGhhTlfssdjbrzkTNZqJfrnhCMwLoyqFlH14hHyQPfz55xixrBcKF1NrQ+GKovkBLDjL98Z/vMzu7TM5HAX0+q+K/Z5EKmeEgYRj41NexxuJ3EbWx9FpjgFIy7AMI8WwtklEUXUt08zB2uNqFuuf/XMcEFr6XPZ0P81oowenJUmaMttiMbbrzdAJyMgRqp63zv3GWspAugo/uhciOunJEbYnBHyk0MtY5+CaLATPLx90KB6NVCP9//qQ800uAdTYl9V62oldxN4NK6gXN72JFKIAkMSCEdZf0imJc2WHxuwDMr";

    /// <summary>An agent in this process: lists the key and signs with it as ssh-agent does.</summary>
    private sealed class FakeAgent
    {
        private readonly RSA _rsa = RSA.Create();

        public FakeAgent() => _rsa.ImportFromPem(PrivateKey);

        public List<uint> Flags { get; } = [];

        public byte[] Blob
        {
            get
            {
                var p = _rsa.ExportParameters(false);
                using var s = new MemoryStream();
                Write(s, "ssh-rsa"u8.ToArray());
                Write(s, Mpint(p.Exponent!));
                Write(s, Mpint(p.Modulus!));
                return s.ToArray();
            }
        }

        private static byte[] Mpint(byte[] value) => value[0] >= 0x80 ? [0, .. value] : value;

        private static void Write(Stream s, byte[] value)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
            s.Write(length);
            s.Write(value);
        }

        private static byte[] Read(byte[] buffer, ref int offset)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset));
            var value = buffer[(offset + 4)..(offset + 4 + length)];
            offset += 4 + length;
            return value;
        }

        public byte[] Answer(byte[] request)
        {
            using var reply = new MemoryStream();
            switch (request[0])
            {
                case 11:
                    reply.WriteByte(12);
                    reply.Write([0, 0, 0, 1]);
                    Write(reply, Blob);
                    Write(reply, "test key"u8.ToArray());
                    break;
                case 13:
                    var offset = 1;
                    Assert.Equal(Blob, Read(request, ref offset));
                    var data = Read(request, ref offset);
                    var flags = BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(offset));
                    Flags.Add(flags);
                    var (name, hash) = flags switch
                    {
                        SshAgent.RsaSha512 => ("rsa-sha2-512", HashAlgorithmName.SHA512),
                        SshAgent.RsaSha256 => ("rsa-sha2-256", HashAlgorithmName.SHA256),
                        _ => ("ssh-rsa", HashAlgorithmName.SHA1),
                    };
                    using (var signature = new MemoryStream())
                    {
                        Write(signature, Encoding.ASCII.GetBytes(name));
                        Write(signature, _rsa.SignData(data, hash, RSASignaturePadding.Pkcs1));
                        reply.WriteByte(14);
                        Write(reply, signature.ToArray());
                    }
                    break;
                default:
                    reply.WriteByte(5);
                    break;
            }
            return reply.ToArray();
        }
    }

    [Fact]
    public void Lists_keys_and_signs_through_the_protocol()
    {
        var fake = new FakeAgent();
        // the framed exchange as over a pipe or a socket
        var agent = new SshAgent(request => SshAgent.Exchange(new Duplex(SshAgentFrame(fake.Answer(request))), request), "test");
        var identity = Assert.Single(agent.Identities());
        Assert.Equal(("ssh-rsa", "test key"), (identity.KeyType, identity.Comment));
        Assert.Equal(PublicKey.Split(' ')[1], Convert.ToBase64String(identity.Blob));
        var signature = agent.Sign(identity.Blob, "data"u8.ToArray(), SshAgent.RsaSha256);
        Assert.Equal("rsa-sha2-256", SshAgent.ReadString(signature, 0, out _));
        Assert.Equal([SshAgent.RsaSha256], fake.Flags);
        Assert.Throws<IOException>(() => new SshAgent(_ => [5], "refusing").Sign(identity.Blob, [1], 0));
    }

    /// <summary>A reply with its length in front, as an agent sends it.</summary>
    private static byte[] SshAgentFrame(byte[] message)
    {
        var framed = new byte[4 + message.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)message.Length);
        message.CopyTo(framed, 4);
        return framed;
    }

    /// <summary>A stream that takes what is written and reads back a fixed reply.</summary>
    private sealed class Duplex(byte[] reply) : MemoryStream(reply)
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }

    [Fact]
    public void A_jump_host_is_read_from_its_address()
    {
        var jump = SshLogin.JumpConnectionOf(new RemoteConnection { User = "deploy", KeyFile = "k", UseSshAgent = true, JumpHost = " me@bastion.example.com:2200 " });
        Assert.Equal(("me", "bastion.example.com", 2200, "k", true), (jump.User, jump.Host, jump.EffectivePort, jump.KeyFile, jump.UseSshAgent));
        var plain = SshLogin.JumpConnectionOf(new RemoteConnection { User = "deploy", JumpHost = "bastion" });
        Assert.Equal(("deploy", 22), (plain.User, plain.EffectivePort));
    }

    /// <summary>The test server: FM_TEST_SSH_AGENT=127.0.0.1:2222 (sftp_server.py with an authorized_keys file holding <see cref="PublicKey"/>).</summary>
    [Fact]
    public async Task Logs_in_through_the_agent_and_a_jump_host()
    {
        if (Environment.GetEnvironmentVariable("FM_TEST_SSH_AGENT") is not { Length: > 0 } server)
        {
            return;
        }
        var fake = new FakeAgent();
        var previous = SshLogin.FindAgent;
        SshLogin.FindAgent = () => new SshAgent(fake.Answer, "test");
        try
        {
            var asked = new List<string>();
            var hooks = new RemoteHooks
            {
                GetPassword = (_, _) => Task.FromResult<string?>(null), // never needed: the agent logs in
                ConfirmFingerprint = (c, _, _, _) =>
                {
                    asked.Add(c.Host);
                    return Task.FromResult(true);
                },
                ResolveConnection = (scheme, authority) =>
                {
                    var c = RemoteConnection.FromAuthority(scheme, authority);
                    c.UseSshAgent = true;
                    return c;
                },
            };
            await using var sftp = new SftpFileSystemProvider(hooks);
            var entries = await sftp.ListAsync(VfsPath.Parse($"sftp://tester@{server}/"), CancellationToken.None);
            Assert.NotNull(entries);
            Assert.Contains(fake.Flags, f => f == SshAgent.RsaSha512 || f == SshAgent.RsaSha256);

            // the same server as its own jump host: the tunnel goes through it to itself
            var tunneled = new RemoteHooks
            {
                GetPassword = (_, _) => Task.FromResult<string?>(null),
                ConfirmFingerprint = (c, _, d, _) =>
                {
                    asked.Add(d);
                    return Task.FromResult(true);
                },
                ResolveConnection = (scheme, authority) =>
                {
                    var c = RemoteConnection.FromAuthority(scheme, authority);
                    c.UseSshAgent = true;
                    c.JumpHost = "tester@" + server;
                    return c;
                },
            };
            await using var through = new SftpFileSystemProvider(tunneled);
            Assert.NotNull(await through.ListAsync(VfsPath.Parse($"sftp://tester@{server}/"), CancellationToken.None));
            Assert.Contains(asked, d => d.EndsWith("(jump host)", StringComparison.Ordinal));
        }
        finally
        {
            SshLogin.FindAgent = previous;
        }
    }
}
