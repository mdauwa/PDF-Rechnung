using System.Text;
using PdfRechnung.Connect;
using PdfRechnung.Edi;
using Xunit;

namespace PdfRechnung.Tests;

public class ConnectValidatorTests
{
    private const string Order = """
<?xml version="1.0" encoding="UTF-8"?>
<ROOT>
  <ORDER>
    <Status>I</Status>
    <Bestellnummer>4711</Bestellnummer>
    <PaymentStatus>processing</PaymentStatus>
    <PaymentType>PayPal</PaymentType>
    <PaymentTotal>22.80</PaymentTotal>
    <Datum>16.11.2026</Datum>
    <Steuerberechnung>Brutto</Steuerberechnung>
    <ADRESS>
      <ShopID>999999</ShopID>
      <Adressnummer>10001</Adressnummer>
      <Nachname>Muster</Nachname>
      <Strasse>Hauptplatz 1</Strasse>
      <PLZ>1010</PLZ>
      <Ort>Wien</Ort>
      <Land>AT</Land>
      <Email>max@muster.at</Email>
    </ADRESS>
    <ARTIKEL>
      <Artikelnummer>A-100</Artikelnummer>
      <ProductID>2241</ProductID>
      <Menge>2</Menge>
      <Netto>10.00</Netto>
      <Brutto>12.00</Brutto>
      <Steuersatz>20</Steuersatz>
    </ARTIKEL>
  </ORDER>
</ROOT>
""";

    private static EdiReport Check(string xml, string name = "ORDER_4711.xml") => EdiValidation.Validate(Encoding.UTF8.GetBytes(xml), name);

    [Fact]
    public void Gueltige_Bestellung()
    {
        var r = Check(Order);
        Assert.Equal("ERP Suite Connect", r.Format);
        Assert.Contains("ORDER", r.Title);
        Assert.True(r.Valid, string.Join("\n", r.Issues.Select(i => i.Text)));
        Assert.Equal(0, r.Warnings);
    }

    [Fact]
    public void Pflichtfelder_Tippfehler_Werte()
    {
        var r = Check(Order.Replace("<Email>max@muster.at</Email>", "").Replace("<Menge>2</Menge>", "<Menge>zwei</Menge>")
                           .Replace("<Status>I</Status>", "<Status>I</Status><PaymentTranactionID>X1</PaymentTranactionID><Lieblingsfarbe>blau</Lieblingsfarbe>"));
        Assert.Contains(r.Issues, i => i.Level == EdiReport.Fehler && i.Text.Contains("<Email>"));
        Assert.Contains(r.Issues, i => i.Level == EdiReport.Fehler && i.Text.Contains("<Menge>") && i.Text.Contains("Zahl"));
        Assert.Contains(r.Issues, i => i.Level == EdiReport.Warnung && i.Text.Contains("PaymentTransactionID"));
        Assert.Contains(r.Issues, i => i.Level == EdiReport.Hinweis && i.Text.Contains("Lieblingsfarbe"));
    }

    [Fact]
    public void Aliase_des_Programms_gelten_als_vorhanden()
    {
        var r = Check(Order.Replace("<Strasse>", "<Street>").Replace("</Strasse>", "</Street>").Replace("<Datum>", "<OrderDate>").Replace("</Datum>", "</OrderDate>"));
        Assert.DoesNotContain(r.Issues, i => i.Text.Contains("<Strasse>") || i.Text.Contains("<Datum>"));
    }

    [Fact]
    public void Typ_am_Dateinamen_und_Inhalt()
    {
        var s = ConnectSchema.Current;
        Assert.Equal("ORDERSTATUS", s.ByName("Orderstatus_12345.xml")!.Key);
        Assert.Equal("ORDER", s.ByName("Order_12607.xml")!.Key);
        Assert.Equal("ARTIKELSTAMM", s.ByName("Artikel_29238.XML")!.Key);
        Assert.Equal("LAGERBESTAND", s.ByName("Lagerbestand_1.xml")!.Key);
        var lb = Check("""<?xml version="1.0" encoding="utf-8"?><ROOT><ARTIKEL><Status>U</Status><Artikelnummer>A1</Artikelnummer><Lagerbestand>17</Lagerbestand></ARTIKEL></ROOT>""", "Lagerbestand_5.xml");
        Assert.True(lb.Valid, string.Join("\n", lb.Issues.Select(i => i.Text)));
        var unbekannt = Check(Order, "bestellung.xml");
        Assert.Contains(unbekannt.Issues, i => i.Text.Contains("Dateinamen"));
    }
}
