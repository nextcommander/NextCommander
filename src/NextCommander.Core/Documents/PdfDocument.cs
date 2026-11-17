using System.Runtime.InteropServices;

namespace NextCommander.Core.Documents;

/// <summary>A PDF that needs a password (none was given, or the one given is wrong).</summary>
public sealed class PdfPasswordException(bool wrong) : IOException(wrong ? "The password is not right." : "The PDF is protected by a password.")
{
    /// <summary>A password was given but it is not the right one.</summary>
    public bool Wrong { get; } = wrong;
}

/// <summary>
/// A PDF opened with PDFium (the bblanchon.PDFium packages: pdfium.dll / libpdfium.so / libpdfium.dylib for every
/// platform): its pages, their sizes and a page drawn into BGRA pixels on white. PDFium is not for several threads: all
/// its calls hold one lock. The document's bytes are kept in native memory while it is open (PDFium reads them lazily).
/// </summary>
public sealed unsafe partial class PdfDocument : IDisposable
{
    private static readonly Lock s_lock = new();
    private static bool s_initialized;
    private static bool? s_available;

    private IntPtr _document;
    private void* _data;

    private PdfDocument(IntPtr document, void* data, int pageCount)
    {
        _document = document;
        _data = data;
        PageCount = pageCount;
    }

    /// <summary>Whether PDFium could be loaded on this system.</summary>
    public static bool IsAvailable
    {
        get
        {
            lock (s_lock)
            {
                if (s_available is null)
                {
                    try
                    {
                        Initialize();
                        s_available = true;
                    }
                    catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                    {
                        s_available = false;
                    }
                }
                return s_available.Value;
            }
        }
    }

    public int PageCount { get; }

    private static void Initialize()
    {
        if (!s_initialized)
        {
            FPDF_InitLibrary();
            s_initialized = true;
        }
    }

    /// <summary>Opens a PDF from its bytes; an <see cref="IOException"/> says why it cannot be shown (damaged, a password ...).</summary>
    public static PdfDocument Open(ReadOnlySpan<byte> bytes, string? password = null)
    {
        if (!IsAvailable)
        {
            throw new IOException("PDF pages cannot be shown: the PDFium library is missing.");
        }
        var data = NativeMemory.Alloc((nuint)Math.Max(1, bytes.Length));
        bytes.CopyTo(new Span<byte>(data, bytes.Length));
        lock (s_lock)
        {
            var document = FPDF_LoadMemDocument64(data, (nuint)bytes.Length, password);
            if (document == IntPtr.Zero)
            {
                var error = FPDF_GetLastError();
                NativeMemory.Free(data);
                if (error == 4)
                {
                    throw new PdfPasswordException(wrong: !string.IsNullOrEmpty(password));
                }
                throw new IOException(error switch
                {
                    5 => "The PDF uses a security handler PDFium does not have.",
                    3 => "The file is not a PDF or it is damaged.",
                    6 => "A page of the PDF is damaged.",
                    _ => $"The PDF could not be opened (PDFium error {error}).",
                });
            }
            return new PdfDocument(document, data, FPDF_GetPageCount(document));
        }
    }

    /// <summary>The size of a page in points (1/72 inch), as it is shown (its rotation applied).</summary>
    public (double Width, double Height) PageSize(int index)
    {
        lock (s_lock)
        {
            Check();
            double width, height;
            return FPDF_GetPageSizeByIndex(_document, index, &width, &height) != 0 ? (width, height) : (612, 792);
        }
    }

    /// <summary>A page drawn at <paramref name="width"/> × <paramref name="height"/> pixels: BGRA, 4 bytes a pixel, on white.</summary>
    public byte[] Render(int index, int width, int height)
    {
        width = Math.Clamp(width, 1, 16384);
        height = Math.Clamp(height, 1, 16384);
        var pixels = new byte[(long)width * height * 4];
        lock (s_lock)
        {
            Check();
            var page = FPDF_LoadPage(_document, index);
            if (page == IntPtr.Zero)
            {
                throw new IOException($"Page {index + 1} of the PDF could not be read.");
            }
            try
            {
                fixed (byte* buffer = pixels)
                {
                    var bitmap = FPDFBitmap_CreateEx(width, height, BitmapBgra, buffer, width * 4);
                    try
                    {
                        _ = FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF); // white paper (transparent PDFs)
                        FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, RenderAnnotations | RenderLcdText);
                    }
                    finally
                    {
                        FPDFBitmap_Destroy(bitmap);
                    }
                }
            }
            finally
            {
                FPDF_ClosePage(page);
            }
        }
        return pixels;
    }

    private readonly Dictionary<int, string> _texts = [];

    /// <summary>The text of a page as PDFium reads it (one character per index of the page's characters).</summary>
    public string PageText(int index)
    {
        lock (s_lock)
        {
            Check();
            if (_texts.TryGetValue(index, out var cached))
            {
                return cached;
            }
            var text = WithTextPage(index, textPage =>
            {
                var count = FPDFText_CountChars(textPage);
                if (count <= 0)
                {
                    return string.Empty;
                }
                var buffer = new char[count + 1];
                fixed (char* chars = buffer)
                {
                    var written = FPDFText_GetText(textPage, 0, count, (ushort*)chars);
                    return new string(buffer, 0, Math.Clamp(written - 1, 0, count));
                }
            });
            _texts[index] = text;
            return text;
        }
    }

    /// <summary>
    /// The rectangles that cover characters <paramref name="start"/> to <paramref name="start"/> + <paramref name="count"/> of a
    /// page (one per line), in page points from the top left.
    /// </summary>
    public IReadOnlyList<(double Left, double Top, double Right, double Bottom)> TextRectangles(int index, int start, int count)
    {
        lock (s_lock)
        {
            Check();
            var (_, pageHeight) = PageSize(index);
            return WithTextPage(index, textPage =>
            {
                var result = new List<(double, double, double, double)>();
                var rects = FPDFText_CountRects(textPage, start, count);
                for (var i = 0; i < rects; i++)
                {
                    double left, top, right, bottom;
                    if (FPDFText_GetRect(textPage, i, &left, &top, &right, &bottom) != 0)
                    {
                        result.Add((left, pageHeight - top, right, pageHeight - bottom)); // PDF points go up from the bottom
                    }
                }
                return (IReadOnlyList<(double, double, double, double)>)result;
            });
        }
    }

    /// <summary>The character of a page at a point (page points from the top left), or -1 when none is near.</summary>
    public int CharacterAt(int index, double x, double y, double tolerance = 4)
    {
        lock (s_lock)
        {
            Check();
            var (_, pageHeight) = PageSize(index);
            return WithTextPage(index, textPage => FPDFText_GetCharIndexAtPos(textPage, x, pageHeight - y, tolerance, tolerance));
        }
    }

    private T WithTextPage<T>(int index, Func<IntPtr, T> use)
    {
        var page = FPDF_LoadPage(_document, index);
        if (page == IntPtr.Zero)
        {
            throw new IOException($"Page {index + 1} of the PDF could not be read.");
        }
        try
        {
            var textPage = FPDFText_LoadPage(page);
            if (textPage == IntPtr.Zero)
            {
                throw new IOException($"Page {index + 1} of the PDF could not be read.");
            }
            try
            {
                return use(textPage);
            }
            finally
            {
                FPDFText_ClosePage(textPage);
            }
        }
        finally
        {
            FPDF_ClosePage(page);
        }
    }

    private void Check() => ObjectDisposedException.ThrowIf(_document == IntPtr.Zero, this);

    public void Dispose()
    {
        lock (s_lock)
        {
            if (_document != IntPtr.Zero)
            {
                FPDF_CloseDocument(_document);
                _document = IntPtr.Zero;
            }
            if (_data is not null)
            {
                NativeMemory.Free(_data);
                _data = null;
            }
        }
    }

    // ---------------------------------------------------------------- PDFium

    private const string Library = "pdfium";
    private const int BitmapBgra = 4;
    private const int RenderAnnotations = 0x01;
    private const int RenderLcdText = 0x02;

    [LibraryImport(Library)]
    private static partial void FPDF_InitLibrary();

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr FPDF_LoadMemDocument64(void* data, nuint size, string? password);

    [LibraryImport(Library)]
    private static partial uint FPDF_GetLastError();

    [LibraryImport(Library)]
    private static partial int FPDF_GetPageCount(IntPtr document);

    [LibraryImport(Library)]
    private static partial int FPDF_GetPageSizeByIndex(IntPtr document, int index, double* width, double* height);

    [LibraryImport(Library)]
    private static partial IntPtr FPDF_LoadPage(IntPtr document, int index);

    [LibraryImport(Library)]
    private static partial void FPDF_ClosePage(IntPtr page);

    [LibraryImport(Library)]
    private static partial void FPDF_CloseDocument(IntPtr document);

    [LibraryImport(Library)]
    private static partial IntPtr FPDFBitmap_CreateEx(int width, int height, int format, void* buffer, int stride);

    [LibraryImport(Library)]
    private static partial int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Library)]
    private static partial void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);

    [LibraryImport(Library)]
    private static partial void FPDFBitmap_Destroy(IntPtr bitmap);

    [LibraryImport(Library)]
    private static partial IntPtr FPDFText_LoadPage(IntPtr page);

    [LibraryImport(Library)]
    private static partial void FPDFText_ClosePage(IntPtr textPage);

    [LibraryImport(Library)]
    private static partial int FPDFText_CountChars(IntPtr textPage);

    [LibraryImport(Library)]
    private static partial int FPDFText_GetText(IntPtr textPage, int start, int count, ushort* result);

    [LibraryImport(Library)]
    private static partial int FPDFText_CountRects(IntPtr textPage, int start, int count);

    [LibraryImport(Library)]
    private static partial int FPDFText_GetRect(IntPtr textPage, int index, double* left, double* top, double* right, double* bottom);

    [LibraryImport(Library)]
    private static partial int FPDFText_GetCharIndexAtPos(IntPtr textPage, double x, double y, double xTolerance, double yTolerance);
}
