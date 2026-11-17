using System.Runtime.InteropServices;

namespace NextCommander.Core.Documents;

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
                throw new IOException(error switch
                {
                    4 => "The PDF is protected by a password.",
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
}
