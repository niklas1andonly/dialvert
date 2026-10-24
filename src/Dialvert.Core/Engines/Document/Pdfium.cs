using System.Runtime.InteropServices;
using System.Text;

namespace Dialvert.Core.Engines.Document;

/// <summary>
/// Minimal PDFium binding (native pdfium.dll from the bblanchon.PDFium NuGet build).
/// PDFium is not thread-safe, so every call runs under one process-wide lock.
/// </summary>
public sealed class PdfDocument : IDisposable
{
    static readonly Lock Gate = new();
    static bool _initialized;

    readonly IntPtr _doc;
    GCHandle _data; // PDFium reads lazily from this buffer until the document is closed

    PdfDocument(IntPtr doc, GCHandle data)
    {
        _doc = doc;
        _data = data;
    }

    /// <summary>Opens a PDF from disk. The file is read into memory so non-ASCII paths are no issue.</summary>
    public static PdfDocument Open(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        lock (Gate)
        {
            if (!_initialized)
            {
                Native.FPDF_InitLibrary();
                _initialized = true;
            }
            var doc = Native.FPDF_LoadMemDocument64(handle.AddrOfPinnedObject(), (nuint)bytes.Length, IntPtr.Zero);
            if (doc != IntPtr.Zero) return new PdfDocument(doc, handle);
            var error = Native.FPDF_GetLastError();
            handle.Free();
            throw new InvalidOperationException(error switch
            {
                4 => "The PDF is password-protected.",
                5 => "The PDF is locked by its security settings.",
                3 => "The file is not a valid PDF or is damaged.",
                _ => $"Could not open the PDF (PDFium error {error}).",
            });
        }
    }

    public int PageCount
    {
        get { lock (Gate) return Native.FPDF_GetPageCount(_doc); }
    }

    /// <summary>Page size in PDF points (1/72 inch).</summary>
    public (double Width, double Height) PageSize(int index)
    {
        lock (Gate)
        {
            var page = LoadPage(index);
            try { return (Native.FPDF_GetPageWidthF(page), Native.FPDF_GetPageHeightF(page)); }
            finally { Native.FPDF_ClosePage(page); }
        }
    }

    /// <summary>Renders a page on white to a BGRA buffer (stride = width·4), annotations/form fields included.</summary>
    public byte[] Render(int index, int width, int height)
    {
        lock (Gate)
        {
            var page = LoadPage(index);
            var bitmap = Native.FPDFBitmap_Create(width, height, 1);
            if (bitmap == IntPtr.Zero)
            {
                Native.FPDF_ClosePage(page);
                throw new InvalidOperationException("Page too large to render.");
            }
            try
            {
                Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, Native.FPDF_ANNOT);
                var stride = Native.FPDFBitmap_GetStride(bitmap);
                var buffer = Native.FPDFBitmap_GetBuffer(bitmap);
                var result = new byte[width * 4 * height];
                for (var y = 0; y < height; y++)
                    Marshal.Copy(buffer + y * stride, result, y * width * 4, width * 4);
                return result;
            }
            finally
            {
                Native.FPDFBitmap_Destroy(bitmap);
                Native.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>The text layer of a page in reading order as PDFium reports it (lines separated by CRLF).</summary>
    public string Text(int index)
    {
        lock (Gate)
        {
            var page = LoadPage(index);
            var text = Native.FPDFText_LoadPage(page);
            try
            {
                if (text == IntPtr.Zero) return "";
                var count = Native.FPDFText_CountChars(text);
                if (count <= 0) return "";
                var buffer = new ushort[count + 1]; // UTF-16 plus terminator
                var written = Native.FPDFText_GetText(text, 0, count, buffer);
                if (written <= 1) return "";
                return Encoding.Unicode.GetString(MemoryMarshal.AsBytes(buffer.AsSpan(0, written - 1)));
            }
            finally
            {
                if (text != IntPtr.Zero) Native.FPDFText_ClosePage(text);
                Native.FPDF_ClosePage(page);
            }
        }
    }

    IntPtr LoadPage(int index)
    {
        var page = Native.FPDF_LoadPage(_doc, index);
        return page != IntPtr.Zero ? page : throw new InvalidOperationException($"Page {index + 1} could not be loaded.");
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (!_data.IsAllocated) return;
            Native.FPDF_CloseDocument(_doc);
            _data.Free();
        }
    }

    static class Native
    {
        const string Lib = "pdfium";
        public const int FPDF_ANNOT = 0x01;

        [DllImport(Lib)] public static extern void FPDF_InitLibrary();
        [DllImport(Lib)] public static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, nuint size, IntPtr password);
        [DllImport(Lib)] public static extern uint FPDF_GetLastError();
        [DllImport(Lib)] public static extern void FPDF_CloseDocument(IntPtr doc);
        [DllImport(Lib)] public static extern int FPDF_GetPageCount(IntPtr doc);
        [DllImport(Lib)] public static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
        [DllImport(Lib)] public static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Lib)] public static extern float FPDF_GetPageWidthF(IntPtr page);
        [DllImport(Lib)] public static extern float FPDF_GetPageHeightF(IntPtr page);
        [DllImport(Lib)] public static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
        [DllImport(Lib)] public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
        [DllImport(Lib)] public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
        [DllImport(Lib)] public static extern int FPDFBitmap_GetStride(IntPtr bitmap);
        [DllImport(Lib)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
        [DllImport(Lib)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);
        [DllImport(Lib)] public static extern IntPtr FPDFText_LoadPage(IntPtr page);
        [DllImport(Lib)] public static extern void FPDFText_ClosePage(IntPtr textPage);
        [DllImport(Lib)] public static extern int FPDFText_CountChars(IntPtr textPage);
        [DllImport(Lib)] public static extern int FPDFText_GetText(IntPtr textPage, int start, int count, [Out] ushort[] result);
    }
}
