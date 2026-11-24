using NextCommander.Core.Automation;
using NextCommander.Core.Operations;
using NextCommander.Core.Vfs;
using NextCommander.Vfs.Local;

namespace NextCommander.Core.Tests;

/// <summary>Rules: arriving files moved or copied, folders synchronized every day (user's request, 23 November 2026).</summary>
public sealed class FileRuleTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ProviderRegistry _registry = new([new LocalFileSystemProvider()]);

    public void Dispose() => _dir.Dispose();

    private RuleRunner Runner(RuleLog log) => new(_registry, job => job.RunAsync(), log, scheduleTimer: false)
    {
        SettleTime = TimeSpan.FromMilliseconds(100),
    };

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_dir.Root, name)).FullName;

    [Fact]
    public void A_synchronization_is_due_once_its_time_of_day_has_passed()
    {
        var rule = new FileRule { Kind = FileRuleKind.Synchronize, At = "02:30", Folder = "a", Target = "b" };
        var created = new DateTime(2026, 11, 23, 12, 0, 0);
        Assert.False(rule.IsDue(new DateTime(2026, 11, 23, 23, 0, 0), created)); // a new rule waits for its time
        Assert.True(rule.IsDue(new DateTime(2026, 11, 24, 2, 31, 0), created));
        rule.LastRun = new DateTime(2026, 11, 24, 2, 31, 0);
        Assert.False(rule.IsDue(new DateTime(2026, 11, 24, 9, 0, 0), created));
        // the computer was off at night: the next start makes up for it
        Assert.True(rule.IsDue(new DateTime(2026, 11, 26, 9, 0, 0), created));
        rule.Enabled = false;
        Assert.False(rule.IsDue(new DateTime(2026, 11, 26, 9, 0, 0), created));

        Assert.Equal(new TimeSpan(2, 30, 0), rule.TimeOfDay);
        Assert.Equal(new TimeSpan(7, 5, 0), new FileRule { At = "7:05" }.TimeOfDay);
        Assert.Null(new FileRule { At = "25:00" }.TimeOfDay);
        Assert.Null(new FileRule { At = "night" }.TimeOfDay);
    }

    [Fact]
    public void Problems_of_a_rule()
    {
        Assert.Null(new FileRule { Folder = _dir.Root, Target = "x", Mask = "*.pdf" }.Problem());
        Assert.NotNull(new FileRule { Folder = "", Target = "x" }.Problem());
        Assert.NotNull(new FileRule { Folder = _dir.Root, Target = " " }.Problem());
        Assert.NotNull(new FileRule { Folder = _dir.Root, Target = "x", Mask = "" }.Problem());
        Assert.NotNull(new FileRule { Folder = "sftp://server/in", Target = "x" }.Problem()); // only local folders are watched
        Assert.Null(new FileRule { Kind = FileRuleKind.Synchronize, Folder = _dir.Root, Target = "sftp://server/backup", At = "3:00" }.Problem());
        Assert.NotNull(new FileRule { Kind = FileRuleKind.Synchronize, Folder = _dir.Root, Target = "x", At = "soon" }.Problem());
    }

    [Fact]
    public async Task An_arriving_file_is_moved_once_it_is_written_and_a_name_in_use_is_kept()
    {
        var downloads = Folder("Downloads");
        var pdfs = Path.Combine(_dir.Root, "Documents", "PDF"); // made when needed
        var log = new RuleLog(Path.Combine(_dir.Root, "logs", "rules.log"));
        using var runner = Runner(log);
        var rule = new FileRule { Name = "PDFs", Folder = downloads, Mask = "*.pdf", Target = pdfs };
        runner.Apply([rule]);

        await File.WriteAllTextAsync(Path.Combine(downloads, "note.txt"), "not for this rule");
        await File.WriteAllTextAsync(Path.Combine(downloads, "bill.pdf.part"), "%PDF-1.4");
        File.Move(Path.Combine(downloads, "bill.pdf.part"), Path.Combine(downloads, "bill.pdf")); // a browser's download ends
        await WaitForAsync(() => File.Exists(Path.Combine(pdfs, "bill.pdf")));
        Assert.False(File.Exists(Path.Combine(downloads, "bill.pdf")));
        Assert.True(File.Exists(Path.Combine(downloads, "note.txt")));

        // a second bill.pdf: the one there stays, the new one gets a new name
        await File.WriteAllTextAsync(Path.Combine(downloads, "bill.pdf"), "%PDF-1.7 another");
        await WaitForAsync(() => Directory.GetFiles(pdfs).Length == 2);
        Assert.Equal("%PDF-1.4", await File.ReadAllTextAsync(Path.Combine(pdfs, "bill.pdf")));
        await WaitForAsync(() => log.Read().Count(l => l.Contains("Moved", StringComparison.Ordinal)) == 2);
        Assert.All(log.Read(), line => Assert.Contains("[PDFs]", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_still_written_waits()
    {
        var inbox = Folder("in");
        var outbox = Folder("out");
        using var runner = Runner(new RuleLog(Path.Combine(_dir.Root, "rules.log")));
        runner.Apply([new FileRule { Name = "copy", Kind = FileRuleKind.CopyArriving, Folder = inbox, Mask = "*", Target = outbox }]);
        var path = Path.Combine(inbox, "big.bin");
        await using (var writing = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            await writing.WriteAsync(new byte[1000]);
            await writing.FlushAsync();
            await Task.Delay(600);
            Assert.False(File.Exists(Path.Combine(outbox, "big.bin"))); // in use: not taken
            await writing.WriteAsync(new byte[1000]);
        }
        await WaitForAsync(() => File.Exists(Path.Combine(outbox, "big.bin")) && new FileInfo(Path.Combine(outbox, "big.bin")).Length == 2000);
        await Task.Delay(300);
        Assert.Equal(2000, new FileInfo(Path.Combine(outbox, "big.bin")).Length); // all of it, taken once
        Assert.True(File.Exists(path)); // copied, not moved
    }

    [Fact]
    public async Task Carrying_out_now_takes_the_files_already_there_and_synchronizes()
    {
        var left = Folder("left");
        var right = Folder("right");
        _dir.Write("left/a.txt", "a");
        _dir.Write("left/sub/b.txt", "b");
        _dir.Write("right/only-right.txt", "r");
        var log = new RuleLog(Path.Combine(_dir.Root, "rules.log"));
        using var runner = Runner(log);
        var sync = new FileRule { Name = "backup", Kind = FileRuleKind.Synchronize, Folder = left, Target = right, At = "02:00" };
        var ran = 0;
        runner.RuleRan += (_, _) => ran++;
        runner.Apply([sync]);
        await runner.CheckSchedulesAsync(DateTime.Now.Date.AddDays(1).AddHours(3)); // after 02:00 tomorrow: due
        Assert.Equal("a", _dir.Read("right/a.txt"));
        Assert.Equal("b", _dir.Read("right/sub/b.txt"));
        Assert.True(File.Exists(Path.Combine(right, "only-right.txt"))); // one way: nothing deleted, nothing copied back
        Assert.False(File.Exists(Path.Combine(left, "only-right.txt")));
        Assert.Equal(1, ran);
        Assert.NotNull(sync.LastRun);
        Assert.Contains(log.Read(), l => l.Contains("2 files copied to the right", StringComparison.Ordinal));

        await runner.RunAsync(sync); // nothing new
        Assert.Contains(log.Read(), l => l.Contains("are alike", StringComparison.Ordinal));

        var moved = Folder("moved");
        var take = new FileRule { Name = "take", Folder = left, Mask = "*.txt", Target = moved };
        await runner.RunAsync(take);
        Assert.True(File.Exists(Path.Combine(moved, "a.txt")));
        Assert.True(File.Exists(Path.Combine(left, "sub", "b.txt"))); // the folder only, not its subfolders
    }

    [Fact]
    public void The_log_keeps_its_newest_half()
    {
        var log = new RuleLog(Path.Combine(_dir.Root, "rules.log")) { MaxBytes = 2000 };
        var rule = new FileRule { Name = "r" };
        for (var i = 0; i < 100; i++)
        {
            log.Write(rule, $"line {i}");
        }
        var lines = log.Read();
        Assert.True(new FileInfo(log.Path).Length <= 2000 + 100);
        Assert.EndsWith("line 99", lines[^1], StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l.EndsWith("line 0", StringComparison.Ordinal));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out");
            await Task.Delay(50);
        }
    }
}
