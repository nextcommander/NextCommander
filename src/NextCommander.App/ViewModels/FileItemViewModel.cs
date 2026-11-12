using System.Globalization;
using System.Text;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using NextCommander.App.Services.Icons;
using NextCommander.Core.Config;
using NextCommander.Core.Formatting;
using NextCommander.Core.Vfs;

namespace NextCommander.App.ViewModels;

/// <summary>How sizes and dates are shown; one instance is shared by all rows of a listing.</summary>
public sealed record DisplayOptions(SizeDisplay SizeDisplay, bool DecimalUnits, string DateFormat)
{
    public static string DefaultDateFormat => CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern + " HH:mm";

    /// <summary>Formatting culture: the current one with the separators of Configuration > Data formats.</summary>
    public CultureInfo Culture { get; init; } = CultureInfo.CurrentCulture;

    public static DisplayOptions From(AppSettings s)
    {
        var culture = FormattingCulture(s);
        return new(s.SizeDisplay, s.DecimalSizeUnits, string.IsNullOrWhiteSpace(s.DateFormat) ? DateFormatFor(s) : s.DateFormat) { Culture = culture };
    }

    /// <summary>The current culture with the separators chosen in the settings.</summary>
    public static CultureInfo FormattingCulture(AppSettings s)
    {
        if (string.IsNullOrEmpty(s.DateSeparator) && string.IsNullOrEmpty(s.TimeSeparator) && string.IsNullOrEmpty(s.ThousandsSeparator)
            && string.IsNullOrEmpty(s.DecimalSeparator))
        {
            return CultureInfo.CurrentCulture;
        }
        var culture = (CultureInfo)CultureInfo.CurrentCulture.Clone();
        if (!string.IsNullOrEmpty(s.DateSeparator))
        {
            culture.DateTimeFormat.DateSeparator = s.DateSeparator;
        }
        if (!string.IsNullOrEmpty(s.TimeSeparator))
        {
            culture.DateTimeFormat.TimeSeparator = s.TimeSeparator;
        }
        if (!string.IsNullOrEmpty(s.ThousandsSeparator))
        {
            culture.NumberFormat.NumberGroupSeparator = s.ThousandsSeparator;
        }
        if (!string.IsNullOrEmpty(s.DecimalSeparator))
        {
            culture.NumberFormat.NumberDecimalSeparator = s.DecimalSeparator;
        }
        return culture;
    }

    /// <summary>Short date of the system ("/" and ":" placeholders for the separators), century, 12/24 hours.</summary>
    public static string DateFormatFor(AppSettings s)
    {
        var system = CultureInfo.CurrentCulture.DateTimeFormat;
        var date = system.ShortDatePattern.Replace(system.DateSeparator, "/", StringComparison.Ordinal);
        if (!s.ShowCentury)
        {
            date = date.Replace("yyyy", "yy", StringComparison.Ordinal);
        }
        else if (!date.Contains("yyyy", StringComparison.Ordinal))
        {
            date = date.Replace("yy", "yyyy", StringComparison.Ordinal);
        }
        return date + (s.TwelveHourClock ? " hh:mm tt" : " HH:mm");
    }

    public string FormatSize(long bytes) => SizeDisplay == SizeDisplay.Dynamic
        ? SizeFormatter.Human(bytes, DecimalUnits)
        : bytes.ToString("N0", Culture);
}

/// <summary>One row of a file panel ("..", a directory, a file or a read error).</summary>
public sealed partial class FileItemViewModel : ObservableObject
{
    private static readonly DisplayOptions s_defaultOptions = new(SizeDisplay.Bytes, false, DisplayOptions.DefaultDateFormat);

    private readonly DisplayOptions _options;

    [ObservableProperty]
    private bool _isMarked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    [NotifyPropertyChangedFor(nameof(SizeValue))]
    private long? _directorySize;

    /// <summary>Configuration > Thumbnails: the fields shown under the thumbnail (e.g. "1920×1080"), null when none.</summary>
    [ObservableProperty]
    private string? _thumbnailCaption;

    /// <summary>Texts of the custom columns (bound as Custom[i]).</summary>
    [ObservableProperty]
    private string[] _custom = [];

    /// <summary>Operating-system icon (Windows shell icon); null → built-in type icon.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSystemIcon))]
    private Avalonia.Media.IImage? _systemIcon;

    private FileItemViewModel(FileKind kind, string name, string? errorMessage = null)
    {
        Kind = kind;
        Name = name;
        Extension = string.Empty;
        DateText = string.Empty;
        AttrText = string.Empty;
        ErrorMessage = errorMessage;
        _options = s_defaultOptions;
    }

    /// <param name="displayName">Name shown instead of <see cref="VfsEntry.Name"/> (relative path in branch view / lists).</param>
    public FileItemViewModel(VfsEntry entry, DisplayOptions? options = null, IBrush? ruleBrush = null, string? displayName = null)
    {
        _options = options ?? s_defaultOptions;
        Entry = entry;
        Kind = FileKinds.Classify(entry);
        _displayName = displayName;
        var shown = displayName ?? entry.Name;
        Name = entry.IsDirectory || entry.Extension.Length == 0 ? shown : shown[..^(entry.Extension.Length + 1)];
        Extension = entry.Extension;
        DateText = entry.LastWriteTime == DateTime.MinValue ? string.Empty : entry.LastWriteTime.ToString(_options.DateFormat, _options.Culture);
        AttrText = FormatAttributes(entry);
        RuleBrush = ruleBrush;
    }

    public static FileItemViewModel CreateParent() => new(FileKind.Parent, "..");

    public static FileItemViewModel CreateError(string message) => new(FileKind.Error, "<Read error!>", message);

    public VfsEntry? Entry { get; }

    public FileKind Kind { get; }

    public bool IsParent => Kind == FileKind.Parent;

    public bool IsError => Kind == FileKind.Error;

    public string? ErrorMessage { get; }

    public bool IsDirectory => IsParent || Entry?.IsDirectory == true;

    public bool IsHiddenEntry => Entry?.IsHidden == true || Entry?.IsSystem == true;

    public bool IsLink => Entry?.IsLink == true;

    /// <summary>Name without extension for files (extension has its own column).</summary>
    public string Name { get; }

    /// <summary>Icons > No icons: the icon column is empty.</summary>
    public bool ShowIcon { get; set; } = true;

    /// <summary>Hidden/system entry drawn dimmed (Icons > Dimmed hidden files).</summary>
    public bool IsDimmed { get; set; }

    /// <summary>"!" on the icon of hidden/system entries (Icons > Show '!' on hidden/system).</summary>
    public bool ShowHiddenMark { get; set; }

    /// <summary>Data formats > extension directly after the file name.</summary>
    public bool ExtensionInName { get; set; }

    /// <summary>Show folders as "[name]".</summary>
    public bool Brackets { get; set; }

    /// <summary>Appended to folder names ("\" with Display > Append \), not to "..".</summary>
    public string DirectorySuffix { get; set; } = string.Empty;

    /// <summary>Name column text: <see cref="Name"/>, folders in brackets and with <see cref="DirectorySuffix"/>.</summary>
    public string ShownName => Decorate(Name);

    /// <summary>Brief / thumbnail text: <see cref="FullName"/> decorated like <see cref="ShownName"/>.</summary>
    public string BriefName => Decorate(FullName);

    private string Decorate(string name)
    {
        if (!IsDirectory)
        {
            return ExtensionInName && Extension.Length > 0 && ReferenceEquals(name, Name) ? name + "." + Extension : name;
        }
        var text = IsParent ? name : name + DirectorySuffix;
        return Brackets ? "[" + text + "]" : text;
    }

    private readonly string? _displayName;

    /// <summary>Full name as shown and used as the row identity ("..", "&lt;Read error!&gt;", relative path in lists).</summary>
    public string FullName => _displayName ?? Entry?.Name ?? Name;

    public string Extension { get; }

    public string DateText { get; }

    public string AttrText { get; }

    /// <summary>Color from "colors by file type" rules (null = default).</summary>
    public IBrush? RuleBrush { get; }

    public bool HasRuleColor => RuleBrush is not null;

    public bool HasSystemIcon => SystemIcon is not null;

    public Geometry? Icon => FileKinds.GeometryFor(Kind);

    public IBrush IconBrush => FileKinds.BrushFor(Kind);

    /// <summary>The outline of the type icon (outline icon packs), or null.</summary>
    public IBrush? IconStroke => FileKinds.StrokeFor(Kind);

    public long SizeValue => Entry is { IsDirectory: false } e ? e.Size : DirectorySize ?? -1;

    public string SizeText
    {
        get
        {
            if (IsParent)
            {
                return "<UP>";
            }
            if (Entry is null)
            {
                return string.Empty;
            }
            if (Entry.IsDirectory)
            {
                return DirectorySize is { } size ? _options.FormatSize(size) : Entry.IsLink ? "<LINK>" : "<DIR>";
            }
            return _options.FormatSize(Entry.Size);
        }
    }

    private static string FormatAttributes(VfsEntry entry)
    {
        if (entry.UnixMode is { } mode)
        {
            var sb = new StringBuilder(10);
            sb.Append(entry.IsLink ? 'l' : entry.IsDirectory ? 'd' : '-');
            sb.Append(mode.HasFlag(UnixFileMode.UserRead) ? 'r' : '-');
            sb.Append(mode.HasFlag(UnixFileMode.UserWrite) ? 'w' : '-');
            sb.Append(mode.HasFlag(UnixFileMode.UserExecute) ? 'x' : '-');
            sb.Append(mode.HasFlag(UnixFileMode.GroupRead) ? 'r' : '-');
            sb.Append(mode.HasFlag(UnixFileMode.GroupWrite) ? 'w' : '-');
            sb.Append(mode.HasFlag(UnixFileMode.GroupExecute) ? 'x' : '-');
            sb.Append(mode.HasFlag(UnixFileMode.OtherRead) ? 'r' : '-');
            sb.Append(mode.HasFlag(UnixFileMode.OtherWrite) ? 'w' : '-');
            sb.Append(mode.HasFlag(UnixFileMode.OtherExecute) ? 'x' : '-');
            return sb.ToString();
        }
        var a = entry.Attributes;
        return string.Concat(
            a.HasFlag(EntryAttributes.ReadOnly) ? "r" : "-",
            a.HasFlag(EntryAttributes.Archive) ? "a" : "-",
            a.HasFlag(EntryAttributes.Hidden) ? "h" : "-",
            a.HasFlag(EntryAttributes.System) ? "s" : "-");
    }
}
