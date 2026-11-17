namespace PdfRechnung.Exchange;

/// <summary>
/// Versandweg Filialverbund: das ERP der Zentrale legt Import-/Exportdateien (.dta) in einen Ausgangsordner, Belegpost verteilt sie an
/// die Standorte (Filialen, Geräte). Dort holt der Filial-Client sie über die REST-API ab, importiert sie ins ERP und lädt Rückläufer hoch.
/// </summary>
public sealed class FilialverbundOptions
{
    public bool Enabled { get; set; }
    /// <summary>Ausgangsordner der Zentrale. Unterordner mit dem Kürzel eines Standorts oder einer Gruppe gehen an diese(n);
    /// Dateien direkt im Ordner werden nach den Regeln verteilt.</summary>
    public string OutFolder { get; set; } = "";
    /// <summary>Verteilte Dateien hierher verschieben (leer = Unterordner „verteilt“).</summary>
    public string OutArchive { get; set; } = "";
    /// <summary>Nur diese Dateien verteilen, z. B. „*.dta; *.dtx“. Leer = *.dta.</summary>
    public string Pattern { get; set; } = "";
    /// <summary>Rückläufer der Standorte ablegen (leer = daten\filialverbund\eingang).</summary>
    public string InFolder { get; set; } = "";
    /// <summary>Rückläufer in Unterordner je Standort ablegen.</summary>
    public bool InSubfolders { get; set; } = true;
    /// <summary>Standort gilt als offline, wenn sich sein Client so lange nicht gemeldet hat (Minuten).</summary>
    public int OfflineMinutes { get; set; } = 60;
    /// <summary>Datei gilt als hängengeblieben, wenn sie so lange nicht abgeholt bzw. quittiert wurde (Minuten).</summary>
    public int PendingMinutes { get; set; } = 120;
    public List<Standort> Standorte { get; set; } = new();
    public List<StandortGruppe> Gruppen { get; set; } = new();
    public List<VerteilRegel> Regeln { get; set; } = new();

    public Standort? Find(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : Standorte.FirstOrDefault(s => string.Equals(s.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Empfänger auflösen: Standort-Kürzel, Gruppenname oder „*“ (alle aktiven Standorte).</summary>
    public List<Standort> Resolve(IEnumerable<string> targets)
    {
        var result = new List<Standort>();
        void Add(Standort s) { if (s.Active && !result.Contains(s)) result.Add(s); }
        foreach (var raw in targets.Select(t => t.Trim()).Where(t => t.Length > 0))
        {
            if (raw == "*") { foreach (var s in Standorte) Add(s); continue; }
            if (Find(raw) is { } st) { Add(st); continue; }
            var g = Gruppen.FirstOrDefault(x => string.Equals(x.Name, raw, StringComparison.OrdinalIgnoreCase));
            if (g is not null) foreach (var m in g.Members) if (Find(m) is { } ms) Add(ms);
        }
        return result;
    }

    public static List<string> SplitList(string? s) =>
        (s ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

/// <summary>Ein Standort (Filiale, Gerät) mit eigenem Filial-Client.</summary>
public sealed class Standort
{
    /// <summary>Kürzel, z. B. „F01“ – auch Name des Unterordners im Ausgangsordner.</summary>
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
    /// <summary>Rückläufer dieses Standorts zusätzlich an diese Standorte/Gruppen weitergeben (z. B. Filiale → Filiale).</summary>
    public string ForwardTo { get; set; } = "";
    public string Note { get; set; } = "";
    /// <summary>Einstellungen des Filial-Clients – werden im Portal gepflegt und vom Client abgeholt.</summary>
    public ClientSettings Client { get; set; } = new();
}

/// <summary>Was der Filial-Client am Standort tut. Alles wird im Portal eingestellt; der Client kennt nur Portal-Adresse und Schlüssel.</summary>
public sealed class ClientSettings
{
    /// <summary>Programmordner des ERP am Standort, z. B. „C:\BueroWARE“ oder „\\server\erp“.</summary>
    public string ErpPath { get; set; } = "";
    /// <summary>auto | bw000632 | wwwin | bwwin – Importprogramm (auto: WWWIN64/WWWIN32, sonst BW000632).</summary>
    public string ImportProgram { get; set; } = "auto";
    /// <summary>wawi (IMPAUTO) oder fibu (FIBAUTO).</summary>
    public string Interface { get; set; } = "wawi";
    /// <summary>ERP-Benutzer (3-stellig) für den Import.</summary>
    public string ErpUser { get; set; } = "000";
    /// <summary>Kennwort des ERP-Benutzers (verschlüsselt gespeichert; WWWIN/BWWIN, Standard „!CW“).</summary>
    public string? ErpPassword { get; set; }
    public bool ErpHasPassword { get; set; }
    public string? ErpNewPassword { get; set; }
    /// <summary>Mandantennummer (leer = ohne /M:).</summary>
    public string Mandant { get; set; } = "";
    /// <summary>Belegzeitraum (nur WWWIN/BWWIN), leer = ohne.</summary>
    public string Period { get; set; } = "";
    /// <summary>Diese Dateien werden importiert, z. B. „*.dta“.</summary>
    public string ImportPattern { get; set; } = "*.dta";
    /// <summary>Nach dem Import prüfen, ob Sätze nicht übernommen wurden (Kennzeichen UEBER N in der Datei).</summary>
    public bool CheckImport { get; set; } = true;
    /// <summary>Sonderdateien: werden nicht importiert, sondern im ERP-Ordner ersetzt.</summary>
    public List<SpecialFileRule> SpecialFiles { get; set; } = new();
    /// <summary>Exporte des Standorts, die an die Zentrale (bzw. weitere Standorte) gehen.</summary>
    public List<ExportRule> Exports { get; set; } = new();
    /// <summary>Aufgaben mit eigenem Zeitplan, z. B. „Import alle 2 h“, „Import mit ERP beenden + Workflow um 19:00“.</summary>
    public List<ClientTask> Tasks { get; set; } = new();
    /// <summary>Wie oft der Client Einstellungen holt und sich meldet (Sekunden).</summary>
    public int PollSeconds { get; set; } = 60;
}

/// <summary>Eine Aufgabe des Filial-Clients mit eigenem Zeitplan.</summary>
public sealed class ClientTask
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Zeitplan wie bei den Versandwegen: intervall (alle n Minuten, Zeitfenster), zeiten (feste Uhrzeiten), Wochentage.</summary>
    public Delivery.Schedule Schedule { get; set; } = new() { Mode = Delivery.ScheduleEngine.Interval, Minutes = 120 };
    /// <summary>Bereitliegende Dateien abholen und ins ERP importieren.</summary>
    public bool Import { get; set; } = true;
    /// <summary>Nur diese Dateien (leer = Importmuster des Clients).</summary>
    public string ImportPattern { get; set; } = "";
    /// <summary>Schnittstelle für diese Aufgabe (leer = wie beim Client): wawi | fibu.</summary>
    public string Interface { get; set; } = "";
    /// <summary>Bereitliegende Sonderdateien im ERP-Ordner ersetzen.</summary>
    public bool SpecialFiles { get; set; } = true;
    /// <summary>Exporte des Standorts hochladen.</summary>
    public bool Export { get; set; } = true;
    /// <summary>Laufende ERP-Programme am Client vor dem Import bzw. Ersetzen beenden.</summary>
    public bool CloseErp { get; set; }
    /// <summary>Workflows und Auswertungen dieser Aufgabe.</summary>
    public List<WorkflowStep> Workflows { get; set; } = new();
}

public sealed class SpecialFileRule
{
    /// <summary>Dateiname bzw. Muster, z. B. „*.bwp“ oder „DESIGNER.DAT“.</summary>
    public string Pattern { get; set; } = "";
    /// <summary>Zielordner: relativ zum ERP-Ordner (z. B. „DATEN\VORLAGEN“) oder absolut. Leer = ERP-Ordner.</summary>
    public string Target { get; set; } = "";
    /// <summary>ERP vor dem Ersetzen beenden.</summary>
    public bool CloseErp { get; set; }
    /// <summary>Vorhandene Datei vorher als Sicherung behalten (*.bak_JJJJMMTT-HHMMSS).</summary>
    public bool Backup { get; set; } = true;
}

public sealed class ExportRule
{
    /// <summary>Ordner am Standort, in den das ERP die Exportdatei schreibt (absolut oder relativ zum ERP-Ordner).</summary>
    public string Folder { get; set; } = "";
    public string Pattern { get; set; } = "*.dta";
    /// <summary>Leer = an die Zentrale; sonst zusätzlich an diese Standorte/Gruppen.</summary>
    public string To { get; set; } = "";
}

public sealed class WorkflowStep
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    /// <summary>vorher (vor Import/Export) | nachher (nach dem Import) | nachErfolg (nur wenn der Import fehlerfrei war)</summary>
    public string When { get; set; } = "nachher";
    /// <summary>ERP-Programmteil, z. B. „AUSWERT“ (wie beim ERP-Aufruf).</summary>
    public string Program { get; set; } = "";
    /// <summary>Name des Workflows bzw. der Auswertung.</summary>
    public string Workflow { get; set; } = "";
    /// <summary>Freie Zusatzparameter (optional).</summary>
    public string Arguments { get; set; } = "";
}

public sealed class StandortGruppe
{
    public string Name { get; set; } = "";
    public List<string> Members { get; set; } = new();
}

/// <summary>Verteilregel für Dateien direkt im Ausgangsordner: Dateiname passt zum Muster → an diese Standorte/Gruppen.</summary>
public sealed class VerteilRegel
{
    public string Pattern { get; set; } = "";
    public string Targets { get; set; } = "";
}

public static class ExchangeStatus
{
    /// <summary>Server → Standort: wartet auf Abholung, abgeholt, vom Client importiert, Import fehlerhaft.</summary>
    public const string Waiting = "wartet", Fetched = "abgeholt", Imported = "importiert", Failed = "fehler";
    /// <summary>Standort → Server: Rückläufer abgelegt.</summary>
    public const string Stored = "abgelegt";
    /// <summary>Vom Admin erledigt (z. B. fehlerhaften Import manuell bereinigt).</summary>
    public const string Done = "erledigt";
    public const string Out = "aus", In = "ein";
}

/// <summary>Ein Eintrag im Datenaustausch (eine Datei für bzw. von einem Standort).</summary>
public sealed record ExchangeEntry(long Nr, DateTimeOffset At, string Direction, string Standort, string FileName, long Size, string Status,
    DateTimeOffset? StatusAt, string? Message, string? Source, string Stored, bool Exists, int Attempts);
