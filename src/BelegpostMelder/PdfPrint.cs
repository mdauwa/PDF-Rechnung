using System.Drawing.Printing;
using System.Runtime.InteropServices;

namespace BelegpostMelder;

/// <summary>
/// Druckt ein PDF (@@pp@@) über GDI auf einen Drucker dieses PCs. Die Seiten zeichnet PDFium (BSD/Apache-2.0)
/// direkt in den Drucker-Kontext – es braucht kein PDF-Programm.
/// </summary>
internal static class PdfPrint
{
    private static readonly object Lock = new();
    private static bool _initialized;

    /// <summary>Standarddrucker dieses Benutzers (leer, wenn keiner eingestellt ist).</summary>
    public static string DefaultPrinter()
    {
        try { return new PrinterSettings().PrinterName ?? ""; } catch { return ""; }
    }

    /// <summary>Druckt und liefert den verwendeten Druckernamen. <paramref name="printer"/> leer = Standarddrucker.</summary>
    public static string Print(byte[] pdf, string? printer, string documentName)
    {
        var name = string.IsNullOrWhiteSpace(printer) ? DefaultPrinter() : printer.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Auf diesem PC ist kein Standarddrucker eingestellt.");
        var installed = PrinterSettings.InstalledPrinters.Cast<string>().FirstOrDefault(p => p.Equals(name, StringComparison.OrdinalIgnoreCase))
            // Netzwerkdrucker werden oft nur mit Namen statt \\server\name angegeben
            ?? PrinterSettings.InstalledPrinters.Cast<string>().FirstOrDefault(p => p.EndsWith("\\" + name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Der Drucker „{name}“ ist auf diesem PC nicht eingerichtet.");

        lock (Lock)
        {
            if (!_initialized) { FPDF_InitLibrary(); _initialized = true; }
            var pin = GCHandle.Alloc(pdf, GCHandleType.Pinned);
            var doc = FPDF_LoadMemDocument(pin.AddrOfPinnedObject(), pdf.Length, null);
            try
            {
                if (doc == IntPtr.Zero) throw new InvalidOperationException($"Das PDF konnte nicht geladen werden (PDFium-Fehler {FPDF_GetLastError()}).");
                var count = FPDF_GetPageCount(doc);
                if (count <= 0) throw new InvalidOperationException("Das PDF hat keine Seiten.");
                var sizes = new (float W, float H)[count];
                for (var i = 0; i < count; i++)
                {
                    var p = FPDF_LoadPage(doc, i);
                    if (p == IntPtr.Zero) throw new InvalidOperationException($"Seite {i + 1} konnte nicht geladen werden.");
                    sizes[i] = (FPDF_GetPageWidthF(p), FPDF_GetPageHeightF(p));
                    FPDF_ClosePage(p);
                }

                using var pd = new PrintDocument();
                pd.PrinterSettings.PrinterName = installed;
                if (!pd.PrinterSettings.IsValid) throw new InvalidOperationException($"Der Drucker „{installed}“ ist nicht verfügbar.");
                pd.DocumentName = string.IsNullOrWhiteSpace(documentName) ? "Belegpost" : documentName;
                pd.PrintController = new StandardPrintController();
                pd.OriginAtMargins = false;
                var index = 0;
                Exception? pageError = null;
                pd.QueryPageSettings += (_, e) =>
                {
                    var (w, h) = sizes[Math.Min(index, count - 1)];
                    e.PageSettings.Landscape = w > h;
                    e.PageSettings.PaperSize = Paper(pd.PrinterSettings, Math.Min(w, h), Math.Max(w, h));
                    e.PageSettings.Margins = new Margins(0, 0, 0, 0);
                };
                pd.PrintPage += (_, e) =>
                {
                    try
                    {
                        var g = e.Graphics!;
                        var hdc = g.GetHdc();
                        try
                        {
                            int offX = GetDeviceCaps(hdc, PHYSICALOFFSETX), offY = GetDeviceCaps(hdc, PHYSICALOFFSETY);
                            int pw = GetDeviceCaps(hdc, PHYSICALWIDTH), ph = GetDeviceCaps(hdc, PHYSICALHEIGHT);
                            var page = FPDF_LoadPage(doc, index);
                            try { FPDF_RenderPage(hdc, page, -offX, -offY, pw, ph, 0, FPDF_ANNOT | FPDF_PRINTING); }
                            finally { FPDF_ClosePage(page); }
                        }
                        finally { g.ReleaseHdc(hdc); }
                    }
                    catch (Exception ex) { pageError ??= ex; }
                    index++;
                    e.HasMorePages = index < count && pageError is null;
                };
                pd.Print();
                if (pageError is not null) throw new InvalidOperationException("Fehler beim Drucken: " + pageError.Message, pageError);
                return installed;
            }
            finally
            {
                if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
                pin.Free();
            }
        }
    }

    private static PaperSize Paper(PrinterSettings ps, float shortPt, float longPt)
    {
        static double Hundredths(float pt) => pt * 100.0 / 72.0;
        double s = Hundredths(shortPt), l = Hundredths(longPt), tol = 2 / 25.4 * 100;
        foreach (PaperSize p in ps.PaperSizes)
        {
            double a = Math.Min(p.Width, p.Height), b = Math.Max(p.Width, p.Height);
            if (Math.Abs(a - s) <= tol && Math.Abs(b - l) <= tol) return p;
        }
        return new PaperSize("Belegpost", (int)Math.Round(s), (int)Math.Round(l));
    }

    private const int PHYSICALWIDTH = 110, PHYSICALHEIGHT = 111, PHYSICALOFFSETX = 112, PHYSICALOFFSETY = 113;
    private const int FPDF_ANNOT = 0x01, FPDF_PRINTING = 0x800;
    private const string Lib = "pdfium";
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
    [DllImport(Lib)] private static extern void FPDF_InitLibrary();
    [DllImport(Lib, CharSet = CharSet.Ansi)] private static extern IntPtr FPDF_LoadMemDocument(IntPtr data, int size, string? password);
    [DllImport(Lib)] private static extern void FPDF_CloseDocument(IntPtr doc);
    [DllImport(Lib)] private static extern int FPDF_GetPageCount(IntPtr doc);
    [DllImport(Lib)] private static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
    [DllImport(Lib)] private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Lib)] private static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(Lib)] private static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport(Lib)] private static extern void FPDF_RenderPage(IntPtr dc, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [DllImport(Lib)] private static extern uint FPDF_GetLastError();
}
