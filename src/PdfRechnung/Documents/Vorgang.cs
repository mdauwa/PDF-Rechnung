namespace PdfRechnung.Documents;

public sealed record CheckMessage(string Level, string Text, string? Field = null);
public sealed record HistoryEntry(DateTimeOffset At, string By, string Action, string? Text = null);

/// <summary>Status eines Vorgangs.</summary>
public static class VorgangStatus
{
    public const string Error = "fehler", Warning = "warnung", Ready = "bereit", NoMail = "ohneVersand",
        Sending = "inVersand", Sent = "versendet", SendError = "versandfehler", Discarded = "verworfen",
        /// <summary>Über den Melder als Entwurf in Outlook geöffnet – der Benutzer versendet selbst.</summary>
        Outlook = "outlook",
        /// <summary>Als Druckauftrag an einen gotomaxx-PDFMAILER-Drucker übergeben – gotomaxx übernimmt den Versand.</summary>
        Gotomaxx = "gotomaxx";

    public static readonly string[] All = { Error, Warning, Ready, NoMail, Sending, Sent, SendError, Discarded, Outlook, Gotomaxx };
    /// <summary>Abgeschlossen: nur diese werden nach Ablauf der Aufbewahrungsdauer gelöscht.</summary>
    public static bool IsFinal(string s) => s is Sent or Discarded or NoMail or Gotomaxx;
}

public sealed class MailData
{
    public bool Enabled { get; set; } = true;
    public List<string> To { get; set; } = new();
    public List<string> Cc { get; set; } = new();
    public List<string> Bcc { get; set; } = new();
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>Zusätzliche Anlagen (Pfade am Server, aus @@ma@@/@@mc@@).</summary>
    public List<string> Attachments { get; set; } = new();
    /// <summary>@@md@@: Mail ohne den Beleg als Anhang.</summary>
    public bool NoDocument { get; set; }
    /// <summary>Normale E-Mail (MAPI ohne Beleg): ohne Prüfung, ohne Briefpapier und Mail-Vorlage – Text und Anlagen wie übergeben.</summary>
    public bool Plain { get; set; }
    /// <summary>Dateiname des PDF-Anhangs (ohne .pdf).</summary>
    public string FileName { get; set; } = "";
    /// <summary>Im Portal geändert – „Neu prüfen“ überschreibt die Versanddaten dann nicht.</summary>
    public bool Edited { get; set; }
    /// <summary>Mail-Vorlage (Name) für die HTML-Gestaltung; null = einfache Text-Mail.</summary>
    public string? Template { get; set; }
    /// <summary>Warum diese Vorlage (Steuerzeichen, Briefpapier, Absender, Standard).</summary>
    public string? TemplateReason { get; set; }
    /// <summary>Absender dieses Belegs (@@pf@@ oder aus der Vorlage); leer = Einstellungen.</summary>
    public string? FromAddress { get; set; }
    public string? FromName { get; set; }
    public string? ReplyTo { get; set; }
    /// <summary>Gewählte Signatur (Name, Text, Grund). Text null = Signatur der Mail-Vorlage.</summary>
    public string? SignatureName { get; set; }
    public string? SignatureText { get; set; }
    public string? SignatureReason { get; set; }
    /// <summary>Versandweg (<see cref="MailRoute"/>), "" = wie eingestellt.</summary>
    public string Route { get; set; } = "";
    public string? RouteReason { get; set; }
    /// <summary>Bei Versandweg gotomaxx: Drucker ("" = Standard).</summary>
    public string? RoutePrinter { get; set; }
    /// <summary>Herkunft von Absender, CC/BCC usw. aus Regeln (Anzeige im Vorgang).</summary>
    public List<string> RuleNotes { get; set; } = new();
    /// <summary>Angewendete Mail-Regeln („eigene Regel „X““, „Regel „Y““).</summary>
    public List<string> AppliedRules { get; set; } = new();
    /// <summary>Was tatsächlich mit der Mail passiert (Versandweg mit Grund) – Anzeige im Vorgang.</summary>
    public string? RouteEffective { get; set; }

    /// <summary>
    /// Darf der Melder die Mail von sich aus in Outlook öffnen (Einstellung „Wenn ein Beleg eintrifft → Outlook“)?
    /// Nein, wenn Regel bzw. @@pw@@ einen anderen Versandweg festlegen (direkt, Freigabe, gotomaxx, keine Mail).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool MelderMayOpenOutlook => Enabled && (string.IsNullOrEmpty(Route) || Route == MailRoute.Outlook);
    /// <summary>Platzhalterwerte (eingebaute + @@pv@@) für Vorlage, Signatur und Fußzeile.</summary>
    public Dictionary<string, string> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Automatische Textkorrekturen (Umlaute, Sonderzeichen).</summary>
    public List<string> TextFixes { get; set; } = new();
    /// <summary>Platzhalter ohne Wert (im Text leer ersetzt).</summary>
    public List<string> MissingPlaceholders { get; set; } = new();
}

/// <summary>Ein gedruckter Beleg mit Prüfergebnis und Versanddaten (meta.json im Vorgangsordner).</summary>
public sealed class Vorgang
{
    public string Id { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Drucker, Eingang (Ordner) oder Upload.</summary>
    public string Source { get; set; } = "";
    public string? OriginalFileName { get; set; }
    public string? PrintUser { get; set; }
    public string? PrintMachine { get; set; }
    /// <summary>Portal-Benutzer, dem der Beleg gehört (aus Druck-Benutzer, @@pu@@ oder Upload).</summary>
    public string? OwnerUserId { get; set; }
    public string? OwnerName { get; set; }
    /// <summary>E-Mail-Adresse des Besitzers (für {MitarbeiterEmail} in Signaturen).</summary>
    public string? OwnerEmail { get; set; }
    /// <summary>ERP-Bedienernummer aus @@pb@@ (3-stellig).</summary>
    public string? OperatorNumber { get; set; }
    /// <summary>Betreff und Empfänger, die das ERP per MAPI übergeben hat (für die Mail-Regeln; haben Vorrang vor Steuerzeichen).</summary>
    public string? MapiSubject { get; set; }
    public List<string> MapiTo { get; set; } = new();
    /// <summary>ERP-Benutzernummer aus dem druckenden Prozess (z. B. wwa.exe 000 …), falls erkannt.</summary>
    public string? ProcessOperator { get; set; }
    /// <summary>Mandant (@@pm@@, /M: des druckenden Prozesses oder Eingangsordner des Mandanten).</summary>
    public string? Mandant { get; set; }
    /// <summary>Mandant aus der Herkunft (Prozess /M: bzw. Eingangsordner); @@pm@@ im Beleg übersteuert ihn.</summary>
    public string? OriginMandant { get; set; }
    /// <summary>Woher der Mandant stammt (Anzeige).</summary>
    public string? MandantReason { get; set; }
    /// <summary>Woher der Herkunfts-Mandant stammt (Prozess /M:, Drucker, Eingangsordner).</summary>
    public string? OriginMandantReason { get; set; }
    /// <summary>Mandant wurde im Portal von Hand gesetzt – „Neu prüfen“ ändert ihn dann nicht mehr.</summary>
    public bool MandantManual { get; set; }
    /// <summary>Druckender Prozess mit Befehlszeile (Anzeige).</summary>
    public string? PrintProcess { get; set; }
    /// <summary>Zusätzlicher Belegpost-Drucker, über den gedruckt wurde (Kennung); null = Hauptdrucker bzw. andere Quelle.</summary>
    public string? PrinterId { get; set; }
    /// <summary>Kunde mit eigenen Versandwegen (Kunden-Verwaltung), falls erkannt.</summary>
    public string? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    /// <summary>Woran der Kunde erkannt wurde, ggf. mit Regel.</summary>
    public string? CustomerReason { get; set; }
    /// <summary>Versandwege des Kunden und ihr Ergebnis. Leer = Versand wie bisher per Mail.</summary>
    public List<PdfRechnung.Delivery.VorgangDelivery> Deliveries { get; set; } = new();
    /// <summary>ERP-Belegindex (BELNDX) aus @@pi@@ – nur dann wird der Status ins ERP zurückgeschrieben.</summary>
    public string? ErpIndex { get; set; }
    /// <summary>Aktueller Belegstatus als 3-stelliger Code (siehe <see cref="StatusCode"/>).</summary>
    public string? StatusCode { get; set; }
    /// <summary>Anzahl erfolgreicher Versände (für „erneut versendet“).</summary>
    public int SendCount { get; set; }
    /// <summary>Über welchen Weg zuletzt versendet: „Mail“ (Dienst) oder „Outlook“.</summary>
    public string? SendChannel { get; set; }
    /// <summary>Wann der Entwurf an Outlook übergeben bzw. der Versand aus Outlook bestätigt wurde.</summary>
    public DateTimeOffset? OutlookHandedAt { get; set; }
    public DateTimeOffset? OutlookConfirmedAt { get; set; }
    public int Pages { get; set; }
    /// <summary>Weiterleitung an gotomaxx: null = nein, "" = Standarddrucker, sonst Druckername (aus @@pg@@ oder „immer“).</summary>
    public string? GotomaxxPrinter { get; set; }
    /// <summary>@@pa@@: über den Melder speichern – null = nein, "" = Ablageordner, sonst Ordner (relativ zum Ablageordner oder absolut).</summary>
    public string? SaveRequest { get; set; }
    /// <summary>@@pn@@: nach dem Senden (Mail versendet bzw. Outlook-Entwurf geöffnet) über den Melder speichern – Wert wie bei <see cref="SaveRequest"/>.</summary>
    public string? SaveAfterSendRequest { get; set; }
    /// <summary>Speichern nach dem Senden wurde schon beauftragt (nur einmal, auch bei erneutem Senden).</summary>
    public bool SaveAfterSendQueued { get; set; }
    /// <summary>@@pp@@: über den Melder drucken – null = nein, "" = Standarddrucker des PCs, sonst Druckername.</summary>
    public string? PrintRequest { get; set; }

    public string Status { get; set; } = VorgangStatus.Warning;
    public List<CheckMessage> Messages { get; set; } = new();

    public string DocumentType { get; set; } = "Dokument";
    public string? VoucherId { get; set; }
    public string? VoucherDate { get; set; }
    public string? BuyerId { get; set; }
    public string? BuyerName { get; set; }
    public decimal? GrossAmount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string? FormatCode { get; set; }
    public string? FormatName { get; set; }
    /// <summary>Unterlegtes Briefpapier (Name) im Versand-PDF.</summary>
    public string? Stationery { get; set; }
    /// <summary>Anzahl der aus dem Versand-PDF entfernten Steuerzeichen-Textbefehle.</summary>
    public int RemovedCodes { get; set; }
    /// <summary>Meldungen beim Erzeugen des Versand-PDFs (Briefpapier, Ränder) – Teil der Prüfung.</summary>
    public List<CheckMessage> LayoutMessages { get; set; } = new();
    /// <summary>Von Belegpost erzeugte E-Rechnung (CII): Profil, z. B. „ZUGFeRD 2 Comfort (EN 16931)“; null = keine.</summary>
    public string? EInvoiceProfile { get; set; }
    /// <summary>Im Portal korrigierte Rechnungsdaten (Stammdaten, Einheiten, Befreiungsgrund …).</summary>
    public InvoiceCorrections Corrections { get; set; } = new();
    /// <summary>Gerade erzeugte XML, wird beim Speichern als <c>rechnung.xml</c> abgelegt.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public byte[]? PendingEInvoice { get; set; }

    public MailData Mail { get; set; } = new();
    public InvoiceData Invoice { get; set; } = new();
    public List<ControlCode> Codes { get; set; } = new();

    public DateTimeOffset? SentAt { get; set; }
    public string? SentBy { get; set; }
    public string? SendError { get; set; }
    public int SendAttempts { get; set; }
    public List<HistoryEntry> History { get; set; } = new();

    public int ErrorCount => Messages.Count(m => m.Level == DocumentChecker.Error);
    public int WarningCount => Messages.Count(m => m.Level == DocumentChecker.Warning);

    /// <summary>Status nach der Prüfung (versendete/verworfene Vorgänge bleiben, wie sie sind).</summary>
    public void UpdateStatus()
    {
        if (Status is VorgangStatus.Sent or VorgangStatus.Discarded or VorgangStatus.Sending or VorgangStatus.Outlook or VorgangStatus.Gotomaxx) return;
        Status = ErrorCount > 0 ? VorgangStatus.Error
            : !Mail.Enabled ? VorgangStatus.NoMail
            : WarningCount > 0 ? VorgangStatus.Warning
            : VorgangStatus.Ready;
    }

    /// <summary>Kurzform für die Liste.</summary>
    public object Summary() => new
    {
        Id, CreatedAt, Source, Status, DocumentType, VoucherId, VoucherDate, BuyerId, BuyerName, GrossAmount, Currency,
        FormatName, To = Mail.To, Mail.Subject, MailEnabled = Mail.Enabled, ErrorCount, WarningCount, SentAt, SentBy, PrintUser, OperatorNumber, OwnerUserId, OwnerName, ErpIndex, Mandant, StatusCode, SendChannel, OutlookConfirmedAt, GotomaxxPrinter,
        FirstMessage = Messages.FirstOrDefault(m => m.Level == DocumentChecker.Error)?.Text ?? Messages.FirstOrDefault(m => m.Level == DocumentChecker.Warning)?.Text
    };
}
