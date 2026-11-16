using PdfRechnung.Documents;

namespace PdfRechnung.Delivery;

/// <summary>Versandwege, die einem Kunden hinterlegt werden können.</summary>
public static class ChannelType
{
    public const string Mail = "mail", Ftp = "ftp", Ftps = "ftps", Sftp = "sftp", As2 = "as2", Gotomaxx = "gotomaxx", Exite = "exite";
    public static readonly string[] All = { Mail, Ftp, Ftps, Sftp, As2, Gotomaxx, Exite };

    public static string Text(string? t) => t switch
    {
        Mail => "E-Mail",
        Ftp => "FTP",
        Ftps => "FTPS",
        Sftp => "SFTP",
        As2 => "AS2",
        Gotomaxx => "gotomaxx-Drucker",
        Exite => "eXite (FTPS)",
        _ => t ?? ""
    };

    public static bool IsFileTransfer(string? t) => t is Ftp or Ftps or Sftp or Exite;
}

/// <summary>Was übertragen wird.</summary>
public static class ChannelContent
{
    /// <summary>
    /// pdf = Versand-PDF · xml = E-Rechnungs-XML · beide = PDF + XML · anlagen = die Anlagen des Belegs (@@ma@@/@@mc@@, z. B. EDI-, XML- oder andere Dateien)
    /// · pdf+anlagen · alles = PDF + XML + Anlagen.
    /// </summary>
    public const string Pdf = "pdf", Xml = "xml", Both = "beide", Attachments = "anlagen", PdfAttachments = "pdf+anlagen", Everything = "alles";
    public static readonly string[] All = { Pdf, Xml, Both, Attachments, PdfAttachments, Everything };
    public static string Text(string? c) => c switch
    {
        Xml => "E-Rechnungs-XML", Both => "PDF und XML", Attachments => "Anlagen des Belegs (EDI, XML, …)",
        PdfAttachments => "PDF und Anlagen", Everything => "PDF, XML und Anlagen", _ => "PDF"
    };
    public static bool HasPdf(string? c) => c is Pdf or Both or PdfAttachments or Everything or "" or null;
    public static bool HasXml(string? c) => c is Xml or Both or Everything;
    public static bool HasAttachments(string? c) => c is Attachments or PdfAttachments or Everything;
}

/// <summary>Abruf beim Mail-Versandweg: Postfach-Protokoll.</summary>
public static class MailProtocol
{
    public const string Imap = "imap", Imaps = "imaps", Pop3 = "pop3", Pop3s = "pop3s";
    public static readonly string[] All = { Imaps, Imap, Pop3s, Pop3 };
}

/// <summary>
/// Einstellungen eines Versandwegs. Global je Versandweg einmal; beim Kunden übersteuert jedes ausgefüllte Feld den globalen Wert
/// (leere Felder, 0 und „wie global“ = globaler Wert).
/// </summary>
public sealed class ChannelSettings
{
    public string Type { get; set; } = ChannelType.Mail;

    // ---- Inhalt ----
    /// <summary>pdf, xml, beide. Leer = global (Standard pdf).</summary>
    public string Content { get; set; } = "";
    /// <summary>Dateiname ohne Endung, Platzhalter wie beim Mail-Anhang. Leer = Dateiname des Belegs.</summary>
    public string FileName { get; set; } = "";
    /// <summary>Welche Anlagen mitgehen, z. B. „*.edi; *.xml“. Leer = alle.</summary>
    public string AttachmentPattern { get; set; } = "";

    // ---- Abruf (Abholung) ----
    /// <summary>„ja“/„nein“, leer = global (Standard nein).</summary>
    public string InboundEnabled { get; set; } = "";
    /// <summary>Wo die Dateien abgeholt werden: Ordner am Server bzw. Postfach-Ordner (IMAP, leer = INBOX).</summary>
    public string InboundFolder { get; set; } = "";
    /// <summary>Welche Dateien bzw. Mail-Anhänge, z. B. „*.xml; *.edi; *.pdf“. Leer = alle.</summary>
    public string InboundPattern { get; set; } = "";
    /// <summary>Zielordner für abgeholte Dateien (bei dir). Der Belegpost-Eingangsordner macht aus PDFs Vorgänge.</summary>
    public string InboundTarget { get; set; } = "";
    /// <summary>Danach am Server: loeschen, verschieben (in <see cref="InboundArchive"/>), behalten (merkt sich, was schon geholt ist).</summary>
    public string InboundAfter { get; set; } = "";
    public string InboundArchive { get; set; } = "";
    /// <summary>Abruf alle n Minuten (0 = global bzw. 5).</summary>
    public int InboundInterval { get; set; }
    /// <summary>Mail-Abruf: imaps, imap, pop3s, pop3. Server, Port, Benutzer und Kennwort wie oben.</summary>
    public string MailProtocol { get; set; } = "";

    // ---- Ausgangsordner (wie BWEDI „FTPLocalUploadPath“): Dateien aus einem Ordner bei dir verschicken ----
    /// <summary>Ordner bei dir, dessen Dateien (z. B. EDI/XML aus dem ERP) regelmäßig über diesen Versandweg gesendet werden. Leer = aus.</summary>
    public string OutboxFolder { get; set; } = "";
    /// <summary>Welche Dateien, z. B. „*.edi; *.xml“. Leer = alle.</summary>
    public string OutboxPattern { get; set; } = "";
    /// <summary>Gesendete Dateien hierhin verschieben. Leer = Unterordner „gesendet“.</summary>
    public string OutboxArchive { get; set; } = "";

    // ---- FTP / FTPS / SFTP / eXite ----
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string User { get; set; } = "";
    /// <summary>Kennwort (verschlüsselt gespeichert).</summary>
    public string? Password { get; set; }
    /// <summary>Zielordner auf dem Server, z. B. /eingang.</summary>
    public string Folder { get; set; } = "";
    /// <summary>FTPS: implizites TLS (Port 990) statt explizitem AUTH TLS.</summary>
    public bool ImplicitTls { get; set; }
    /// <summary>FTP/FTPS: aktiver Modus statt passiv.</summary>
    public bool ActiveMode { get; set; }
    /// <summary>Erwarteter Fingerabdruck: SFTP-Hostschlüssel (SHA256:…) bzw. FTPS-Zertifikat (SHA-256 hex). Leer = nur gültige Zertifikate / jeder Hostschlüssel.</summary>
    public string Fingerprint { get; set; } = "";
    /// <summary>eXite: Datenformat im Befehl typ(): X = XML (ebInterface), E = EDIFACT. Leer = global bzw. X.</summary>
    public string ExiteType { get; set; } = "";
    /// <summary>eXite: Nachrichtenklasse umc() (optional).</summary>
    public string ExiteMessageClass { get; set; } = "";
    /// <summary>eXite: Empfangsbestätigung ack() anfordern („ja“/„nein“, leer = global/nein).</summary>
    public string ExiteAck { get; set; } = "";
    /// <summary>Erst unter temporärem Namen hochladen und danach umbenennen (der Empfänger sieht nie halbe Dateien).</summary>
    public bool UploadAsTemp { get; set; } = true;

    // ---- E-Mail ----
    /// <summary>Empfänger, ersetzt die Adressen aus dem Druck. Leer = wie im Beleg.</summary>
    public List<string> To { get; set; } = new();
    public List<string> Cc { get; set; } = new();
    public List<string> Bcc { get; set; } = new();
    /// <summary>Mail-Vorlage für diesen Kunden. Leer = wie ermittelt.</summary>
    public string Template { get; set; } = "";

    // ---- AS2 ----
    public string Url { get; set; } = "";
    public string As2From { get; set; } = "";
    public string As2To { get; set; } = "";
    /// <summary>Eigenes Zertifikat mit privatem Schlüssel (.pfx) zum Signieren, Pfad am Server.</summary>
    public string OwnCertificate { get; set; } = "";
    public string? OwnCertificatePassword { get; set; }
    /// <summary>Zertifikat des Partners (.cer) zum Verschlüsseln.</summary>
    public string PartnerCertificate { get; set; } = "";
    /// <summary>„ja“/„nein“, leer = global (Standard ja).</summary>
    public string Sign { get; set; } = "";
    public string Encrypt { get; set; } = "";
    /// <summary>Synchrone Empfangsbestätigung (MDN) anfordern.</summary>
    public string RequestMdn { get; set; } = "";

    // ---- gotomaxx ----
    public string Printer { get; set; } = "";

    // ---- nur Oberfläche, nie gespeichert ----
    public bool HasPassword { get; set; }
    public string? NewPassword { get; set; }
    public bool ClearPassword { get; set; }
    public bool HasCertificatePassword { get; set; }
    public string? NewCertificatePassword { get; set; }

    /// <summary>Globale Einstellungen mit den ausgefüllten Feldern des Kunden überschreiben.</summary>
    public static ChannelSettings Merge(ChannelSettings? global, ChannelSettings own)
    {
        var g = global ?? new ChannelSettings { Type = own.Type };
        static string S(string a, string b) => string.IsNullOrWhiteSpace(a) ? b : a.Trim();
        static List<string> L(List<string> a, List<string> b) => a.Any(x => !string.IsNullOrWhiteSpace(x)) ? a.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList() : b;
        var ownLogin = !string.IsNullOrWhiteSpace(own.User);
        return new ChannelSettings
        {
            Type = own.Type,
            Content = S(own.Content, S(g.Content, own.Type == ChannelType.Exite ? ChannelContent.Xml : ChannelContent.Pdf)),
            FileName = S(own.FileName, g.FileName),
            Host = S(own.Host, g.Host), Port = own.Port > 0 ? own.Port : g.Port,
            User = ownLogin ? own.User.Trim() : g.User, Password = ownLogin ? own.Password : g.Password,
            Folder = S(own.Folder, g.Folder),
            ImplicitTls = own.ImplicitTls || g.ImplicitTls, ActiveMode = own.ActiveMode || g.ActiveMode,
            Fingerprint = S(own.Fingerprint, g.Fingerprint),
            ExiteType = S(own.ExiteType, S(g.ExiteType, "X")), ExiteMessageClass = S(own.ExiteMessageClass, g.ExiteMessageClass),
            ExiteAck = S(own.ExiteAck, S(g.ExiteAck, "nein")),
            UploadAsTemp = own.UploadAsTemp && g.UploadAsTemp,
            To = L(own.To, g.To), Cc = L(own.Cc, g.Cc), Bcc = L(own.Bcc, g.Bcc), Template = S(own.Template, g.Template),
            Url = S(own.Url, g.Url), As2From = S(own.As2From, g.As2From), As2To = S(own.As2To, g.As2To),
            OwnCertificate = S(own.OwnCertificate, g.OwnCertificate),
            OwnCertificatePassword = string.IsNullOrWhiteSpace(own.OwnCertificate) ? g.OwnCertificatePassword : own.OwnCertificatePassword,
            PartnerCertificate = S(own.PartnerCertificate, g.PartnerCertificate),
            Sign = S(own.Sign, S(g.Sign, "ja")), Encrypt = S(own.Encrypt, S(g.Encrypt, "ja")), RequestMdn = S(own.RequestMdn, S(g.RequestMdn, "ja")),
            Printer = S(own.Printer, g.Printer),
            AttachmentPattern = S(own.AttachmentPattern, g.AttachmentPattern),
            InboundEnabled = S(own.InboundEnabled, S(g.InboundEnabled, "nein")), InboundFolder = S(own.InboundFolder, g.InboundFolder),
            InboundPattern = S(own.InboundPattern, g.InboundPattern), InboundTarget = S(own.InboundTarget, g.InboundTarget),
            InboundAfter = S(own.InboundAfter, S(g.InboundAfter, "loeschen")), InboundArchive = S(own.InboundArchive, g.InboundArchive),
            InboundInterval = own.InboundInterval > 0 ? own.InboundInterval : g.InboundInterval > 0 ? g.InboundInterval : 5,
            MailProtocol = S(own.MailProtocol, S(g.MailProtocol, PdfRechnung.Delivery.MailProtocol.Imaps)),
            OutboxFolder = S(own.OutboxFolder, g.OutboxFolder), OutboxPattern = S(own.OutboxPattern, g.OutboxPattern),
            OutboxArchive = S(own.OutboxArchive, g.OutboxArchive)
        };
    }

    /// <summary>Für die Oberfläche: Geheimnisse entfernen, nur „vorhanden“ melden.</summary>
    public void Redact()
    {
        HasPassword = !string.IsNullOrEmpty(Password); Password = null;
        HasCertificatePassword = !string.IsNullOrEmpty(OwnCertificatePassword); OwnCertificatePassword = null;
        NewPassword = null; NewCertificatePassword = null; ClearPassword = false;
    }

    /// <summary>Beim Speichern: neue Geheimnisse verschlüsseln, sonst die bisherigen behalten.</summary>
    public void TakeSecrets(ChannelSettings? previous, Func<string, string?> protect)
    {
        Password = ClearPassword ? null : !string.IsNullOrEmpty(NewPassword) ? protect(NewPassword) : previous?.Password;
        OwnCertificatePassword = ClearPassword ? null : !string.IsNullOrEmpty(NewCertificatePassword) ? protect(NewCertificatePassword) : previous?.OwnCertificatePassword;
        NewPassword = null; NewCertificatePassword = null; ClearPassword = false; HasPassword = false; HasCertificatePassword = false;
    }
}

/// <summary>Globale Einstellungen der Versandwege (Einstellungen → Versandwege).</summary>
public sealed class DeliveryOptions
{
    public List<ChannelSettings> Channels { get; set; } = new();

    public ChannelSettings? Global(string type) => Channels.FirstOrDefault(c => c.Type == type);
}

/// <summary>Ein Versandweg des Kunden.</summary>
public sealed class CustomerChannel
{
    /// <summary>Feste Kennung (für Regeln).</summary>
    public string Id { get; set; } = "";
    /// <summary>Anzeigename, z. B. „Buchhaltung per Mail“. Leer = Art des Versandwegs.</summary>
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Art und eigene Einstellungen (leere Felder = global).</summary>
    public ChannelSettings Settings { get; set; } = new();

    public string Label => string.IsNullOrWhiteSpace(Name) ? ChannelType.Text(Settings.Type) : Name.Trim();
}

/// <summary>
/// Regel beim Kunden: Bedingungen wie bei den Mail-Regeln → welche Versandwege dieses Kunden verwendet werden.
/// Die erste passende Regel gilt. Passt keine, werden alle aktiven Versandwege verwendet.
/// </summary>
public sealed class CustomerRule
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public List<string> DocumentTypes { get; set; } = new();
    public List<string> Formats { get; set; } = new();
    public List<string> Sources { get; set; } = new();
    public List<string> Stationeries { get; set; } = new();
    public List<string> Recipients { get; set; } = new();
    public string TextContains { get; set; } = "";
    /// <summary>Kennungen der Versandwege. Leer = alle aktiven.</summary>
    public List<string> Channels { get; set; } = new();
    /// <summary>Für diese Belege nichts senden (Vorgang „ohne Versand“).</summary>
    public bool NoDelivery { get; set; }

    public MailRule AsMailRule() => new()
    {
        Name = Name, Enabled = Enabled, DocumentTypes = DocumentTypes, Formats = Formats, Sources = Sources,
        Stationeries = Stationeries, Recipients = Recipients, TextContains = TextContains
    };
}

/// <summary>Kunde mit eigenen Versandwegen und Regeln.</summary>
public sealed class Customer
{
    public string Id { get; set; } = "";
    /// <summary>Kundennummer wie im ERP (@@xi BUYER_ID=…).</summary>
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Nur für diesen Mandanten. Leer = alle.</summary>
    public string Mandant { get; set; } = "";
    /// <summary>E-Mail-Adressen zur Erkennung (auch *@kunde.at), falls der Beleg keine Kundennummer hat.</summary>
    public List<string> Emails { get; set; } = new();
    public bool Active { get; set; } = true;
    public string Note { get; set; } = "";
    public List<CustomerChannel> Channels { get; set; } = new();
    public List<CustomerRule> Rules { get; set; } = new();

    public string Label => string.IsNullOrWhiteSpace(Name) ? Number : string.IsNullOrWhiteSpace(Number) ? Name : $"{Number} – {Name}";
}

/// <summary>Ergebnis eines Versandwegs bei einem Vorgang.</summary>
public sealed class VorgangDelivery
{
    public string ChannelId { get; set; } = "";
    public string Type { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>offen, ok, fehler.</summary>
    public string Status { get; set; } = "offen";
    public string? Target { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset? At { get; set; }
    public int Attempts { get; set; }
}

/// <summary>Inhaltstyp einer Datei nach der Endung.</summary>
public static class MimeTypes
{
    public static string For(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf", ".xml" => "application/xml", ".edi" or ".edifact" or ".x12" => "application/edifact",
        ".txt" or ".csv" => "text/plain", ".json" => "application/json", ".zip" => "application/zip",
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg",
        _ => "application/octet-stream"
    };
}
