using NextCommander.Core.Tags;
using NextCommander.Core.Vfs;

namespace NextCommander.Core.Tests;

/// <summary>Tags of files (user's request, 23 November 2026).</summary>
public sealed class TagStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private VfsPath P(params string[] parts) => _dir.VPath(parts);

    [Fact]
    public void A_tag_goes_on_and_comes_off_a_selection_and_is_kept_in_the_file()
    {
        var file = Path.Combine(_dir.Root, "tags.json");
        var store = new TagStore(file);
        Assert.True(store.IsEmpty);
        Assert.True(store.Toggle([P("a.txt"), P("b.txt")], "Red"));
        Assert.Equal(["Red"], store.TagsOf(P("a.txt")));
        // one has it, one not: on for both; both have it: off for both
        store.Toggle([P("a.txt")], "Blue");
        Assert.True(store.Toggle([P("a.txt"), P("b.txt")], "blue"));
        Assert.Equal(["Red", "Blue"], store.TagsOf(P("a.txt")));
        Assert.False(store.Toggle([P("a.txt"), P("b.txt")], "Blue"));
        Assert.Equal(["Red"], store.TagsOf(P("b.txt")));

        var again = new TagStore(file);
        Assert.Equal(["Red"], again.TagsOf(P("a.txt")));
        again.Clear([P("a.txt"), P("b.txt")]);
        Assert.True(again.IsEmpty);
        Assert.True(new TagStore(file).IsEmpty);
        Assert.True(new TagStore(Path.Combine(_dir.Root, "none.json")).IsEmpty);
        File.WriteAllText(file, "{ not json");
        Assert.True(new TagStore(file).IsEmpty); // a damaged file: no tags, no failure
    }

    [Fact]
    public void Tags_follow_moves_and_copies_with_the_contents_of_folders()
    {
        var store = new TagStore(null);
        store.Toggle([P("docs"), P("docs", "x.pdf"), P("docs", "sub", "y.pdf"), P("docs2", "z.pdf")], "Work");
        store.Moved(P("docs"), P("archive", "docs"));
        Assert.Empty(store.TagsOf(P("docs", "x.pdf")));
        Assert.Equal(["Work"], store.TagsOf(P("archive", "docs")));
        Assert.Equal(["Work"], store.TagsOf(P("archive", "docs", "sub", "y.pdf")));
        Assert.Equal(["Work"], store.TagsOf(P("docs2", "z.pdf"))); // only the folder itself, not one with a longer name

        store.Copied(P("archive", "docs", "x.pdf"), P("copy.pdf"));
        Assert.Equal(["Work"], store.TagsOf(P("copy.pdf")));
        Assert.Equal(["Work"], store.TagsOf(P("archive", "docs", "x.pdf")));

        store.RenameTag("work", "Job");
        Assert.Equal(["Job"], store.TagsOf(P("copy.pdf")));
        Assert.Equal(5, store.PathsWith("Job").Count); // the moved folder with its two files, z.pdf and the copy
    }

    [Fact]
    public void Tags_of_remote_places_are_kept_by_their_address()
    {
        var store = new TagStore(null);
        var remote = VfsPath.Parse("sftp://server/home/me/report.pdf");
        store.Toggle([remote], "Green");
        Assert.Equal(["Green"], store.TagsOf(VfsPath.Parse("sftp://server/home/me/report.pdf")));
        Assert.Empty(store.TagsOf(VfsPath.Parse("sftp://other/home/me/report.pdf")));
    }
}
