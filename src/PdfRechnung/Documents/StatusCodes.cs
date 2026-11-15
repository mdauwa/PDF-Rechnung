namespace PdfRechnung.Documents;

/// <summary>
/// Belegstatus als 3-stelliger Code (Zeitstrahl, Status-Link, ERP-Rückmeldung).
/// Gruppen: 0xx in Arbeit · 1xx versendet · 2xx ohne Versand erledigt · 8xx Fehler · 9xx abgebrochen.
/// Die Werte, die ins ERP geschrieben werden, sind in der ERP-Rückmeldung anpassbar.
/// </summary>
public static class StatusCode
{
    public const string Erfasst = "001", WartetFreigabe = "010", Freigegeben = "020", AnOutlook = "030",
        Versendet = "100", VersendetOutlook = "110", ErneutVersendet = "120", AnGotomaxx = "140", OhneVersand = "200",
        FehlerBeleg = "800", Versandfehler = "810", OutlookFehler = "820", OutlookUnbestaetigt = "830", GotomaxxFehler = "840", Verworfen = "900";

    /// <summary>Hinweis im Zeitstrahl (kein Belegstatus): Status wurde ins ERP geschrieben.</summary>
    public const string ErpBestaetigt = "ERP";

    /// <summary>Alle echten Statuscodes (ohne Hinweise) in Reihenfolge.</summary>
    public static readonly string[] All = { Erfasst, WartetFreigabe, Freigegeben, AnOutlook, Versendet, VersendetOutlook, ErneutVersendet,
        AnGotomaxx, OhneVersand, FehlerBeleg, Versandfehler, OutlookFehler, OutlookUnbestaetigt, GotomaxxFehler, Verworfen };

    public static bool IsStatus(string? code) => code is not null && code != ErpBestaetigt;

    public static readonly IReadOnlyDictionary<string, string> Texts = new Dictionary<string, string>
    {
        [Erfasst] = "erfasst",
        [WartetFreigabe] = "wartet auf Freigabe",
        [Freigegeben] = "freigegeben, Versand geplant",
        [AnOutlook] = "an Outlook übergeben",
        [Versendet] = "versendet",
        [VersendetOutlook] = "versendet über Outlook",
        [ErneutVersendet] = "erneut versendet",
        [AnGotomaxx] = "an gotomaxx PDFMAILER übergeben",
        [OhneVersand] = "abgelegt ohne Versand",
        [FehlerBeleg] = "Fehler im Beleg",
        [Versandfehler] = "Versandfehler",
        [OutlookFehler] = "Outlook-Übergabe fehlgeschlagen",
        [OutlookUnbestaetigt] = "Outlook-Versand nicht bestätigt",
        [GotomaxxFehler] = "Übergabe an gotomaxx fehlgeschlagen",
        [Verworfen] = "verworfen",
        [ErpBestaetigt] = "Status im ERP bestätigt"
    };

    public static string Text(string? code) => code is not null && Texts.TryGetValue(code, out var t) ? t : code ?? "";

    /// <summary>Ampel für den Status-Link: ok, offen, fehler, neutral.</summary>
    public static string Light(string? code) => code switch
    {
        Versendet or VersendetOutlook or ErneutVersendet or AnGotomaxx or OhneVersand => "ok",
        FehlerBeleg or Versandfehler or OutlookFehler or OutlookUnbestaetigt or GotomaxxFehler => "fehler",
        Verworfen => "neutral",
        _ => "offen"
    };

    /// <summary>Code zum Portal-Status eines Vorgangs (Versand-Kanal entscheidet bei „versendet“).</summary>
    public static string? FromVorgang(Vorgang v, bool alreadySent) => v.Status switch
    {
        VorgangStatus.Error => FehlerBeleg,
        VorgangStatus.Warning or VorgangStatus.Ready => WartetFreigabe,
        VorgangStatus.NoMail => OhneVersand,
        VorgangStatus.Sending => Freigegeben,
        VorgangStatus.Sent => alreadySent ? ErneutVersendet : v.SendChannel == "Outlook" ? VersendetOutlook : Versendet,
        VorgangStatus.SendError => v.SendChannel == GotomaxxService.Channel ? GotomaxxFehler : Versandfehler,
        VorgangStatus.Gotomaxx => AnGotomaxx,
        VorgangStatus.Discarded => Verworfen,
        VorgangStatus.Outlook => AnOutlook,
        _ => null
    };
}

/// <summary>Ein Eintrag im Zeitstrahl eines Vorgangs.</summary>
public sealed record VorgangEvent(string VorgangId, DateTimeOffset At, string Code, string? Channel, string? By, string? Text)
{
    public string CodeText => StatusCode.Text(Code);
}
