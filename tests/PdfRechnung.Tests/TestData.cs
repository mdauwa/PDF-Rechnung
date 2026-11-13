using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace PdfRechnung.Tests;

/// <summary>Beispielbeleg mit Steuerzeichen, wie ihn die ERP-Vorlage (SEBELK/SEBELP) druckt.</summary>
public static class TestData
{
    public static readonly string[] InvoiceCodes =
    {
        "@@xt gotomaxx.invoice.zugferd2.comfort@@",
        "@@xi SELLER_NAME=ERP Austria GmbH@@",
        "@@xi SELLER_ADDRESS_LINE=Musterstraße 1@@",
        "@@xi SELLER_ADDRESS_ZIP=8010@@",
        "@@xi SELLER_ADDRESS_CITY=Graz@@",
        "@@xi SELLER_ADDRESS_COUNTRY=AT@@",
        "@@xi SELLER_TAX_ID=ATU12345678@@",
        "@@xi SELLER_IBAN=AT611904300234573201@@",
        "@@xi SELLER_BIC=BKAUATWW@@",
        "@@xi BUYER_ID=10001@@",
        "@@xi BUYER_NAME=Kunde & Co KG@@",
        "@@xi BUYER_ADDRESS_LINE=Hauptplatz 5@@",
        "@@xi BUYER_ADDRESS_ZIP=1010@@",
        "@@xi BUYER_ADDRESS_CITY=Wien@@",
        "@@xi BUYER_ADDRESS_COUNTRY=AT@@",
        "@@xi DOCUMENT_TYPE=380@@",
        "@@xi VOUCHER_ID=R2026-0815@@",
        "@@xi VOUCHER_DATE=13.11.2026@@",
        "@@xi DELIVERY_DATE=12.11.2026@@",
        "@@xi CURRENCY=EUR@@",
        "@@xi PAYMENT_MEANS_TYPE_CODE=58@@",
        "@@xi GROSS_AMOUNT=150,00@@",
        "@@xr table=ALLOWANCE_CHARGE|ALLOWANCE_CHARGE_TYPE=CHARGE|ALLOWANCE_CHARGE_AMOUNT=10,00|ALLOWANCE_CHARGE_REASON=Nebenkosten 1|ALLOWANCE_CHARGE_VAT_CATEGORY=S|ALLOWANCE_CHARGE_VAT_PERCENT=20@@",
        "@@xr table=ITEM_DETAILS|ITEM_LINE_ID=1|ITEM_SELLER_ID=A-100|ITEM_NAME=Beratung|ITEM_QUANTITY=2|ITEM_PRICE_BASE_QUANTITY=1|ITEM_UNIT=Std|ITEM_GROSS_PRICE=50,00|ITEM_VAT_PERCENT=20|ITEM_VAT_CATEGORY=S|ITEM_TOTAL_AMOUNT=100,00@@",
        "@@xr table=ITEM_DETAILS|ITEM_LINE_ID=2|ITEM_SELLER_ID=B-200|ITEM_NAME=Lizenz|ITEM_QUANTITY=1|ITEM_PRICE_BASE_QUANTITY=1|ITEM_UNIT=Stk|ITEM_GROSS_PRICE=15,00@@",
        "@@<<|ITEM_VAT_PERCENT=20|ITEM_VAT_CATEGORY=S|ITEM_TOTAL_AMOUNT=15,00@@",
        "@@toeinkauf@kunde.at@@",
        "@@ccbuchhaltung@kunde.at@@",
        "@@edIhre Rechnung R2026-0815@@",
        "@@saSehr geehrte Damen und Herren,@@",
        "@@sa@@",
        "@@saanbei Ihre Rechnung.@@",
        "@@bpBriefpapier Standard@@",
        "@@fnRechnung_R2026-0815@@"
    };

    /// <summary>Erzeugt ein PDF: sichtbarer Rechnungstext plus die Steuerzeichen in Courier (wie vom ERP gedruckt).</summary>
    public static byte[] Pdf(IEnumerable<string> codes, string title = "Rechnung R2026-0815")
    {
        var builder = new PdfDocumentBuilder();
        var helv = builder.AddStandard14Font(Standard14Font.Helvetica);
        var courier = builder.AddStandard14Font(Standard14Font.Courier);
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        page.AddText(title, 16, new PdfPoint(60, 780), helv);
        page.AddText("Kunde & Co KG, Hauptplatz 5, 1010 Wien", 10, new PdfPoint(60, 750), helv);
        var y = 700.0;
        foreach (var c in codes)
        {
            page.AddText(c, 5, new PdfPoint(20, y), courier);
            y -= 9;
        }
        return builder.Build();
    }
}
