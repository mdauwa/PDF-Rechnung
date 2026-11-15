namespace PdfRechnung;

/// <summary>Gesamte Konfiguration, wie sie in settings.json gespeichert und in der Oberfläche bearbeitet wird.</summary>
public sealed class AppSettings
{
    public ServerOptions Server { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
    public PortalOptions Portal { get; set; } = new();
    public InboxOptions Inbox { get; set; } = new();
    public MailOptions Mail { get; set; } = new();
    public InvoiceOptions Invoice { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public StationeryOptions Stationery { get; set; } = new();
    public MailTemplateOptions MailTemplates { get; set; } = new();
    public StatusLinkOptions StatusLink { get; set; } = new();
    public ErpOptions Erp { get; set; } = new();
    public MapiOptions Mapi { get; set; } = new();
    public GotomaxxOptions Gotomaxx { get; set; } = new();
}

/// <summary>
/// Öffentlicher Status-Link /s/&lt;token&gt; (ohne Anmeldung, nur aus freigegebenen IP-Bereichen).
/// Zeigt nur Belegart/-nummer und den Zeitstrahl – keine Empfänger, Beträge oder Inhalte.
/// </summary>
public sealed class StatusLinkOptions
{
    /// <summary>Freigegebene IP-Bereiche (CIDR, z. B. 192.168.10.0/24) oder einzelne Adressen. Leer = Status-Link aus.</summary>
    public List<string> AllowedNetworks { get; set; } = new();
    /// <summary>Reverse-Proxys, deren X-Forwarded-For übernommen wird (einzelne Adressen oder CIDR).</summary>
    public List<string> TrustedProxies { get; set; } = new();
    /// <summary>Link gilt so viele Tage nach dem letzten Ereignis. 0 = unbegrenzt.</summary>
    public int ExpiryDays { get; set; } = 180;
    /// <summary>Belegart und Belegnummer anzeigen.</summary>
    public bool ShowVoucherNumber { get; set; } = true;
    public bool Enabled => AllowedNetworks.Any(n => !string.IsNullOrWhiteSpace(n));
}

/// <summary>Was mit E-Mails passiert, die das ERP über MAPI (BelegpostMapi.dll) übergibt.</summary>
public static class MapiMode
{
    public const string Inherit = "", Outlook = "outlook", Portal = "portal", Direct = "direkt";
    public static readonly string[] All = { Outlook, Portal, Direct };
    public static string Text(string? m) => m switch { Outlook => "in Outlook öffnen", Portal => "im Portal prüfen", Direct => "direkt über den Dienst senden", _ => "erben" };
}

public sealed class MapiOptions
{
    /// <summary>Standard für alle Benutzer ohne eigene Vorgabe.</summary>
    public string DefaultMode { get; set; } = MapiMode.Outlook;
    /// <summary>Outlook-Auftrag, den kein Melder abholt, nach so vielen Stunden aufgeben (Beleg bleibt im Portal). </summary>
    public int DeliveryTimeoutHours { get; set; } = 8;
    /// <summary>Ohne Versandbestätigung aus Outlook nach so vielen Stunden Status 830.</summary>
    public int OutlookConfirmHours { get; set; } = 24;
}

/// <summary>ERP-Rückmeldung über WEBWARE-Webservices (WWSVC): Status per BELEG.PUT in den Belegkopf.</summary>
public sealed class ErpOptions
{
    public bool Enabled { get; set; }
    /// <summary>HTTPS-Basisadresse mit abschließendem /, z. B. https://erp.firma.at/WWSVC/ (FQDN passend zum Zertifikat).</summary>
    public string Endpoint { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Application { get; set; } = "";
    /// <summary>SecuredApp-Zugriffs-ID (eigene für Belegpost – nicht die eines anderen Dienstes).</summary>
    public string SecureAppId { get; set; } = "";
    public int Revision { get; set; }
    /// <summary>Optionaler technischer ERP-Benutzer.</summary>
    public string Username { get; set; } = "";
    /// <summary>Kennwort (verschlüsselt gespeichert).</summary>
    public string? Password { get; set; }
    /// <summary>Darf Belegpost selbst einen Servicepass registrieren?</summary>
    public bool AllowRegistration { get; set; }
    /// <summary>Vor dem Schreiben mit BELEG.GET prüfen (Beleg vorhanden, „nur wenn leer“, schon aktuell).</summary>
    public bool CheckBeforeWrite { get; set; } = true;
    /// <summary>Nach so vielen Tagen ohne Erfolg gilt ein Auftrag als dauerhaft fehlgeschlagen.</summary>
    public int GiveUpDays { get; set; } = 3;
    /// <summary>Zuordnung ERP-Feld → Wert.</summary>
    public List<ErpFieldMapping> Fields { get; set; } = new();
    /// <summary>Statuscodes: welcher Wert ins ERP geschrieben wird und ob überhaupt.</summary>
    public List<ErpStatusCode> Codes { get; set; } = new();

    public bool IsConfigured => Enabled && Endpoint.Length > 0 && Vendor.Length > 0 && Application.Length > 0 && SecureAppId.Length > 0;
}

public sealed class ErpFieldMapping
{
    /// <summary>ERP-Feldname, z. B. BEL_3_8 (Länge = Zahl nach dem letzten _).</summary>
    public string Field { get; set; } = "";
    /// <summary>Welcher Wert (siehe ErpValues.All).</summary>
    public string Value { get; set; } = "statuscode";
    /// <summary>Formatangabe (Datumsformat) bzw. Text/Vorlage bei „fest“ und „vorlage“.</summary>
    public string Format { get; set; } = "";
    /// <summary>Länge von Hand (wenn der Feldname kein _&lt;Länge&gt; trägt). 0 = aus dem Feldnamen.</summary>
    public int Length { get; set; }
    /// <summary>Nur bei diesen Statuscodes schreiben; leer = bei allen.</summary>
    public List<string> OnlyCodes { get; set; } = new();
    /// <summary>Nur schreiben, wenn das Feld im ERP leer ist.</summary>
    public bool OnlyIfEmpty { get; set; }
}

public sealed class ErpStatusCode
{
    public string Code { get; set; } = "";
    /// <summary>Wert im ERP (3-stellig); leer = wie Code.</summary>
    public string ErpValue { get; set; } = "";
    public bool Write { get; set; } = true;
}

public sealed class PortalOptions
{
    public string Title { get; set; } = "Belegpost";
    /// <summary>Adresse des Portals für Links (Status-Link), z. B. https://belegpost.firma.at:5444. Leer = erster Hostname.</summary>
    public string PublicUrl { get; set; } = "";
}

public sealed class AuthOptions
{
    /// <summary>Anmeldung der Benutzer mit Microsoft Entra ID (Microsoft-365-Konto).</summary>
    public EntraOptions Entra { get; set; } = new();
    /// <summary>Anmeldung aus dem Firmenportal übernehmen und Einbettung dort erlauben.</summary>
    public FirmenportalOptions Firmenportal { get; set; } = new();
}

/// <summary>
/// Einbindung in das interne Firmenportal: Das Firmenportal übergibt die Anmeldung mit einem kurzlebigen,
/// signierten Token (HS256, gemeinsamer Schlüssel) an /auth/firmenportal und darf Belegpost in einem Rahmen anzeigen.
/// </summary>
public sealed class FirmenportalOptions
{
    public bool Enabled { get; set; }
    /// <summary>Adresse des Firmenportals, z. B. https://firmenportal.firma.at:5443 – erlaubt die Einbettung (frame-ancestors).</summary>
    public string Url { get; set; } = "";
    /// <summary>Gemeinsamer Schlüssel (verschlüsselt gespeichert, Präfix "enc:").</summary>
    public string? Secret { get; set; }
    /// <summary>Unbekannte, aber im Token bestätigte Benutzer automatisch anlegen (Sicht: nur eigene Belege).</summary>
    public bool CreateUsers { get; set; }
}

/// <summary>App-Registrierung in Microsoft Entra ID (Umleitungs-URI: https://SERVER:PORT/signin-oidc).</summary>
public sealed class EntraOptions
{
    public bool Enabled { get; set; }
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    /// <summary>Geheimer Clientschlüssel (verschlüsselt gespeichert, Präfix "enc:").</summary>
    public string? ClientSecret { get; set; }
    /// <summary>Optional: abweichende Authority (nur für Tests).</summary>
    public string? Authority { get; set; }

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ClientId)
        && (!string.IsNullOrWhiteSpace(TenantId) || !string.IsNullOrWhiteSpace(Authority));
}

/// <summary>Webserver (Kestrel) und Zertifikat.</summary>
public sealed class ServerOptions
{
    public int HttpsPort { get; set; } = 5444;
    public bool EnableHttp { get; set; } = true;
    public int HttpPort { get; set; } = 5081;
    public bool RedirectHttpToHttps { get; set; } = true;
    /// <summary>Pfad zu einer PFX-Datei. Leer = selbst signiertes Zertifikat wird automatisch erzeugt.</summary>
    public string CertificatePath { get; set; } = "";
    /// <summary>Passwort der PFX-Datei (verschlüsselt gespeichert, Präfix "enc:").</summary>
    public string? CertificatePassword { get; set; }
    /// <summary>Weitere DNS-Namen für das selbst signierte Zertifikat.</summary>
    public string[] AdditionalHostNames { get; set; } = Array.Empty<string>();
    /// <summary>
    /// IP-Adressen, an die das Portal gebunden wird. Leer = alle Adressen des Servers.
    /// 127.0.0.1 ist immer dabei (Einrichtung und Administrator-Anmeldung am Server).
    /// </summary>
    public List<string> BindAddresses { get; set; } = new();
    /// <summary>Firewall-Regel auch für das Netzwerkprofil „Öffentlich“ (Standard: nur Domäne und Privat).</summary>
    public bool FirewallPublic { get; set; }
}

/// <summary>
/// Eingang: Hierher legt der Drucker (Microsoft Print to PDF mit eigenem Anschluss) die gedruckten PDFs.
/// Jede PDF-Datei wird zu einem Vorgang verarbeitet und danach aus dem Ordner entfernt.
/// </summary>
/// <summary>Weiterleitung an einen gotomaxx-PDFMAILER-Drucker (Einstellungen → Drucker &amp; Eingang).</summary>
public sealed class GotomaxxOptions
{
    public bool Enabled { get; set; }
    /// <summary>Standarddrucker für @@pg@@ und „immer weiterleiten“; @@pgName@@ wählt einen anderen.</summary>
    public string Printer { get; set; } = "";
    /// <summary>Jeden Beleg weiterleiten (außer mit @@pg0@@) – sonst nur Belege mit @@pg@@.</summary>
    public bool Always { get; set; }
    /// <summary>In der Windows-Sitzung des Benutzers am Server drucken (gotomaxx verarbeitet den Auftrag dort wie einen ERP-Druck).</summary>
    public bool UserSession { get; set; } = true;
    /// <summary>Ist der Benutzer nicht angemeldet, druckt der Dienst selbst.</summary>
    public bool ServiceFallback { get; set; } = true;
    /// <summary>Schriftgröße der neu gedruckten Steuerzeichen (Punkt).</summary>
    public double CodeFontSize { get; set; } = 2;
    /// <summary>Steuerzeichen schwarz statt weiß drucken (zum Testen).</summary>
    public bool CodesVisible { get; set; }
    /// <summary>Höchstdauer eines Druckauftrags in Sekunden.</summary>
    public int TimeoutSeconds { get; set; } = 120;
}

public sealed class InboxOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Leer = Unterordner "eingang" im Datenverzeichnis.</summary>
    public string Folder { get; set; } = "";
    /// <summary>Name des Druckers (für die spätere Einrichtung über die Oberfläche).</summary>
    public string PrinterName { get; set; } = "Belegpost";
    /// <summary>
    /// Pfade aus den Steuerzeichen (@@mb@@, @@ma@@ …) umsetzen, z. B. "K:\=\\server\WebWARE5\".
    /// Das ERP druckt Pfade, wie sie am Arbeitsplatz gelten – der Dienst läuft als SYSTEM und sieht keine Netzlaufwerke.
    /// </summary>
    public List<string> PathMappings { get; set; } = new();
}

public sealed class MailOptions
{
    public bool Enabled { get; set; }
    /// <summary>Versandart: "Smtp" oder "Graph" (Microsoft 365 / Exchange Online über Microsoft Graph).</summary>
    public string Transport { get; set; } = "Smtp";

    // ---- Microsoft Graph (App-Registrierung mit Anwendungsberechtigung Mail.Send) ----
    public string GraphTenantId { get; set; } = "";
    public string GraphClientId { get; set; } = "";
    /// <summary>Geheimer Clientschlüssel (verschlüsselt gespeichert, Präfix "enc:").</summary>
    public string? GraphClientSecret { get; set; }
    /// <summary>Versendete Mails im Ordner „Gesendete Elemente“ des Absenderpostfachs ablegen.</summary>
    public bool GraphSaveToSentItems { get; set; } = true;

    // ---- SMTP ----
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    /// <summary>Auto, StartTls, SslOnConnect, None.</summary>
    public string Security { get; set; } = "Auto";
    public string User { get; set; } = "";
    /// <summary>SMTP-Kennwort (verschlüsselt gespeichert, Präfix "enc:").</summary>
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "";
    public string ReplyTo { get; set; } = "";
    /// <summary>Kopie jeder versendeten Mail (BCC), z. B. an die Buchhaltung.</summary>
    public string CopyTo { get; set; } = "";
    /// <summary>Standardbetreff, wenn kein @@ed@@ gedruckt wurde. Platzhalter siehe <see cref="Documents.MailTemplate"/>.</summary>
    public string DefaultSubject { get; set; } = "{Belegart} {Belegnummer}";
    public string DefaultBody { get; set; } = "Sehr geehrte Damen und Herren,\n\nanbei erhalten Sie {Belegart} {Belegnummer} vom {Belegdatum}.\n\nMit freundlichen Grüßen";
    /// <summary>Dateiname des PDF-Anhangs, wenn kein @@fn@@ gedruckt wurde.</summary>
    public string DefaultFileName { get; set; } = "{Belegart}_{Belegnummer}";

    /// <summary>Korrekte Vorgänge (Status „bereit“) automatisch versenden.</summary>
    public bool AutoSend { get; set; }
    /// <summary>Wartezeit vor dem automatischen Versand (Minuten), damit noch eingegriffen werden kann.</summary>
    public int AutoSendDelayMinutes { get; set; } = 5;
    /// <summary>Auch Vorgänge mit Warnungen automatisch versenden (Fehler nie).</summary>
    public bool AutoSendWithWarnings { get; set; }
}

public sealed class InvoiceOptions
{
    /// <summary>Toleranz für die Summenprüfung (Betrag in Belegwährung).</summary>
    public decimal SumTolerance { get; set; } = 0.02m;
    /// <summary>Mengeneinheit aus dem ERP → UN/ECE-Code (Rec. 20), z. B. "Stk" → "H87".</summary>
    public Dictionary<string, string> Units { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class StorageOptions
{
    /// <summary>Vorgänge nach so vielen Tagen löschen. 0 = nie.</summary>
    public int RetentionDays { get; set; } = 400;
    /// <summary>Zusätzlich versendete PDFs/XML hierhin kopieren (z. B. Archiv/DMS). Leer = aus.</summary>
    public string ExportFolder { get; set; } = "";
    /// <summary>Tägliche Datenbanksicherungen aufbewahren (Anzahl). 0 = keine Sicherung.</summary>
    public int DatabaseBackups { get; set; } = 14;
}

/// <summary>Briefpapiere (z. B. je Firma). Auswahl per Steuerzeichen @@bpName@@, sonst das Standard-Briefpapier.</summary>
public sealed class StationeryOptions
{
    public List<Stationery> Items { get; set; } = new();
    /// <summary>Name des Standard-Briefpapiers (ohne @@bp@@). Leer = kein Briefpapier.</summary>
    public string Default { get; set; } = "";
}

public sealed class Stationery
{
    /// <summary>Name wie im Steuerzeichen @@bpName@@ (Groß-/Kleinschreibung wird beachtet).</summary>
    public string Name { get; set; } = "";
    /// <summary>PDF für die erste Seite (Datei im Ordner „briefpapier“ des Datenverzeichnisses).</summary>
    public string FirstPageFile { get; set; } = "";
    /// <summary>Folgeseiten: "same" = wie erste Seite, "file" = eigenes PDF, "none" = kein Briefpapier.</summary>
    public string FollowingMode { get; set; } = "same";
    public string FollowingPageFile { get; set; } = "";

    /// <summary>Druckbereich: Ränder in mm, in die der gedruckte Inhalt nicht ragen soll (Kopf, Fuß, Seiten).</summary>
    public double MarginTop { get; set; }
    public double MarginBottom { get; set; }
    public double MarginLeft { get; set; }
    public double MarginRight { get; set; }
    /// <summary>Folgeseiten mit eigenen Rändern (z. B. kleinerer Kopf). false = wie erste Seite.</summary>
    public bool FollowingOwnMargins { get; set; }
    public double FollowingMarginTop { get; set; }
    public double FollowingMarginBottom { get; set; }
    /// <summary>Gedruckten Inhalt verkleinern und in den Druckbereich verschieben, damit er Kopf/Fuß nicht überdeckt.</summary>
    public bool FitContent { get; set; }

    /// <summary>Feinjustierung des Briefpapiers: Versatz in mm und Skalierung in %.</summary>
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double Scale { get; set; } = 100;
}

/// <summary>
/// HTML-Mailvorlagen im CI des Kunden. Auswahl: @@pt Name@@ → Vorlage des Briefpapiers → Vorlage des Absenders → Standard.
/// </summary>
public sealed class MailTemplateOptions
{
    public List<MailDesign> Items { get; set; } = new();
    /// <summary>Standardvorlage. Leer = einfache Mail ohne Gestaltung.</summary>
    public string Default { get; set; } = "";
}

public sealed class MailDesign
{
    public string Name { get; set; } = "";
    /// <summary>"simple" = Baukasten (Logo, Farben, Texte), "html" = eigenes HTML mit Platzhaltern.</summary>
    public string Kind { get; set; } = "simple";

    // ---- Zuordnung ----
    /// <summary>Gilt für diese Briefpapiere (Namen wie in @@bpName@@).</summary>
    public List<string> Stationeries { get; set; } = new();
    /// <summary>Gilt für diese Absenderadressen (@@pf@@ oder Absender aus den Einstellungen).</summary>
    public List<string> Senders { get; set; } = new();

    // ---- Absender (leer = aus den Einstellungen) ----
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "";
    public string ReplyTo { get; set; } = "";

    // ---- Texte (leer = Vorlagen aus „E-Mail & Versand“) ----
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";

    // ---- Gestaltung (Baukasten) ----
    public string LogoFile { get; set; } = "";
    public int LogoWidth { get; set; } = 180;
    /// <summary>left, center, right</summary>
    public string LogoAlign { get; set; } = "left";
    public string PrimaryColor { get; set; } = "#1f5fbf";
    public string BackgroundColor { get; set; } = "#f4f5f7";
    public string TextColor { get; set; } = "#1c2330";
    public string FontFamily { get; set; } = "Segoe UI, Arial, Helvetica, sans-serif";
    /// <summary>Optionaler Balken in der Hauptfarbe über dem Text (z. B. „Ihre Rechnung {Belegnummer}“).</summary>
    public string HeaderText { get; set; } = "";
    /// <summary>Infokasten mit Belegart, Nummer, Datum, Betrag und Anhang.</summary>
    public bool ShowDocumentBox { get; set; } = true;
    public string Signature { get; set; } = "";
    public string Footer { get; set; } = "";

    /// <summary>Eigenes HTML (Kind = "html") mit Platzhaltern wie {Inhalt}, {Logo}, {Signatur}, {Fusszeile}, {Belegnummer} …</summary>
    public string CustomHtml { get; set; } = "";
}
