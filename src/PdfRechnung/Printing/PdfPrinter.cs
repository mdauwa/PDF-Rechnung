using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PdfRechnung.Documents;

namespace PdfRechnung.Printing;

/// <summary>
/// Druckt ein PDF über GDI auf einen Windows-Drucker. Die Seiten zeichnet PDFium (BSD/Apache-2.0) direkt in den
/// Drucker-Kontext. Die weitergegebenen Steuerzeichen werden vorher als echter Text in winziger Schrift auf die
/// jeweilige Seite gedruckt (weiß, unter dem Seiteninhalt) – so kann der gotomaxx-PDFMAILER sie wie aus dem ERP lesen.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PdfPrinter
{
    private static readonly object Lock = new();
    private static bool _initialized;

    public static IReadOnlyList<string> InstalledPrinters() => PrinterSettings.InstalledPrinters.Cast<string>().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Druckt und liefert eine Beschreibung des verwendeten Trennzeichens (leer = Standard @@).</summary>
    public static string Print(GotomaxxJob job, byte[] pdf)
    {
        var (delimiter, info) = job.Codes.Count == 0 ? (GotomaxxProfile.Default, "") : GotomaxxProfile.Resolve(job.Delimiter, job.Printer, job.ProfileIds);
        var printer = InstalledPrinters().FirstOrDefault(p => p.Equals(job.Printer, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Der Drucker „{job.Printer}“ ist auf diesem Server nicht installiert.");

        lock (Lock)
        {
            if (!_initialized) { Pdfium.FPDF_InitLibrary(); _initialized = true; }
            var pin = GCHandle.Alloc(pdf, GCHandleType.Pinned);
            var doc = Pdfium.FPDF_LoadMemDocument(pin.AddrOfPinnedObject(), pdf.Length, null);
            try
            {
                if (doc == IntPtr.Zero) throw new InvalidOperationException($"Das PDF konnte nicht geladen werden (PDFium-Fehler {Pdfium.FPDF_GetLastError()}).");
                var count = Pdfium.FPDF_GetPageCount(doc);
                if (count <= 0) throw new InvalidOperationException("Das PDF hat keine Seiten.");
                var sizes = new (float W, float H)[count];
                for (var i = 0; i < count; i++)
                {
                    var p = Pdfium.FPDF_LoadPage(doc, i);
                    if (p == IntPtr.Zero) throw new InvalidOperationException($"Seite {i + 1} konnte nicht geladen werden.");
                    sizes[i] = (Pdfium.FPDF_GetPageWidthF(p), Pdfium.FPDF_GetPageHeightF(p));
                    Pdfium.FPDF_ClosePage(p);
                }

                using var pd = new PrintDocument();
                pd.PrinterSettings.PrinterName = printer;
                if (!pd.PrinterSettings.IsValid) throw new InvalidOperationException($"Der Drucker „{printer}“ ist nicht verfügbar.");
                pd.DocumentName = string.IsNullOrWhiteSpace(job.DocumentName) ? "Belegpost" : job.DocumentName;
                pd.PrintController = new StandardPrintController(); // kein Fortschrittsfenster
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
                        DrawCodes(g, job, index + 1, delimiter);
                        var hdc = g.GetHdc();
                        try
                        {
                            int offX = GetDeviceCaps(hdc, PHYSICALOFFSETX), offY = GetDeviceCaps(hdc, PHYSICALOFFSETY);
                            int pw = GetDeviceCaps(hdc, PHYSICALWIDTH), ph = GetDeviceCaps(hdc, PHYSICALHEIGHT);
                            var page = Pdfium.FPDF_LoadPage(doc, index);
                            try { Pdfium.FPDF_RenderPage(hdc, page, -offX, -offY, pw, ph, 0, Pdfium.FPDF_ANNOT | Pdfium.FPDF_PRINTING); }
                            finally { Pdfium.FPDF_ClosePage(page); }
                        }
                        finally { g.ReleaseHdc(hdc); }
                    }
                    catch (Exception ex) { pageError ??= ex; }
                    index++;
                    e.HasMorePages = index < count && pageError is null;
                };
                pd.Print();
                if (pageError is not null) throw new InvalidOperationException("Fehler beim Drucken: " + pageError.Message, pageError);
                return info;
            }
            finally
            {
                if (doc != IntPtr.Zero) Pdfium.FPDF_CloseDocument(doc);
                pin.Free();
            }
        }
    }

    /// <summary>Steuerzeichen der Seite als echten Text oben links (Einheit der Grafik: 1/100 Zoll ab Druckbereich).</summary>
    private static void DrawCodes(Graphics g, GotomaxxJob job, int page, string delimiter)
    {
        var lines = job.Codes.Where(c => c.Page == page).Select(c => GotomaxxProfile.WithDelimiter(c.Text, delimiter)).ToList();
        if (lines.Count == 0) return;
        var size = (float)job.FontSize;
        using var font = new Font("Arial", size, GraphicsUnit.Point);
        using var brush = new SolidBrush(job.Visible ? Color.Black : Color.White);
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
        var step = size * 100f / 72f * 1.25f;
        var y = 1f;
        // Sehr lange Zeilen (z. B. @@xr@@ mit Positionstext) würden über den Seitenrand ragen und dort abgeschnitten:
        // solche Zeilen kleiner drucken, damit sie ganz auf die Seite passen.
        var maxWidth = Math.Max(50f, g.VisibleClipBounds.Width - 4f);
        foreach (var t in lines)
        {
            var w = g.MeasureString(t, font, int.MaxValue, format).Width;
            if (w > maxWidth)
            {
                using var small = new Font("Arial", Math.Max(0.3f, size * maxWidth / w * 0.98f), GraphicsUnit.Point);
                g.DrawString(t, small, brush, 1f, y, format);
            }
            else g.DrawString(t, font, brush, 1f, y, format);
            y += step;
        }
    }

    /// <summary>Passendes Papierformat des Druckers (±2 mm), sonst ein eigenes Format in PDF-Größe.</summary>
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
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);
}

/// <summary>Benötigte PDFium-Funktionen (pdfium.dll aus dem Paket bblanchon.PDFium.Win32).</summary>
[SupportedOSPlatform("windows")]
internal static class Pdfium
{
    public const int FPDF_ANNOT = 0x01, FPDF_PRINTING = 0x800;
    private const string Lib = "pdfium";

    [DllImport(Lib)] public static extern void FPDF_InitLibrary();
    [DllImport(Lib, CharSet = CharSet.Ansi)] public static extern IntPtr FPDF_LoadMemDocument(IntPtr data, int size, string? password);
    [DllImport(Lib)] public static extern void FPDF_CloseDocument(IntPtr doc);
    [DllImport(Lib)] public static extern int FPDF_GetPageCount(IntPtr doc);
    [DllImport(Lib)] public static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
    [DllImport(Lib)] public static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Lib)] public static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(Lib)] public static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport(Lib)] public static extern void FPDF_RenderPage(IntPtr dc, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [DllImport(Lib)] public static extern uint FPDF_GetLastError();
}
