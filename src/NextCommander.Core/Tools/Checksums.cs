using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Tools;

public enum ChecksumKind
{
    Crc32,
    Md5,
    Sha1,
    Sha256,
    Sha384,
    Sha512,
    Sha3256,
    Sha3512,
    Blake3,
}

/// <summary>One line of a checksum file.</summary>
public sealed record ChecksumLine(string Name, string Hash);

/// <summary>Checksum algorithms and the md5sum / sha256sum / BSD / SFV file formats.</summary>
public static partial class Checksums
{
    public static IReadOnlyList<ChecksumKind> Available { get; } = Enum.GetValues<ChecksumKind>()
        .Where(k => k switch
        {
            ChecksumKind.Sha3256 => SHA3_256.IsSupported,
            ChecksumKind.Sha3512 => SHA3_512.IsSupported,
            _ => true,
        }).ToList();

    public static string Extension(ChecksumKind kind) => kind switch
    {
        ChecksumKind.Crc32 => ".sfv",
        ChecksumKind.Md5 => ".md5",
        ChecksumKind.Sha1 => ".sha",
        ChecksumKind.Sha256 => ".sha256",
        ChecksumKind.Sha384 => ".sha384",
        ChecksumKind.Sha512 => ".sha512",
        ChecksumKind.Sha3256 => ".sha3-256",
        ChecksumKind.Blake3 => ".blake3",
        _ => ".sha3-512",
    };

    public static string DisplayName(ChecksumKind kind) => kind switch
    {
        ChecksumKind.Crc32 => "CRC32 (SFV)",
        ChecksumKind.Md5 => "MD5",
        ChecksumKind.Sha1 => "SHA1",
        ChecksumKind.Sha256 => "SHA-256",
        ChecksumKind.Sha384 => "SHA-384",
        ChecksumKind.Sha512 => "SHA-512",
        ChecksumKind.Sha3256 => "SHA3-256",
        ChecksumKind.Blake3 => "BLAKE3",
        _ => "SHA3-512",
    };

    /// <summary>Algorithm of a checksum file by its extension (".sha1" too), or null.</summary>
    public static ChecksumKind? FromFileName(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".sfv" or ".crc32" => ChecksumKind.Crc32,
            ".md5" => ChecksumKind.Md5,
            ".sha" or ".sha1" => ChecksumKind.Sha1,
            ".sha256" => ChecksumKind.Sha256,
            ".sha384" => ChecksumKind.Sha384,
            ".sha512" => ChecksumKind.Sha512,
            ".sha3-256" => ChecksumKind.Sha3256,
            ".sha3-512" => ChecksumKind.Sha3512,
            ".blake3" or ".b3" => ChecksumKind.Blake3,
            _ => null,
        };
    }

    /// <summary>Hex digest length of an algorithm (used to recognise algorithms from content).</summary>
    public static int HexLength(ChecksumKind kind) => kind switch
    {
        ChecksumKind.Crc32 => 8,
        ChecksumKind.Md5 => 32,
        ChecksumKind.Sha1 => 40,
        ChecksumKind.Sha256 or ChecksumKind.Sha3256 or ChecksumKind.Blake3 => 64,
        ChecksumKind.Sha384 => 96,
        _ => 128,
    };

    public static async Task<string> ComputeAsync(Stream stream, ChecksumKind kind, Action<int>? progress, CancellationToken cancellationToken)
    {
        using var hasher = Hasher.Create(kind);
        var buffer = new byte[1024 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hasher.Append(buffer.AsSpan(0, n));
            progress?.Invoke(n);
        }
        return hasher.HexDigest();
    }

    /// <summary>Line in the file: SFV "name CRC", others md5sum style "hash *name" (binary mode).</summary>
    public static string FormatLine(ChecksumKind kind, string hash, string name) =>
        kind == ChecksumKind.Crc32 ? $"{name} {hash.ToUpperInvariant()}" : $"{hash.ToLowerInvariant()} *{name}";

    /// <summary>Reads md5sum ("hash  name" / "hash *name"), BSD ("SHA256 (name) = hash") and SFV ("name CRC") lines.</summary>
    public static IReadOnlyList<ChecksumLine> Parse(string text, ChecksumKind kind)
    {
        var result = new List<ChecksumLine>();
        var hexLength = HexLength(kind);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim('﻿');
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }
            var bsd = BsdLine().Match(line);
            if (bsd.Success)
            {
                result.Add(new ChecksumLine(bsd.Groups["name"].Value, bsd.Groups["hash"].Value));
                continue;
            }
            if (kind == ChecksumKind.Crc32)
            {
                var space = line.LastIndexOf(' ');
                if (space > 0 && line.Length - space - 1 == 8)
                {
                    result.Add(new ChecksumLine(line[..space].TrimEnd(), line[(space + 1)..]));
                }
                continue;
            }
            if (line.Length > hexLength + 1 && line[..hexLength].All(Uri.IsHexDigit) && line[hexLength] is ' ' or '\t')
            {
                var name = line[(hexLength + 1)..];
                if (name.StartsWith('*') || name.StartsWith(' '))
                {
                    name = name[1..];
                }
                result.Add(new ChecksumLine(name, line[..hexLength]));
            }
        }
        return result;
    }

    [GeneratedRegex(@"^\s*[A-Za-z0-9-]+\s*\((?<name>.+)\)\s*=\s*(?<hash>[0-9A-Fa-f]+)\s*$")]
    private static partial Regex BsdLine();

    /// <summary>Incremental hash for every supported algorithm.</summary>
    public abstract class Hasher : IDisposable
    {
        public static Hasher Create(ChecksumKind kind) => kind switch
        {
            ChecksumKind.Crc32 => new Crc32Hasher(),
            ChecksumKind.Md5 => new Incremental(IncrementalHash.CreateHash(HashAlgorithmName.MD5)),
            ChecksumKind.Sha1 => new Incremental(IncrementalHash.CreateHash(HashAlgorithmName.SHA1)),
            ChecksumKind.Sha256 => new Incremental(IncrementalHash.CreateHash(HashAlgorithmName.SHA256)),
            ChecksumKind.Sha384 => new Incremental(IncrementalHash.CreateHash(HashAlgorithmName.SHA384)),
            ChecksumKind.Sha512 => new Incremental(IncrementalHash.CreateHash(HashAlgorithmName.SHA512)),
            ChecksumKind.Sha3256 => new Incremental(IncrementalHash.CreateHash(HashAlgorithmName.SHA3_256)),
            ChecksumKind.Blake3 => new Blake3Hasher(),
            _ => new Incremental(IncrementalHash.CreateHash(HashAlgorithmName.SHA3_512)),
        };

        public abstract void Append(ReadOnlySpan<byte> data);

        public abstract string HexDigest();

        public virtual void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }

    private sealed class Incremental(IncrementalHash hash) : Hasher
    {
        public override void Append(ReadOnlySpan<byte> data) => hash.AppendData(data);

        public override string HexDigest() => Convert.ToHexStringLower(hash.GetHashAndReset());

        public override void Dispose()
        {
            hash.Dispose();
            base.Dispose();
        }
    }

    /// <summary>BLAKE3 (256 bit; fast, multithreaded for large buffers).</summary>
    public sealed class Blake3Hasher : Hasher
    {
        private global::Blake3.Hasher _hasher = global::Blake3.Hasher.New();

        public override void Append(ReadOnlySpan<byte> data)
        {
            if (data.Length >= 128 * 1024)
            {
                _hasher.UpdateWithJoin(data);
            }
            else
            {
                _hasher.Update(data);
            }
        }

        public override string HexDigest() => Convert.ToHexStringLower(Digest());

        /// <summary>Raw 32-byte digest.</summary>
        public byte[] Digest()
        {
            var hash = _hasher.Finalize();
            return hash.AsSpan().ToArray();
        }

        public override void Dispose()
        {
            _hasher.Dispose();
            base.Dispose();
        }
    }

    /// <summary>CRC-32 (IEEE 802.3, as in zip and SFV).</summary>
    public sealed class Crc32Hasher : Hasher
    {
        private static readonly uint[] s_table = BuildTable();
        private uint _crc = 0xFFFFFFFF;

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                }
                table[i] = c;
            }
            return table;
        }

        public override void Append(ReadOnlySpan<byte> data)
        {
            var crc = _crc;
            foreach (var b in data)
            {
                crc = s_table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }
            _crc = crc;
        }

        public uint Value => _crc ^ 0xFFFFFFFF;

        public override string HexDigest() => Value.ToString("X8", CultureInfo.InvariantCulture);
    }
}

/// <summary>Creates checksum files: one for all files (names relative to its folder) or one next to every file.</summary>
public sealed class CreateChecksumsJob : FileJob
{
    private readonly ProviderRegistry _registry;
    private readonly IReadOnlyList<VfsEntry> _sources;
    private readonly ChecksumKind _kind;
    private readonly VfsPath? _singleFile;

    /// <param name="singleFile">Target of the combined file, or null for one checksum file per source file.</param>
    public CreateChecksumsJob(ProviderRegistry registry, IReadOnlyList<VfsEntry> sources, ChecksumKind kind, VfsPath? singleFile)
        : base("Create checksums")
    {
        _registry = registry;
        _sources = sources;
        _kind = kind;
        _singleFile = singleFile;
    }

    public List<(VfsEntry File, string Hash)> Results { get; } = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var files = await BranchLister.ListAsync(_registry, _sources, cancellationToken: ct).ConfigureAwait(false);
        Progress.SetTotals(files.Sum(f => f.Size), files.Count);
        foreach (var file in files)
        {
            await CheckpointAsync(ct).ConfigureAwait(false);
            Progress.BeginItem(file.Path.ToDisplayString(), file.Size);
            string? hash = null;
            var ok = await WithErrorHandlingAsync(file.Path.ToDisplayString(), async () =>
            {
                await using var stream = await _registry.Resolve(file.Path).OpenReadAsync(file.Path, ct).ConfigureAwait(false);
                hash = await Checksums.ComputeAsync(stream, _kind, n => Progress.AddBytes(n), ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            if (ok && hash is not null)
            {
                Results.Add((file, hash));
                Progress.CompleteFile();
                if (_singleFile is null)
                {
                    var target = file.Path.Parent!.Combine(file.Name + Checksums.Extension(_kind));
                    await WriteAsync(target, Checksums.FormatLine(_kind, hash, file.Name) + "\n", ct).ConfigureAwait(false);
                }
            }
        }
        if (_singleFile is not null)
        {
            var baseDir = _singleFile.Parent!;
            var sb = new StringBuilder();
            foreach (var (file, hash) in Results)
            {
                var name = file.Path.GetRelativeTo(baseDir) ?? file.Name;
                sb.Append(Checksums.FormatLine(_kind, hash, _kind == ChecksumKind.Crc32 || OperatingSystem.IsWindows() ? name.Replace('/', '\\') : name)).Append('\n');
            }
            await WriteAsync(_singleFile, sb.ToString(), ct).ConfigureAwait(false);
        }
    }

    private Task WriteAsync(VfsPath target, string text, CancellationToken ct) =>
        WithErrorHandlingAsync(target.ToDisplayString(), async () =>
        {
            await using var stream = await _registry.Resolve(target).OpenWriteAsync(target, overwrite: true, ct).ConfigureAwait(false);
            await stream.WriteAsync(new UTF8Encoding(false).GetBytes(text), ct).ConfigureAwait(false);
        }, ct);
}

public enum ChecksumStatus
{
    Ok,
    Mismatch,
    Missing,
    ReadError,
}

public sealed record ChecksumResult(string Name, string Expected, string? Actual, ChecksumStatus Status);

/// <summary>Verifies the files listed in checksum files (names relative to the checksum file).</summary>
public sealed class VerifyChecksumsJob : FileJob
{
    private readonly ProviderRegistry _registry;
    private readonly IReadOnlyList<VfsEntry> _checksumFiles;

    public VerifyChecksumsJob(ProviderRegistry registry, IReadOnlyList<VfsEntry> checksumFiles)
        : base("Verify checksums")
    {
        _registry = registry;
        _checksumFiles = checksumFiles;
    }

    public List<ChecksumResult> Results { get; } = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var work = new List<(ChecksumKind Kind, VfsPath? Path, ChecksumLine Line)>();
        foreach (var file in _checksumFiles)
        {
            var kind = Checksums.FromFileName(file.Name);
            if (kind is null)
            {
                AddError(file.Name, "Unrecognized checksum file (extension .md5, .sha, .sha256, .sfv ...).");
                continue;
            }
            string text;
            await using (var stream = await _registry.Resolve(file.Path).OpenReadAsync(file.Path, ct).ConfigureAwait(false))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            foreach (var line in Checksums.Parse(text, kind.Value))
            {
                var path = file.Path.Parent;
                foreach (var part in line.Name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    path = path?.Combine(part);
                }
                work.Add((kind.Value, path, line));
            }
        }
        var entries = new List<(ChecksumKind, VfsEntry?, ChecksumLine)>();
        foreach (var (kind, path, line) in work)
        {
            var entry = path is null ? null : await _registry.Resolve(path).GetEntryAsync(path, ct).ConfigureAwait(false);
            entries.Add((kind, entry, line));
        }
        Progress.SetTotals(entries.Sum(e => e.Item2?.Size ?? 0), entries.Count);
        foreach (var (kind, entry, line) in entries)
        {
            await CheckpointAsync(ct).ConfigureAwait(false);
            if (entry is null || entry.IsDirectory)
            {
                Results.Add(new ChecksumResult(line.Name, line.Hash, null, ChecksumStatus.Missing));
                continue;
            }
            Progress.BeginItem(entry.Path.ToDisplayString(), entry.Size);
            try
            {
                await using var stream = await _registry.Resolve(entry.Path).OpenReadAsync(entry.Path, ct).ConfigureAwait(false);
                var actual = await Checksums.ComputeAsync(stream, kind, n => Progress.AddBytes(n), ct).ConfigureAwait(false);
                var ok = string.Equals(actual, line.Hash, StringComparison.OrdinalIgnoreCase);
                Results.Add(new ChecksumResult(line.Name, line.Hash, actual, ok ? ChecksumStatus.Ok : ChecksumStatus.Mismatch));
                Progress.CompleteFile();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Results.Add(new ChecksumResult(line.Name, line.Hash, null, ChecksumStatus.ReadError));
                Progress.SkipFile(entry.Size);
            }
        }
    }
}
