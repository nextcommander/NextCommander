using NextCommander.Plugin.Packages.Formats;

namespace NextCommander.Plugin.Packages;

internal enum PackageKind
{
    Deb,
    Rpm,
    Cab,
    Msi,
    Image,
}

/// <summary>
/// An opened package: its tree of files (folders may fill themselves when first listed) and what the Lister shows of
/// it. Disposing it closes the file and deletes what was unpacked.
/// </summary>
internal abstract class Package : IDisposable
{
    private static readonly Dictionary<string, PackageKind> s_byExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".deb"] = PackageKind.Deb,
        [".udeb"] = PackageKind.Deb,
        [".ddeb"] = PackageKind.Deb,
        [".rpm"] = PackageKind.Rpm,
        [".srpm"] = PackageKind.Rpm,
        [".cab"] = PackageKind.Cab,
        [".msi"] = PackageKind.Msi,
        [".msm"] = PackageKind.Msi,
        [".tar"] = PackageKind.Image,
    };

    protected Package(string file)
    {
        File = file;
        try
        {
            Blob = Owner.Add(FileBlob.Open(file));
        }
        catch
        {
            Owner.Dispose();
            throw;
        }
    }

    public string File { get; }

    public PackageNode Root { get; } = new(string.Empty, isDirectory: true);

    /// <summary>"Debian package", "RPM package" ... (English; translated where shown).</summary>
    public abstract string Format { get; }

    protected Owner Owner { get; } = new();

    protected FileBlob Blob { get; }

    /// <summary>The kind a file name stands for (.tar: a container image archive, only when its content says so).</summary>
    public static PackageKind? KindOf(string fileName) => s_byExtension.TryGetValue(Path.GetExtension(fileName), out var kind) ? kind : null;

    /// <summary>
    /// Opens <paramref name="file"/> as the package its name says; an <see cref="InvalidDataException"/> naming the file
    /// when it is not one.
    /// </summary>
    public static Package Open(string file)
    {
        var kind = KindOf(file) ?? throw new NotSupportedException("Not a package: " + file);
        try
        {
            Package package = kind switch
            {
                PackageKind.Deb => new DebPackage(file),
                PackageKind.Rpm => new RpmPackage(file),
                PackageKind.Cab => new CabPackage(file),
                PackageKind.Msi => new MsiPackage(file),
                _ => new ImagePackage(file),
            };
            try
            {
                package.Load();
                return package;
            }
            catch
            {
                package.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException or OverflowException or ArgumentException or IndexOutOfRangeException)
        {
            throw new InvalidDataException($"{Path.GetFileName(file)}: not a readable package ({ex.Message})", ex);
        }
    }

    /// <summary>Reads what is needed for the root folder (called once, after the file is open).</summary>
    protected abstract void Load();

    /// <summary>Writes the Lister's page.</summary>
    public abstract void Describe(Overview overview);

    public void Dispose()
    {
        Owner.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// A Debian package (.deb, .udeb): an ar archive of debian-binary, control.tar.* and data.tar.*. The tar members are
/// folders of their files (unpacked when first opened: gzip, xz, zstd, bzip2 or none); the others are files.
/// </summary>
internal sealed class DebPackage : Package
{
    private static readonly string[] s_relations = ["Pre-Depends", "Depends", "Recommends", "Suggests", "Enhances", "Breaks", "Conflicts", "Replaces", "Provides"];

    private List<ArchiveItem> _members = [];

    public DebPackage(string file)
        : base(file)
    {
    }

    protected override void Load()
    {
        _members = ArReader.Read(Blob);
        if (!_members.Exists(m => m.Name == "debian-binary"))
        {
            throw new InvalidDataException("No debian-binary member.");
        }
        foreach (var member in _members)
        {
            if (IsTar(member.Name))
            {
                var content = member.Content!;
                Root.Put(new PackageNode(member.Name, isDirectory: true)
                {
                    Time = member.Time,
                    Fill = folder => ArchiveItem.Tree(TarReader.Read(Compression.Unpacked(content, Owner)), folder),
                });
            }
            else
            {
                Root.Put(member.ToNode());
            }
        }
    }

    public override string Format => "Debian package";

    private static bool IsTar(string name) => name.StartsWith("control.tar", StringComparison.Ordinal) || name.StartsWith("data.tar", StringComparison.Ordinal);

    /// <summary>The fields of the control file ("Package" → "hello"), in their order; continuation lines joined with line breaks.</summary>
    public List<(string Name, string Value)> Control()
    {
        var fields = new List<(string, string)>();
        if (Root.Children.FirstOrDefault(c => c.Name.StartsWith("control.tar", StringComparison.Ordinal))?.Child("control") is not { Content: { } content })
        {
            return fields;
        }
        string? name = null;
        var value = new List<string>();
        foreach (var line in System.Text.Encoding.UTF8.GetString(content.ToArray()).Split('\n'))
        {
            if (line.Length > 0 && char.IsWhiteSpace(line[0]) && name is not null)
            {
                value.Add(line.Trim() == "." ? string.Empty : line.Trim());
                continue;
            }
            if (name is not null)
            {
                fields.Add((name, string.Join('\n', value)));
                name = null;
            }
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                name = line[..colon].Trim();
                value = [line[(colon + 1)..].Trim()];
            }
        }
        if (name is not null)
        {
            fields.Add((name, string.Join('\n', value)));
        }
        return fields;
    }

    public override void Describe(Overview overview)
    {
        var control = Control();
        string? Field(string name) => control.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        overview.Title(Field("Package") ?? Path.GetFileName(File), Overview.T(Format), Field("Version"), Field("Architecture"));
        var description = Field("Description");
        if (description is not null)
        {
            var lines = description.Split('\n', 2);
            overview.Paragraph(lines[0]);
            if (lines.Length > 1)
            {
                overview.Preformatted(lines[1]);
            }
        }
        overview.Section("Package");
        overview.Fields([
            ("Name", Field("Package")), ("Version", Field("Version")), ("Architecture", Field("Architecture")), ("Maintainer", Field("Maintainer")),
            ("Section", Field("Section")), ("Priority", Field("Priority")), ("Homepage", Field("Homepage")), ("Source", Field("Source")),
            ("Installed size", long.TryParse(Field("Installed-Size"), out var kb) ? Overview.Size(kb * 1024) : null),
        ]);
        var relations = s_relations.Where(r => Field(r) is not null).ToList();
        if (relations.Count > 0)
        {
            overview.Section("Dependencies");
            overview.Table(["Relation", "Packages"], relations.Select(r => (IReadOnlyList<string?>)[r, Field(r)]));
        }
        overview.Section("Members");
        overview.Table(["Name", "Size", "Compression"],
            _members.Select(m => (IReadOnlyList<string?>)[m.Name, Overview.Size(m.Size), IsTar(m.Name) ? Compression.NameOf(m.Content!.Head(6)) ?? Overview.T("None") : string.Empty]), 1);
        if (Root.Children.FirstOrDefault(c => c.Name.StartsWith("control.tar", StringComparison.Ordinal)) is { } controlFolder)
        {
            var scripts = controlFolder.Children.Where(c => !c.IsDirectory && c.Name != "control").Select(c => c.Name).ToList();
            if (scripts.Count > 0)
            {
                overview.Section("Control files");
                overview.List(scripts);
            }
        }
        overview.Status = Overview.T(Format);
    }
}

/// <summary>An rpm package: the header's tags and the payload's files (a cpio archive, gzip / xz / zstd / bzip2 compressed).</summary>
internal sealed class RpmPackage : Package
{
    private Blob _payload = null!;

    public RpmPackage(string file)
        : base(file)
    {
    }

    protected override void Load()
    {
        Header = RpmHeader.Read(Blob);
        _payload = Blob.Slice(Header.PayloadOffset, Blob.Length - Header.PayloadOffset);
        var payload = _payload;
        Root.Fill = folder =>
        {
            if (Compression.NameOf(payload.Head(6)) == "lzma")
            {
                throw new NotSupportedException("The payload of this package is compressed with LZMA, which this plugin cannot unpack.");
            }
            ArchiveItem.Tree(CpioReader.Read(Compression.Unpacked(payload, Owner)), folder);
        };
    }

    public RpmHeader Header { get; private set; } = null!;

    public override string Format => "RPM package";

    public override void Describe(Overview overview)
    {
        var h = Header;
        var epoch = h.Text(RpmHeader.Epoch);
        var version = $"{(epoch is null ? string.Empty : epoch + ":")}{h.Text(RpmHeader.Version)}-{h.Text(RpmHeader.Release)}";
        overview.Title(h.Text(RpmHeader.Name) ?? Path.GetFileName(File), Overview.T(Format), version, h.Text(RpmHeader.Arch));
        if (h.Text(RpmHeader.Summary) is { } summary)
        {
            overview.Paragraph(summary);
        }
        if (h.Text(RpmHeader.Description) is { } description)
        {
            overview.Preformatted(description);
        }
        overview.Section("Package");
        var built = h.Numbers(RpmHeader.BuildTime) is [var seconds, ..] ? ArchiveItem.UnixTime(seconds) : DateTime.MinValue;
        overview.Fields([
            ("Name", h.Text(RpmHeader.Name)), ("Version", h.Text(RpmHeader.Version)), ("Release", h.Text(RpmHeader.Release)), ("Epoch", epoch),
            ("Architecture", h.Text(RpmHeader.Arch)), ("License", h.Text(RpmHeader.License)), ("Group", h.Text(RpmHeader.Group)),
            ("Homepage", h.Text(RpmHeader.Url)), ("Vendor", h.Text(RpmHeader.Vendor)), ("Packager", h.Text(RpmHeader.Packager)),
            ("Built", built == DateTime.MinValue ? null : built.ToString("g", System.Globalization.CultureInfo.CurrentCulture)),
            ("Build host", h.Text(RpmHeader.BuildHost)), ("Source package", h.Text(RpmHeader.SourceRpm)),
            ("Installed size", h.Numbers(RpmHeader.Size) is [var size, ..] ? Overview.Size(size) : null),
            ("Compression", h.Text(RpmHeader.PayloadCompressor) ?? Compression.NameOf(_payload.Head(6))),
        ]);
        var requires = h.Relations(RpmHeader.RequireName, RpmHeader.RequireFlags, RpmHeader.RequireVersion)
            .Where(r => !r.StartsWith("rpmlib(", StringComparison.Ordinal)).Distinct().ToList();
        if (requires.Count > 0)
        {
            overview.Section("Dependencies");
            overview.List(requires);
        }
        var provides = h.Relations(RpmHeader.ProvideName, RpmHeader.ProvideFlags, RpmHeader.ProvideVersion).Distinct().ToList();
        if (provides.Count > 0)
        {
            overview.Section("Provides");
            overview.List(provides);
        }
        overview.Status = Overview.T(Format);
    }
}

/// <summary>A Microsoft Cabinet (.cab).</summary>
internal sealed class CabPackage : Package
{
    public CabPackage(string file)
        : base(file)
    {
    }

    protected override void Load()
    {
        Cabinet = Cabinet.Read(Blob);
        Cabinet.AddTo(Root, Owner);
    }

    public Cabinet Cabinet { get; private set; } = null!;

    public override string Format => "Cabinet";

    public override void Describe(Overview overview)
    {
        var cab = Cabinet;
        overview.Title(Path.GetFileName(File), Overview.T(Format), Overview.T($"Version {cab.Version >> 8}.{cab.Version & 0xFF}"),
            Overview.T($"{cab.Files.Count} files"), Overview.Size(cab.Folders.Sum(f => f.UnpackedSize)));
        if (cab.Previous is not null || cab.Next is not null)
        {
            overview.Fields([("Set", $"{cab.SetId} / {cab.IndexInSet + 1}"), ("Previous cabinet", cab.Previous), ("Next cabinet", cab.Next)]);
        }
        Folders(overview, cab);
        overview.Status = Overview.T(Format);
    }

    /// <summary>The folders of a cabinet: their compression, blocks, unpacked size and files; a note on those that cannot be unpacked.</summary>
    internal static void Folders(Overview overview, Cabinet cab)
    {
        overview.Section("Folders");
        overview.Table(["#", "Compression", "Blocks", "Unpacked", "Files"], cab.Folders.Select(f => (IReadOnlyList<string?>)[
            (f.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), f.Compression,
            f.Blocks.ToString(System.Globalization.CultureInfo.InvariantCulture), Overview.Size(f.UnpackedSize),
            cab.Files.Count(e => e.FolderIndex == f.Index).ToString(System.Globalization.CultureInfo.InvariantCulture)]), 0, 2, 3, 4);
        foreach (var refusal in cab.Folders.Select(f => f.Refusal).OfType<string>().Distinct())
        {
            overview.Paragraph(Overview.T(refusal));
        }
    }
}
