using System.Text;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Tools;

public enum TransferEncoding
{
    /// <summary>MIME / Base64 with mail headers (.b64).</summary>
    Mime,
    /// <summary>uuencode (.uue).</summary>
    Uue,
    /// <summary>xxencode (.xxe).</summary>
    Xxe,
}

/// <summary>Files &gt; Encode / Decode: MIME (Base64), UUE and XXE, the text encodings of e-mail and Usenet attachments.</summary>
public static class FileEncoding
{
    private const string UueAlphabet = "`!\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_";
    private const string XxeAlphabet = "+-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public static string Extension(TransferEncoding encoding) => encoding switch
    {
        TransferEncoding.Mime => ".b64",
        TransferEncoding.Uue => ".uue",
        _ => ".xxe",
    };

    public static string Encode(byte[] data, string fileName, TransferEncoding encoding)
    {
        var sb = new StringBuilder();
        if (encoding == TransferEncoding.Mime)
        {
            sb.Append("MIME-Version: 1.0\r\n")
              .Append("Content-Type: application/octet-stream; name=\"").Append(fileName).Append("\"\r\n")
              .Append("Content-Transfer-Encoding: base64\r\n")
              .Append("Content-Disposition: attachment; filename=\"").Append(fileName).Append("\"\r\n\r\n");
            var base64 = Convert.ToBase64String(data);
            for (var i = 0; i < base64.Length; i += 76)
            {
                sb.Append(base64.AsSpan(i, Math.Min(76, base64.Length - i))).Append("\r\n");
            }
            return sb.ToString();
        }
        var alphabet = encoding == TransferEncoding.Uue ? UueAlphabet : XxeAlphabet;
        sb.Append("begin 644 ").Append(fileName).Append("\r\n");
        for (var offset = 0; offset < data.Length; offset += 45)
        {
            var count = Math.Min(45, data.Length - offset);
            sb.Append(alphabet[count]);
            for (var i = 0; i < count; i += 3)
            {
                int b0 = data[offset + i];
                var b1 = i + 1 < count ? data[offset + i + 1] : 0;
                var b2 = i + 2 < count ? data[offset + i + 2] : 0;
                sb.Append(alphabet[b0 >> 2]).Append(alphabet[((b0 & 3) << 4) | (b1 >> 4)])
                  .Append(alphabet[((b1 & 15) << 2) | (b2 >> 6)]).Append(alphabet[b2 & 63]);
            }
            sb.Append("\r\n");
        }
        sb.Append(alphabet[0]).Append("\r\nend\r\n");
        return sb.ToString();
    }

    /// <summary>Decodes MIME (headers optional), UUE or XXE text; returns the stored file name (if any) and the data.</summary>
    /// <exception cref="FormatException">Not one of the supported encodings.</exception>
    public static (string? FileName, byte[] Data) Decode(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var begin = Array.FindIndex(lines, l => l.StartsWith("begin ", StringComparison.Ordinal));
        if (begin >= 0)
        {
            var parts = lines[begin].Split(' ', 3);
            var name = parts.Length == 3 ? parts[2].Trim() : null;
            var body = lines.Skip(begin + 1).TakeWhile(l => l.Trim() != "end").Where(l => l.Length > 0).ToList();
            // XXE lines start with 'h' for 45 bytes, UUE lines with 'M'.
            var xxe = body.Count > 0 && body[0][0] != 'M' && XxeAlphabet.Contains(body[0][0], StringComparison.Ordinal) && body.All(l => l.All(c => XxeAlphabet.Contains(c, StringComparison.Ordinal)));
            return (name, DecodeLines(body, xxe ? XxeAlphabet : UueAlphabet));
        }
        // MIME: skip headers up to the first empty line when there are any.
        var start = lines.Length > 0 && lines[0].Contains(':', StringComparison.Ordinal) ? Array.FindIndex(lines, l => l.Trim().Length == 0) + 1 : 0;
        string? fileName = null;
        foreach (var header in lines.Take(Math.Max(0, start)))
        {
            var key = header.IndexOf("name=", StringComparison.OrdinalIgnoreCase);
            if (key >= 0)
            {
                fileName = header[(key + 5)..].Trim().Trim(';').Trim('"');
            }
        }
        var base64 = string.Concat(lines.Skip(start).TakeWhile(l => !l.StartsWith("--", StringComparison.Ordinal)).Select(l => l.Trim()));
        try
        {
            return (fileName, Convert.FromBase64String(base64));
        }
        catch (FormatException ex)
        {
            throw new FormatException("Not a recognized encoding (MIME/Base64, UUE, XXE).", ex);
        }
    }

    private static byte[] DecodeLines(List<string> lines, string alphabet)
    {
        var output = new List<byte>();
        int Value(char c) => alphabet == UueAlphabet && c == ' ' ? 0 : alphabet.IndexOf(c, StringComparison.Ordinal) is var v and >= 0 ? v
            : throw new FormatException($"Invalid character: '{c}'");
        foreach (var line in lines)
        {
            var count = Value(line[0]);
            if (count == 0)
            {
                break;
            }
            var produced = 0;
            for (var i = 1; i + 3 < line.Length + 1 && produced < count; i += 4)
            {
                int Get(int k) => i + k < line.Length ? Value(line[i + k]) : 0;
                var c0 = Get(0);
                var c1 = Get(1);
                var c2 = Get(2);
                var c3 = Get(3);
                var bytes = new[] { (byte)((c0 << 2) | (c1 >> 4)), (byte)(((c1 & 15) << 4) | (c2 >> 2)), (byte)(((c2 & 3) << 6) | c3) };
                foreach (var b in bytes)
                {
                    if (produced++ < count)
                    {
                        output.Add(b);
                    }
                }
            }
        }
        return [.. output];
    }
}

/// <summary>Encodes or decodes files (in memory: attachments are small) next to the target folder.</summary>
public sealed class EncodeJob : FileJob
{
    private readonly ProviderRegistry _registry;
    private readonly IReadOnlyList<VfsEntry> _files;
    private readonly VfsPath _target;
    private readonly TransferEncoding? _encoding;

    /// <param name="encoding">Null decodes.</param>
    public EncodeJob(ProviderRegistry registry, IReadOnlyList<VfsEntry> files, VfsPath targetDirectory, TransferEncoding? encoding)
        : base(encoding is null ? "Decode" : "Encode")
    {
        _registry = registry;
        _files = files;
        _target = targetDirectory;
        _encoding = encoding;
    }

    public List<VfsPath> Created { get; } = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Progress.SetTotals(_files.Sum(f => f.Size), _files.Count);
        foreach (var file in _files)
        {
            await CheckpointAsync(ct).ConfigureAwait(false);
            Progress.BeginItem(file.Path.ToDisplayString(), file.Size);
            await WithErrorHandlingAsync(file.Path.ToDisplayString(), async () =>
            {
                byte[] input;
                await using (var stream = await _registry.Resolve(file.Path).OpenReadAsync(file.Path, ct).ConfigureAwait(false))
                using (var ms = new MemoryStream())
                {
                    await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
                    input = ms.ToArray();
                }
                string name;
                byte[] output;
                if (_encoding is { } encoding)
                {
                    name = file.Name + FileEncoding.Extension(encoding);
                    output = Encoding.ASCII.GetBytes(FileEncoding.Encode(input, file.Name, encoding));
                }
                else
                {
                    try
                    {
                        var (stored, data) = FileEncoding.Decode(Encoding.Latin1.GetString(input));
                        name = string.IsNullOrWhiteSpace(stored) || stored.IndexOfAny(['/', '\\']) >= 0 ? Path.GetFileNameWithoutExtension(file.Name) : stored;
                        output = data;
                    }
                    catch (FormatException ex)
                    {
                        throw new IOException(ex.Message, ex);
                    }
                }
                var target = _target.Combine(name);
                await using (var stream = await _registry.Resolve(target).OpenWriteAsync(target, overwrite: true, ct).ConfigureAwait(false))
                {
                    await stream.WriteAsync(output, ct).ConfigureAwait(false);
                }
                Created.Add(target);
            }, ct).ConfigureAwait(false);
            Progress.AddBytes(file.Size);
            Progress.CompleteFile();
        }
    }
}
