using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using PdfRechnung.Documents;

namespace PdfRechnung.Printing;

/// <summary>
/// Trennzeichen in gotomaxx-Druckern setzen (Einstellungen → gotomaxx → „Trennzeichen in gotomaxx-Druckern“), z. B. $$ für einen
/// PDFMAILER-Drucker, der an Belegpost weiterleitet und die @@-Steuerzeichen deshalb nicht selbst auswerten soll.
///
/// gotomaxx speichert die Einstellungen je Benutzer in <c>%APPDATA%\gotomaxx\pdfmailer6\profiles\&lt;Profil-ID&gt;.cfg</c>.
/// Der Dienst (SYSTEM) trägt <c>commandDelimiter</c> in alle Benutzerprofile am Server ein – auch ins Standardprofil
/// (C:\Users\Default), damit neue Benutzer es bei der ersten Anmeldung mitbekommen. „Standard“ entfernt den Eintrag wieder.
/// Läuft beim Start, nach dem Speichern und alle 10 Minuten (für Benutzer, die gotomaxx erst später benutzen).
/// </summary>
public sealed class GotomaxxDelimiterService : BackgroundService
{
    public sealed record PrinterStatus(string Printer, string Delimiter, List<string> ProfileIds, int Files, int Changed, List<string> Errors, string? Note);
    public sealed record Status(DateTimeOffset At, List<PrinterStatus> Printers, string? Error);

    private readonly IOptionsMonitor<GotomaxxOptions> _opt;
    private readonly IOptionsMonitor<GotomaxxLogOptions> _logOpt;
    private readonly ILogger<GotomaxxDelimiterService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);

    public Status? Last { get; private set; }

    public GotomaxxDelimiterService(IOptionsMonitor<GotomaxxOptions> opt, IOptionsMonitor<GotomaxxLogOptions> logOpt, ILogger<GotomaxxDelimiterService> log)
    {
        _opt = opt; _logOpt = logOpt; _log = log;
        _opt.OnChange(_ => Wake());
    }

    private void Wake() { try { if (_wake.CurrentCount == 0) _wake.Release(); } catch (SemaphoreFullException) { } }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { if (_opt.CurrentValue.PrinterDelimiters.Count > 0) Apply(); }
            catch (Exception ex) { _log.LogWarning(ex, "gotomaxx-Trennzeichen"); }
            try { await _wake.WaitAsync(TimeSpan.FromMinutes(10), ct); await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public Status Apply()
    {
        _gate.Wait();
        try
        {
            var rows = _opt.CurrentValue.PrinterDelimiters.Where(r => !string.IsNullOrWhiteSpace(r.Printer)).ToList();
            if (!OperatingSystem.IsWindows())
                return Last = new Status(DateTimeOffset.Now, rows.Select(r => new PrinterStatus(r.Printer, Shown(r.Delimiter), new(), 0, 0, new(), "Nur unter Windows.")).ToList(), null);
            var usageFolder = _logOpt.CurrentValue.FolderOrDefault;
            var version = Path.GetFileName(usageFolder.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(version) || !version.StartsWith("pdfmailer", StringComparison.OrdinalIgnoreCase)) version = "pdfmailer6";
            var profiles = UserProfiles();
            var list = new List<PrinterStatus>();
            foreach (var r in rows) list.Add(ApplyPrinter(r, usageFolder, version, profiles));
            var st = new Status(DateTimeOffset.Now, list, null);
            Last = st;
            foreach (var p in list.Where(p => p.Changed > 0))
                _log.LogInformation("gotomaxx-Trennzeichen {Printer}: {Delimiter} in {Changed} Profil(en) eingetragen", p.Printer, p.Delimiter, p.Changed);
            return st;
        }
        catch (Exception ex)
        {
            return Last = new Status(DateTimeOffset.Now, Last?.Printers ?? new(), ex.Message);
        }
        finally { _gate.Release(); }
    }

    private static string Shown(string? d) => string.IsNullOrWhiteSpace(d) || d.Trim() == GotomaxxProfile.Default ? "Standard (@@)" : d.Trim();

    [SupportedOSPlatform("windows")]
    private PrinterStatus ApplyPrinter(GotomaxxPrinterDelimiter r, string usageFolder, string version, List<(string Name, string AppData, bool IsDefault)> profiles)
    {
        var printer = r.Printer.Trim();
        var delimiter = (r.Delimiter ?? "").Trim();
        if (delimiter == GotomaxxProfile.Default) delimiter = "";
        var errors = new List<string>();
        // Sichere Profil-IDs: von Hand eingetragen oder aus dem gotomaxx-Protokoll (Druckername) – dort darf eine Datei neu angelegt werden.
        var sure = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.ProfileId)) sure.Add(r.ProfileId.Trim().ToLowerInvariant());
        try { foreach (var id in GotomaxxProfile.IdsFromUsage(usageFolder, printer, Path.Combine(Path.GetTempPath(), "belegpost-gotomaxx"))) if (!sure.Contains(id)) sure.Add(id); }
        catch (Exception ex) { errors.Add("Protokoll: " + ex.Message); }
        // Aus der Registry des Druckers: nur vorhandene Dateien bearbeiten (die GUID könnte auch etwas anderes sein)
        var guesses = GotomaxxProfile.RegistryIds(printer).Where(id => !sure.Contains(id)).ToList();
        if (sure.Count == 0 && guesses.Count == 0)
            return new PrinterStatus(printer, Shown(delimiter), new(), 0, 0, errors,
                "Profil-ID nicht gefunden – einmal über diesen Drucker drucken (dann steht er im gotomaxx-Protokoll) oder die ID von Hand eintragen.");

        int files = 0, changed = 0;
        foreach (var (name, appData, isDefault) in profiles)
        {
            var gx = Path.Combine(appData, "gotomaxx");
            foreach (var id in sure.Concat(guesses))
            {
                var mayCreate = sure.Contains(id) && delimiter.Length > 0;
                var targets = new List<string>();
                if (Directory.Exists(gx))
                    foreach (var dir in Directory.GetDirectories(gx, "pdfmailer*"))
                    {
                        var f = Path.Combine(dir, "profiles", id + ".cfg");
                        if (File.Exists(f) || (mayCreate && string.Equals(Path.GetFileName(dir), version, StringComparison.OrdinalIgnoreCase))) targets.Add(f);
                    }
                // Benutzer ohne gotomaxx-Ordner: nur das Standardprofil vorbereiten (für neue Benutzer)
                if (targets.Count == 0 && mayCreate && isDefault) targets.Add(Path.Combine(gx, version, "profiles", id + ".cfg"));
                foreach (var f in targets)
                {
                    try
                    {
                        var exists = File.Exists(f);
                        var latin = Encoding.Latin1; // Bytes unverändert lassen (Datei kann ANSI oder UTF-8 sein)
                        var old = exists ? File.ReadAllText(f, latin) : null;
                        var text = GotomaxxProfileEditor.SetDelimiter(old, delimiter, out var ch);
                        files++;
                        if (!ch) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                        File.WriteAllText(f, text, latin);
                        changed++;
                    }
                    catch (Exception ex) { errors.Add($"{name}: {ex.Message}"); }
                }
            }
        }
        var note = files == 0
            ? (delimiter.Length == 0 ? "Kein Profil mit eigenem Trennzeichen gefunden – gilt bereits der Standard." : "Kein Benutzerprofil mit gotomaxx gefunden.")
            : null;
        return new PrinterStatus(printer, Shown(delimiter), sure.Concat(guesses).ToList(), files, changed, errors, note);
    }

    /// <summary>Roaming-AppData aller Benutzerprofile am Server (ohne Systemkonten) und des Standardprofils für neue Benutzer.</summary>
    [SupportedOSPlatform("windows")]
    private static List<(string Name, string AppData, bool IsDefault)> UserProfiles()
    {
        var list = new List<(string, string, bool)>();
        using var pl = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        foreach (var sid in pl?.GetSubKeyNames() ?? Array.Empty<string>())
        {
            if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !sid.StartsWith("S-1-12-1-", StringComparison.Ordinal)) continue; // keine Systemkonten
            using var k = pl!.OpenSubKey(sid);
            if (k?.GetValue("ProfileImagePath") is not string path) continue;
            path = Environment.ExpandEnvironmentVariables(path);
            var appData = Path.Combine(path, "AppData", "Roaming");
            // Ordnerumleitung: nur sichtbar, solange der Benutzer angemeldet ist (Registry des Benutzers geladen)
            try
            {
                using var shell = Registry.Users.OpenSubKey(sid + @"\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
                if (shell?.GetValue("AppData", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string redirected && redirected.Length > 0)
                    appData = redirected.Replace("%USERPROFILE%", path, StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            if (Directory.Exists(path)) list.Add((Path.GetFileName(path), appData, false));
        }
        var def = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList", "Default", null) as string;
        var defPath = Environment.ExpandEnvironmentVariables(string.IsNullOrEmpty(def) ? @"%SystemDrive%\Users\Default" : def);
        if (Directory.Exists(defPath)) list.Add(("Standardprofil", Path.Combine(defPath, "AppData", "Roaming"), true));
        return list;
    }
}
