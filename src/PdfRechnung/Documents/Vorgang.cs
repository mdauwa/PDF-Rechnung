namespace PdfRechnung.Documents;

public sealed record CheckMessage(string Level, string Text, string? Field = null);
public sealed record HistoryEntry(DateTimeOffset At, string By, string Action, string? Text = null);

/// <summary>Status eines Vorgangs.</summary>
public static class VorgangStatus
{
    public const string Error = "fehler", Warning = "warnung", Ready = "bereit", NoMail = "ohneVersand",
        Sending = "inVersand", Sent = "versendet", SendError = "versandfehler", Discarded = "verworfen";

    public static readonly string[] All = { Error, Warning, Ready, NoMail, Sending, Sent, SendError, Discarded };
    /// <summary>Abgeschlossen: nur diese werden nach Ablauf der Aufbewahrungsdauer gelöscht.</summary>
    public static bool IsFinal(string s) => s is Sent or Discarded or NoMail;
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
    /// <summary>Dateiname des PDF-Anhangs (ohne .pdf).</summary>
    public string FileName { get; set; } = "";
    /// <summary>Im Portal geändert – „Neu prüfen“ überschreibt die Versanddaten dann nicht.</summary>
    public bool Edited { get; set; }
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
    public int Pages { get; set; }

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
    public string? Stationery { get; set; }

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
        if (Status is VorgangStatus.Sent or VorgangStatus.Discarded or VorgangStatus.Sending) return;
        Status = ErrorCount > 0 ? VorgangStatus.Error
            : !Mail.Enabled ? VorgangStatus.NoMail
            : WarningCount > 0 ? VorgangStatus.Warning
            : VorgangStatus.Ready;
    }

    /// <summary>Kurzform für die Liste.</summary>
    public object Summary() => new
    {
        Id, CreatedAt, Source, Status, DocumentType, VoucherId, VoucherDate, BuyerId, BuyerName, GrossAmount, Currency,
        FormatName, To = Mail.To, Mail.Subject, MailEnabled = Mail.Enabled, ErrorCount, WarningCount, SentAt, SentBy, PrintUser,
        FirstMessage = Messages.FirstOrDefault(m => m.Level == DocumentChecker.Error)?.Text ?? Messages.FirstOrDefault(m => m.Level == DocumentChecker.Warning)?.Text
    };
}
