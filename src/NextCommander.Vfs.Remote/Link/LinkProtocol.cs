using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NextCommander.Core.Vfs;

namespace NextCommander.Vfs.Remote.Link;

/// <summary>
/// The wire format of PC link (two NextCommanders over a cable or the network), inside TLS:
/// <list type="bullet">
/// <item>A frame is a kind byte ('J' JSON, 'D' data), a 32-bit big-endian length and the payload.</item>
/// <item>A request is one JSON frame <c>{"op": ..., "path": ...}</c>; the answer is one JSON frame, <c>{"ok": true, ...}</c>
/// or <c>{"error": kind, "message": ...}</c>.</item>
/// <item>File contents follow as data frames, ended by an empty data frame; a JSON frame in their place is an error.</item>
/// </list>
/// After TLS the client sends <c>hello</c> (its name and the protocol version); the server answers <c>ok</c> (a known
/// computer), or first <c>{"pending": true}</c> while its user decides with the pairing code on both screens.
/// </summary>
public static class LinkProtocol
{
    public const int Version = 1;

    /// <summary>The name in the TLS handshake (the certificates are self-signed; trust comes from the pairing code).</summary>
    public const string TlsName = "nextcommander-link";

    /// <summary>The largest JSON frame (a folder of very many entries still fits).</summary>
    public const int MaxJsonFrame = 64 * 1024 * 1024;

    /// <summary>The size of the data frames of a transfer.</summary>
    public const int ChunkSize = 256 * 1024;

    public const byte JsonFrame = (byte)'J';

    public const byte DataFrame = (byte)'D';

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = false };

    public static async Task WriteJsonAsync(Stream stream, JsonObject message, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(message.ToJsonString(s_json));
        await WriteFrameAsync(stream, JsonFrame, payload, cancellationToken).ConfigureAwait(false);
    }

    public static Task WriteDataAsync(Stream stream, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        WriteFrameAsync(stream, DataFrame, data, cancellationToken);

    private static async Task WriteFrameAsync(Stream stream, byte kind, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        header[0] = kind;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The kind and length of the next frame; <see cref="EndOfStreamException"/> when the other side closed.</summary>
    public static async Task<(byte Kind, int Length)> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (header[0] is not (JsonFrame or DataFrame) || length < 0 || (header[0] == JsonFrame ? length > MaxJsonFrame : length > ChunkSize))
        {
            throw new InvalidDataException("PC link: invalid frame.");
        }
        return (header[0], length);
    }

    /// <summary>The JSON of a frame whose header was read.</summary>
    public static async Task<JsonObject> ReadJsonPayloadAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return JsonNode.Parse(payload) as JsonObject ?? throw new InvalidDataException("PC link: invalid message.");
    }

    /// <summary>The next frame, which must be JSON.</summary>
    public static async Task<JsonObject> ReadJsonAsync(Stream stream, CancellationToken cancellationToken)
    {
        var (kind, length) = await ReadHeaderAsync(stream, cancellationToken).ConfigureAwait(false);
        if (kind != JsonFrame)
        {
            throw new InvalidDataException("PC link: a message was expected.");
        }
        return await ReadJsonPayloadAsync(stream, length, cancellationToken).ConfigureAwait(false);
    }

    public static JsonObject Ok() => new() { ["ok"] = true };

    // ---------------------------------------------------------------- errors

    /// <summary>An exception as an error answer.</summary>
    public static JsonObject Error(Exception ex) => new()
    {
        ["error"] = ex switch
        {
            CrossDeviceMoveException => "crossdevice",
            FileNotFoundException or DirectoryNotFoundException => "notfound",
            UnauthorizedAccessException => "denied",
            NotSupportedException => "unsupported",
            _ => "io",
        },
        ["message"] = ex.Message,
    };

    /// <summary>The exception of an error answer, or null for an answer that is no error.</summary>
    public static Exception? ToException(JsonObject answer, string path)
    {
        if (answer["error"]?.GetValue<string>() is not { } kind)
        {
            return null;
        }
        var message = answer["message"]?.GetValue<string>() ?? kind;
        return kind switch
        {
            "crossdevice" => new CrossDeviceMoveException(message),
            "notfound" => new FileNotFoundException(message, path),
            "denied" => new UnauthorizedAccessException(message),
            "unsupported" => new NotSupportedException(message),
            _ => new IOException(message),
        };
    }

    // ---------------------------------------------------------------- entries

    /// <summary>An entry as JSON: name, folder, size, times (UTC ticks), attributes, link.</summary>
    public static JsonObject ToJson(VfsEntry entry)
    {
        var json = new JsonObject
        {
            ["n"] = entry.Name,
            ["m"] = Ticks(entry.LastWriteTime),
        };
        if (entry.IsDirectory)
        {
            json["d"] = true;
        }
        else
        {
            json["s"] = entry.Size;
        }
        if (entry.CreationTime is { } created)
        {
            json["c"] = Ticks(created);
        }
        if (entry.Attributes != EntryAttributes.None)
        {
            json["a"] = (int)entry.Attributes;
        }
        if (entry.IsLink)
        {
            json["l"] = true;
        }
        return json;
    }

    /// <summary>An entry of the JSON of <see cref="ToJson"/>, below <paramref name="folder"/> of the peer.</summary>
    public static VfsEntry FromJson(JsonObject json, VfsPath folder)
    {
        var name = json["n"]?.GetValue<string>() ?? throw new InvalidDataException("PC link: entry without a name.");
        var directory = json["d"]?.GetValue<bool>() == true;
        return new VfsEntry
        {
            Name = name,
            Path = folder.Combine(name),
            IsDirectory = directory,
            Size = directory ? 0 : json["s"]?.GetValue<long>() ?? 0,
            LastWriteTime = FromTicks(json["m"]?.GetValue<long>() ?? 0),
            CreationTime = json["c"]?.GetValue<long>() is { } created ? FromTicks(created) : null,
            Attributes = (EntryAttributes)(json["a"]?.GetValue<int>() ?? 0),
            IsLink = json["l"]?.GetValue<bool>() == true,
        };
    }

    /// <summary>A time on the wire: UTC ticks (the computers may be in different time zones).</summary>
    public static long Ticks(DateTime time) => time == DateTime.MinValue ? 0 : time.ToUniversalTime().Ticks;

    /// <summary>A time from the wire, as local time like the local file system's.</summary>
    public static DateTime FromTicks(long ticks) => ticks <= 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();

    // ---------------------------------------------------------------- trust

    /// <summary>The fingerprint of a certificate: "SHA256:" and the hex of its hash.</summary>
    public static string Fingerprint(X509Certificate certificate) =>
        "SHA256:" + Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    /// <summary>
    /// The pairing code both screens show: six digits from the fingerprints of both computers (in a fixed order). A
    /// computer in the middle has other certificates, so the codes on the two screens would differ.
    /// </summary>
    public static string PairingCode(string fingerprintA, string fingerprintB)
    {
        var (first, second) = string.CompareOrdinal(fingerprintA, fingerprintB) <= 0 ? (fingerprintA, fingerprintB) : (fingerprintB, fingerprintA);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(first + "|" + second));
        var number = BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000;
        var digits = number.ToString("D6", CultureInfo.InvariantCulture);
        return digits[..3] + " " + digits[3..];
    }

    /// <summary>A computer name usable in a path (<c>link://name/</c>): letters, digits, '-', '_' and '.'.</summary>
    public static string SafeName(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in name.Trim())
        {
            builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        }
        var safe = builder.ToString().Trim('-', '.');
        return safe.Length > 0 ? safe : "computer";
    }
}
