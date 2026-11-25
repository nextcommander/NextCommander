using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using NextCommander.Core.Compare;
using NextCommander.Core.Text;
using NextCommander.Core.Vfs;

namespace NextCommander.App.Views.Tools;

/// <summary>What the compare window shows two files as.</summary>
public enum CompareMode
{
    /// <summary>Text, edited in place.</summary>
    Text,

    /// <summary>Binary files as hex dumps side by side (read-only).</summary>
    Hex,

    /// <summary>The text of Office / OpenDocument / PDF files (read-only).</summary>
    Document,

    /// <summary>Two pictures: side by side, overlaid or their difference.</summary>
    Picture,
}

/// <summary>
/// Binary files, documents and pictures in the compare window (the compare and merge plan, phase 7; 25 November 2026): what the
/// two files are decides how they are compared; a side with unsaved text keeps it.
/// </summary>
public sealed partial class CompareWindow
{
    private static readonly HashSet<string> s_pictures = new(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "bmp", "gif", "webp", "ico" };

    private readonly Border _pictureHost = new() { IsVisible = false };
    private byte[] _hexLeft = [];
    private byte[] _hexRight = [];

    public CompareMode Mode { get; private set; }

    /// <summary>The picture compare shown (tests).</summary>
    public PictureCompareView? Pictures => _pictureHost.Child as PictureCompareView;

    /// <summary>
    /// Reads the files and decides how they are compared; true when a view of its own is shown (hex, pictures), false
    /// when the sides hold the lines to compare.
    /// </summary>
    private async Task<bool> LoadAsync()
    {
        // a side with unsaved changes keeps them; the other is read again (what it is decides the mode)
        if (!_l.Dirty)
        {
            _l.Loaded = false;
        }
        if (!_r.Dirty)
        {
            _r.Loaded = false;
        }
        byte[]? a, b;
        try
        {
            a = _l.Loaded ? null : await ReadAsync(_l.Entry);
            b = _r.Loaded ? null : await ReadAsync(_r.Entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _summary.Text = ex.Message;
            return true;
        }
        Mode = CompareMode.Text;
        IsBinary = false;
        _pictureHost.IsVisible = false;
        _pictureHost.Child = null;
        if (a is not null && b is not null)
        {
            if (s_pictures.Contains(_l.Entry.Extension) && s_pictures.Contains(_r.Entry.Extension) && PictureCompareView.TryCreate(a, b) is { } pictures)
            {
                ShowPictures(pictures);
                return true;
            }
            if (DocumentText.Supports(_l.Entry.Extension) && DocumentText.Supports(_r.Entry.Extension))
            {
                try
                {
                    var (left, right) = await Task.Run(() => (DocumentText.Lines(_l.Entry.Extension, a), DocumentText.Lines(_r.Entry.Extension, b)));
                    _l.LoadLines(left);
                    _r.LoadLines(right);
                    Mode = CompareMode.Document;
                    AfterLoad();
                    return false;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException or FormatException or ArgumentException or NotSupportedException)
                {
                    // not a document that can be read: compared by its bytes
                }
            }
        }
        static bool Binary(byte[]? data) => data is not null && EncodingDetector.LooksBinary(data.AsSpan(0, Math.Min(data.Length, 8192)));
        if (Binary(a) || Binary(b))
        {
            try
            {
                _hexLeft = a ?? await ReadAsync(_l.Entry);
                _hexRight = b ?? await ReadAsync(_r.Entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _summary.Text = ex.Message;
                return true;
            }
            ShowHex();
            return true;
        }
        if (a is not null)
        {
            _l.Load(a, WritableOf(_l.Entry));
        }
        if (b is not null)
        {
            _r.Load(b, WritableOf(_r.Entry));
        }
        AfterLoad();
        return false;
    }

    private void AfterLoad()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        ShowText();
        Watch();
        BuildFileHeaders();
    }

    /// <summary>Two binary files as hex dumps side by side: 16 bytes a row at the same offset, the differing bytes marked.</summary>
    private void ShowHex()
    {
        Mode = CompareMode.Hex;
        IsBinary = true;
        _l.LoadLines([]);
        _r.LoadLines([]);
        ShowText();
        _ignoreCase.IsEnabled = _ignoreSpace.IsEnabled = false;
        _rows = HexCompare.Rows(_hexLeft, _hexRight);
        _hunks = Hunk.Of(_rows);
        BuildPanes();
        ShowBinary(_hexLeft, _hexRight);
        BuildFileHeaders();
        UpdateTitle();
        _currentBlock = _hunks.Count > 0 ? 0 : -1;
        Highlight(scroll: true);
    }

    /// <summary>Two pictures: the picture view in place of the lines.</summary>
    private void ShowPictures(PictureCompareView view)
    {
        Mode = CompareMode.Picture;
        IsBinary = true;
        _l.LoadLines([]);
        _r.LoadLines([]);
        _rows = [];
        _hunks = [];
        _currentBlock = -1;
        _pictureHost.Child = view;
        _pictureHost.IsVisible = true;
        _body.IsVisible = false;
        _binaryList.IsVisible = false;
        foreach (var control in new Control[] { _split, _inline, _ignoreCase, _ignoreSpace, _previous, _next, _copyLeft, _copyRight, _undo, _redo, _save })
        {
            control.IsEnabled = false;
        }
        view.Changed += (_, _) => SummarizePictures(view);
        SummarizePictures(view);
        BuildFileHeaders();
        UpdateTitle();
        _counter.Text = "–";
        UpdateToggles();
    }

    private void SummarizePictures(PictureCompareView view)
    {
        var result = view.Result;
        var size = $"{result.LeftWidth}×{result.LeftHeight} → {result.RightWidth}×{result.RightHeight}";
        _stats.Children.Clear();
        _stats.Children.Add(Chip(result.Different == 0 && result.SameSize ? "identical" : $"{result.Percent.ToString("0.##", CultureInfo.CurrentCulture)} % different",
            _palette.Muted, _palette.Surface2));
        _summary.Text = result.Different == 0 && result.SameSize ? $"{size} · the pictures are identical"
            : result.SameSize ? $"{size} · {result.Different:N0} of {result.Compared:N0} pixels differ"
            : $"{size} · {result.Different:N0} of {result.Compared:N0} pixels of the common area differ";
    }
}
