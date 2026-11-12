using System.Diagnostics;
using System.Text;
using NextCommander.Core.Operations;
using NextCommander.Core.Tools;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

/// <summary>BLAKE3 checksums, copy verification and the speed limit.</summary>
public sealed class TransferExtrasTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Theory]
    [InlineData("", "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262")]
    [InlineData("abc", "6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85")]
    public async Task Blake3_known_vectors(string input, string expected)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(input));
        Assert.Equal(expected, await Checksums.ComputeAsync(stream, ChecksumKind.Blake3, null, CancellationToken.None));
    }

    [Fact]
    public async Task Blake3_large_input_matches_chunked()
    {
        var data = new byte[3_000_000];
        new Random(7).NextBytes(data);
        using var whole = new Checksums.Blake3Hasher();
        whole.Append(data); // multithreaded path
        using var parts = new Checksums.Blake3Hasher();
        for (var i = 0; i < data.Length; i += 1000)
        {
            parts.Append(data.AsSpan(i, Math.Min(1000, data.Length - i)));
        }
        Assert.Equal(whole.HexDigest(), parts.HexDigest());
        using var stream = new MemoryStream(data);
        Assert.Equal(whole.HexDigest(), await Checksums.ComputeAsync(stream, ChecksumKind.Blake3, null, CancellationToken.None));
    }

    [Fact]
    public void Blake3_file_format()
    {
        Assert.Equal(ChecksumKind.Blake3, Checksums.FromFileName("x.blake3"));
        Assert.Equal(ChecksumKind.Blake3, Checksums.FromFileName("x.b3"));
        Assert.Contains(ChecksumKind.Blake3, Checksums.Available);
        var lines = Checksums.Parse("6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d85 *abc.txt", ChecksumKind.Blake3);
        Assert.Equal("abc.txt", Assert.Single(lines).Name);
    }

    [Fact]
    public async Task Speed_limiter_waits_for_the_configured_rate()
    {
        var limiter = new SpeedLimiter { BytesPerSecond = 1000 };
        var waited = TimeSpan.Zero;
        Task Delay(TimeSpan t, CancellationToken _)
        {
            waited = t; // the clock does not move here, so the last wait is the whole debt
            return Task.CompletedTask;
        }
        for (var i = 0; i < 4; i++)
        {
            await limiter.ThrottleAsync(500, Delay, CancellationToken.None);
        }
        // 2000 bytes at 1000 B/s: due two seconds after the start
        Assert.InRange(waited.TotalSeconds, 1.8, 2.05);

        limiter.BytesPerSecond = 0;
        waited = TimeSpan.Zero;
        await limiter.ThrottleAsync(1_000_000, Delay, CancellationToken.None);
        Assert.Equal(TimeSpan.Zero, waited);
    }

    [Fact]
    public async Task Copy_respects_speed_limit_and_verifies()
    {
        _tmp.Write("src/a.bin", new string('x', 300_000));
        Directory.CreateDirectory(Path.Combine(_tmp.Root, "dst"));
        var local = new LocalFileSystemProvider();
        var registry = new ProviderRegistry([local]);
        var entry = (await local.GetEntryAsync(_tmp.VPath("src", "a.bin"), CancellationToken.None))!;
        var job = new CopyMoveJob(registry, [entry], _tmp.VPath("dst"), false,
            new CopyOptions { Verify = true, BufferSize = 50_000, SpeedLimit = 1_000_000 });
        var sw = Stopwatch.StartNew();
        await job.RunAsync();
        Assert.Equal(JobState.Completed, job.State);
        Assert.Empty(job.Errors);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(220), $"took {sw.ElapsedMilliseconds} ms"); // 300 kB at 1 MB/s ≈ 0.3 s
        Assert.Equal(1_000_000, job.SpeedLimit.BytesPerSecond);
    }
}
