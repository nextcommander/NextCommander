using System.Globalization;
using System.Text;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Tools;

/// <summary>Splits a file into name.001, name.002, ... plus name.crc (original name, size, CRC32).</summary>
public sealed class SplitJob : FileJob
{
    private readonly ProviderRegistry _registry;
    private readonly VfsEntry _file;
    private readonly long _partSize;
    private readonly VfsPath _targetDirectory;

    public SplitJob(ProviderRegistry registry, VfsEntry file, long partSize, VfsPath targetDirectory)
        : base($"Split: {file.Name}")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partSize, 1);
        _registry = registry;
        _file = file;
        _partSize = partSize;
        _targetDirectory = targetDirectory;
    }

    public int Parts { get; private set; }

    /// <summary>"1440k", "100M", "4.7G", "650 MB" or plain bytes.</summary>
    public static long ParseSize(string text) => Search.SearchInputParser.ParseSize(text)
        ?? throw new FormatException("Enter a part size.");

    public static string PartName(string fileName, int index) => $"{fileName}.{index.ToString("D3", CultureInfo.InvariantCulture)}";

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var parts = (int)Math.Max(1, (_file.Size + _partSize - 1) / _partSize);
        Progress.SetTotals(_file.Size, parts);
        var target = _registry.Resolve(_targetDirectory);
        var crc = new Checksums.Crc32Hasher();
        await using var input = await _registry.Resolve(_file.Path).OpenReadAsync(_file.Path, ct).ConfigureAwait(false);
        var buffer = new byte[1024 * 1024];
        var index = 0;
        long remaining = _file.Size;
        for (index = 1; index <= parts; index++)
        {
            var partPath = _targetDirectory.Combine(PartName(_file.Name, index));
            var size = Math.Min(_partSize, remaining);
            Progress.BeginItem(partPath.ToDisplayString(), size);
            await using (var output = await target.OpenWriteAsync(partPath, overwrite: true, ct).ConfigureAwait(false))
            {
                long written = 0;
                while (written < size)
                {
                    await CheckpointAsync(ct).ConfigureAwait(false);
                    var n = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - written)), ct).ConfigureAwait(false);
                    if (n == 0)
                    {
                        throw new IOException("File was truncated while splitting: " + _file.Name);
                    }
                    await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    crc.Append(buffer.AsSpan(0, n));
                    written += n;
                    Progress.AddBytes(n);
                }
            }
            remaining -= size;
            Progress.CompleteFile();
        }
        index--;
        Parts = index;
        var info = $"filename={_file.Name}\r\nsize={_file.Size.ToString(CultureInfo.InvariantCulture)}\r\ncrc32={crc.HexDigest()}\r\n";
        await using var crcFile = await target.OpenWriteAsync(_targetDirectory.Combine(Path.ChangeExtension(_file.Name, null) + ".crc"), overwrite: true, ct)
            .ConfigureAwait(false);
        await crcFile.WriteAsync(Encoding.UTF8.GetBytes(info), ct).ConfigureAwait(false);
    }
}

/// <summary>Joins name.001, name.002, ... (all consecutive parts) and checks size / CRC32 against name.crc when present.</summary>
public sealed class CombineJob : FileJob
{
    private readonly ProviderRegistry _registry;
    private readonly VfsEntry _firstPart;
    private readonly VfsPath _targetDirectory;

    public CombineJob(ProviderRegistry registry, VfsEntry firstPart, VfsPath targetDirectory)
        : base($"Combine: {firstPart.Name}")
    {
        _registry = registry;
        _firstPart = firstPart;
        _targetDirectory = targetDirectory;
    }

    public VfsPath? Result { get; private set; }

    /// <summary>True when name.crc existed and matched.</summary>
    public bool Verified { get; private set; }

    public static bool IsFirstPart(string name) => name.EndsWith(".001", StringComparison.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!IsFirstPart(_firstPart.Name) || _firstPart.Path.Parent is not { } dir)
        {
            throw new InvalidOperationException("Select the first part with the .001 extension to combine.");
        }
        var baseName = _firstPart.Name[..^4];
        var provider = _registry.Resolve(dir);
        var parts = new List<VfsEntry>();
        for (var i = 1; i < 10000; i++)
        {
            var path = dir.Combine(SplitJob.PartName(baseName, i));
            if (await provider.GetEntryAsync(path, ct).ConfigureAwait(false) is not { IsDirectory: false } part)
            {
                break;
            }
            parts.Add(part);
        }

        var crcPath = dir.Combine(Path.ChangeExtension(baseName, null) + ".crc");
        string? expectedName = null;
        long? expectedSize = null;
        string? expectedCrc = null;
        if (await provider.GetEntryAsync(crcPath, ct).ConfigureAwait(false) is not null)
        {
            await using var stream = await provider.OpenReadAsync(crcPath, ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                var eq = line.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0)
                {
                    continue;
                }
                var key = line[..eq].Trim().ToLowerInvariant();
                var value = line[(eq + 1)..].Trim();
                switch (key)
                {
                    case "filename":
                        expectedName = value;
                        break;
                    case "size" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size):
                        expectedSize = size;
                        break;
                    case "crc32":
                        expectedCrc = value;
                        break;
                }
            }
        }

        Progress.SetTotals(parts.Sum(p => p.Size), parts.Count);
        var target = _targetDirectory.Combine(expectedName is { Length: > 0 } n && n.IndexOfAny(['/', '\\']) < 0 ? n : baseName);
        var crc = new Checksums.Crc32Hasher();
        long total = 0;
        var buffer = new byte[1024 * 1024];
        await using (var output = await _registry.Resolve(target).OpenWriteAsync(target, overwrite: true, ct).ConfigureAwait(false))
        {
            foreach (var part in parts)
            {
                Progress.BeginItem(part.Path.ToDisplayString(), part.Size);
                await using var input = await _registry.Resolve(part.Path).OpenReadAsync(part.Path, ct).ConfigureAwait(false);
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await CheckpointAsync(ct).ConfigureAwait(false);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    crc.Append(buffer.AsSpan(0, read));
                    total += read;
                    Progress.AddBytes(read);
                }
                Progress.CompleteFile();
            }
        }
        Result = target;
        if (expectedSize is { } size2 && size2 != total)
        {
            AddError(target.Name, $"Size mismatch: expected {size2:N0}, combined {total:N0} bytes (missing part?).");
        }
        else if (expectedCrc is not null && !string.Equals(expectedCrc, crc.HexDigest(), StringComparison.OrdinalIgnoreCase))
        {
            AddError(target.Name, $"CRC32 mismatch: expected {expectedCrc}, combined {crc.HexDigest()}.");
        }
        else
        {
            Verified = expectedCrc is not null || expectedSize is not null;
        }
    }
}
