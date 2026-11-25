using System.Globalization;
using NextCommander.Plugin.Packages.Formats;

namespace NextCommander.Plugin.Packages;

/// <summary>
/// A Windows Installer package (.msi, .msm): the files of its embedded cabinets, at the paths they are installed to
/// (File → Component → Directory tables: "ProgramFilesFolder/App/app.exe"; a folder of the system keeps its property
/// name). A file of a cabinet that the File table does not name is shown as "&lt;cabinet&gt;/&lt;its name in the cabinet&gt;";
/// files the installer takes from outside the package are not there. The whole file is read into memory (compound
/// file reader).
/// </summary>
internal sealed class MsiPackage : Package
{
    private readonly List<(string Name, Cabinet Cabinet)> _cabinets = [];
    private MsiDatabase _database = null!;

    public MsiPackage(string file)
        : base(file)
    {
    }

    public override string Format => "Windows Installer package";

    protected override void Load()
    {
        if (Blob.Length > int.MaxValue - 64)
        {
            throw new InvalidDataException("Too big.");
        }
        _database = MsiDatabase.Open(Blob.ToArray());
        var paths = InstalledPaths();
        foreach (var name in CabinetStreams())
        {
            if (_database.Stream(name) is not { } bytes || !Cabinet.IsCabinet(bytes))
            {
                continue;
            }
            var cabinet = Cabinet.Read(new MemoryBlob(bytes));
            _cabinets.Add((name, cabinet));
            cabinet.AddTo(Root, Owner, e => paths.GetValueOrDefault(e.Name) ?? name + "/" + e.Name);
        }
    }

    /// <summary>The cabinets the Media table names ("#cab1.cab": a stream of the package); without a Media table, every stream that is one.</summary>
    private IEnumerable<string> CabinetStreams()
    {
        var named = _database.Rows("Media").Select(r => r.GetValueOrDefault("Cabinet") as string)
            .OfType<string>().Where(c => c.StartsWith('#')).Select(c => c[1..]).Distinct(StringComparer.Ordinal).ToList();
        return named.Count > 0 ? named : _database.StreamNames.Where(n => !n.StartsWith('!') && !n.StartsWith('\u0005'));
    }

    /// <summary>File key (the file's name in the cabinet) → the path it is installed to.</summary>
    private Dictionary<string, string> InstalledPaths()
    {
        var directories = _database.Rows("Directory")
            .Where(r => r.GetValueOrDefault("Directory") is string)
            .GroupBy(r => (string)r["Directory"]!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (Parent: g.First().GetValueOrDefault("Directory_Parent") as string, Default: g.First().GetValueOrDefault("DefaultDir") as string ?? "."),
                StringComparer.Ordinal);
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        string PathOf(string id, int depth)
        {
            if (known.TryGetValue(id, out var path))
            {
                return path;
            }
            if (depth > 64 || !directories.TryGetValue(id, out var directory) || directory.Parent is null || directory.Parent == id)
            {
                return known[id] = string.Empty; // a root (TARGETDIR = "SourceDir")
            }
            var parent = PathOf(directory.Parent, depth + 1);
            var name = LongName(directory.Default.Split(':')[0]);
            // a folder of the system right under the root ("ProgramFilesFolder") keeps its property name
            if (parent.Length == 0 && id.EndsWith("Folder", StringComparison.Ordinal) && id.All(char.IsAsciiLetterOrDigit))
            {
                name = id;
            }
            return known[id] = name is "." or "" ? parent : parent.Length == 0 ? name : parent + "/" + name;
        }
        var components = _database.Rows("Component").Where(r => r.GetValueOrDefault("Component") is string)
            .GroupBy(r => (string)r["Component"]!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().GetValueOrDefault("Directory_") as string, StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in _database.Rows("File"))
        {
            if (row.GetValueOrDefault("File") is not string key || row.GetValueOrDefault("FileName") is not string fileName)
            {
                continue;
            }
            var folder = row.GetValueOrDefault("Component_") is string component && components.GetValueOrDefault(component) is { } directory
                ? PathOf(directory, 0)
                : string.Empty;
            var name = LongName(fileName);
            result[key] = folder.Length == 0 ? name : folder + "/" + name;
        }
        return result;
    }

    /// <summary>"SHORT~1.TXT|Long name.txt" → the long name.</summary>
    private static string LongName(string names) => names.Contains('|', StringComparison.Ordinal) ? names[(names.IndexOf('|', StringComparison.Ordinal) + 1)..] : names;

    public override void Describe(Overview overview)
    {
        var properties = _database.Rows("Property")
            .Where(r => r.GetValueOrDefault("Property") is string)
            .GroupBy(r => (string)r["Property"]!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().GetValueOrDefault("Value") as string, StringComparer.Ordinal);
        string? Property(string name) => properties.GetValueOrDefault(name);
        overview.Title(Property("ProductName") ?? Path.GetFileName(File), Overview.T(Format), Property("ProductVersion"), Property("Manufacturer"));
        overview.Section("Product");
        overview.Fields([
            ("Name", Property("ProductName")), ("Version", Property("ProductVersion")), ("Manufacturer", Property("Manufacturer")),
            ("Product code", Property("ProductCode")), ("Upgrade code", Property("UpgradeCode")), ("Language", Property("ProductLanguage")),
            ("Files", _database.Rows("File").Count.ToString(CultureInfo.InvariantCulture)),
        ]);
        overview.Section("Cabinets");
        if (_cabinets.Count == 0)
        {
            overview.Paragraph(Overview.T("The package holds no cabinet: its files are installed from next to it."));
        }
        else
        {
            overview.Table(["Name", "Files", "Folders", "Compression", "Unpacked"], _cabinets.Select(c => (IReadOnlyList<string?>)[
                c.Name, c.Cabinet.Files.Count.ToString(CultureInfo.InvariantCulture), c.Cabinet.Folders.Count.ToString(CultureInfo.InvariantCulture),
                string.Join(", ", c.Cabinet.Folders.Select(f => f.Compression).Distinct()), Overview.Size(c.Cabinet.Folders.Sum(f => f.UnpackedSize))]), 1, 2, 4);
            foreach (var refusal in _cabinets.SelectMany(c => c.Cabinet.Folders).Select(f => f.Refusal).OfType<string>().Distinct())
            {
                overview.Paragraph(Overview.T(refusal));
            }
        }
        overview.Status = Overview.T(Format);
    }
}
