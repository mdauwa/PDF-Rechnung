using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PdfRechnung.Documents;

namespace PdfRechnung.Printing;

/// <summary>Ein Druckauftrag aus der Windows-Druckwarteschlange.</summary>
public sealed record SpoolJob(int JobId, string? Document, string? UserName, string? MachineName, DateTime? SubmittedUtc, bool Printing);

/// <summary>Druckwarteschlange lesen (EnumJobs, JOB_INFO_2).</summary>
[SupportedOSPlatform("windows")]
public static class SpoolJobs
{
    public static List<SpoolJob> List(string printer)
    {
        var list = new List<SpoolJob>();
        if (!OpenPrinterW(printer, out var h, IntPtr.Zero)) return list;
        try
        {
            EnumJobsW(h, 0, 100, 2, IntPtr.Zero, 0, out var needed, out _);
            if (needed == 0) return list;
            var buf = Marshal.AllocHGlobal(needed);
            try
            {
                if (!EnumJobsW(h, 0, 100, 2, buf, needed, out _, out var count)) return list;
                var size = Marshal.SizeOf<JOB_INFO_2>();
                for (var i = 0; i < count; i++)
                {
                    var j = Marshal.PtrToStructure<JOB_INFO_2>(buf + i * size);
                    var s = j.Submitted;
                    DateTime? submitted = s.wYear > 0 ? new DateTime(s.wYear, s.wMonth, s.wDay, s.wHour, s.wMinute, s.wSecond, s.wMilliseconds, DateTimeKind.Utc) : null;
                    list.Add(new SpoolJob((int)j.JobId, Marshal.PtrToStringUni(j.pDocument), Marshal.PtrToStringUni(j.pUserName),
                        Marshal.PtrToStringUni(j.pMachineName)?.TrimStart('\\'), submitted, (j.Status & JOB_STATUS_PRINTING) != 0));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { ClosePrinter(h); }
        return list;
    }

    /// <summary>Der Auftrag, der gerade an den Anschluss geschrieben wird (sonst der älteste).</summary>
    public static SpoolJob? Current(string printer)
    {
        var jobs = List(printer);
        return jobs.Where(j => j.Printing).OrderBy(j => j.JobId).FirstOrDefault() ?? jobs.OrderBy(j => j.JobId).FirstOrDefault();
    }

    private const uint JOB_STATUS_PRINTING = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME { public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOB_INFO_2
    {
        public uint JobId;
        public IntPtr pPrinterName, pMachineName, pUserName, pDocument, pNotifyName, pDatatype, pPrintProcessor, pParameters, pDriverName, pDevMode, pStatus, pSecurityDescriptor;
        public uint Status, Priority, Position, StartTime, UntilTime, TotalPages, Size;
        public SYSTEMTIME Submitted;
        public uint Time, PagesPrinted;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool OpenPrinterW(string name, out IntPtr handle, IntPtr defaults);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool ClosePrinter(IntPtr handle);
    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumJobsW(IntPtr printer, int first, int count, int level, IntPtr buffer, int size, out int needed, out int returned);
}

/// <summary>
/// Drucker „Belegpost“ einrichten: Treiber „Microsoft Print To PDF“ mit dem lokalen Anschluss <c>\\.\pipe\belegpost-druck</c>
/// (statt PORTPROMPT: – kein Speichern-Dialog). Der Treiber schreibt jedes PDF in die Pipe, auf der der Dienst lauscht.
/// </summary>
[SupportedOSPlatform("windows")]
public static class PrinterSetup
{
    public const string PipeName = "belegpost-druck";
    public const string PortName = @"\\.\pipe\" + PipeName;
    public const string Driver = "Microsoft Print To PDF";

    public sealed record Status(bool Exists, string? Driver, string? Port, bool Shared, string? ShareName, bool DriverInstalled, string? Problem);

    public static Status Get(string name)
    {
        try
        {
            var json = PowerShell($$"""
                $p = Get-Printer -Name '{{Q(name)}}' -ErrorAction SilentlyContinue
                $d = Get-PrinterDriver -Name '{{Driver}}' -ErrorAction SilentlyContinue
                [pscustomobject]@{ exists = [bool]$p; driver = $p.DriverName; port = $p.PortName; shared = [bool]$p.Shared; shareName = $p.ShareName; driverInstalled = [bool]$d } | ConvertTo-Json -Compress
                """);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool B(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
            return new Status(B("exists"), S("driver"), S("port"), B("shared"), S("shareName"), B("driverInstalled"), null);
        }
        catch (Exception ex) { return new Status(false, null, null, false, null, false, ex.Message); }
    }

    public static void Install(string name, bool share, Action<string> log) => Install(name, share, PortName, log);

    public static void Install(string name, bool share, string portName, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Bitte einen Druckernamen angeben.");
        string output;
        try { output = InstallScript(name, share, portName); }
        catch (PowerShellException ex)
        {
            foreach (var line in ex.Done.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) log(line);
            throw new InvalidOperationException(Explain(ex.Message));
        }
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) log(line);
    }

    /// <summary>Häufige Spooler-Meldungen mit Hinweis ergänzen.</summary>
    private static string Explain(string m) =>
        m.Contains("0x00000704", StringComparison.OrdinalIgnoreCase) || m.Contains("unbekannt", StringComparison.OrdinalIgnoreCase) && m.Contains("Anschluss", StringComparison.OrdinalIgnoreCase)
            || m.Contains("port is unknown", StringComparison.OrdinalIgnoreCase)
            ? m + " – die Druckwarteschlange kennt den neuen Anschluss noch nicht. Dienst „Druckwarteschlange“ neu starten und „Drucker einrichten“ erneut ausführen."
        : m.Contains("Zugriff verweigert", StringComparison.OrdinalIgnoreCase) || m.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            ? m + " – Belegpost muss als Dienst (bzw. als Administrator) laufen, um Drucker anzulegen."
        : m;

    private static string InstallScript(string name, bool share, string portName)
    {
        return PowerShell($$"""
            $ErrorActionPreference = 'Stop'
            if (-not (Get-PrinterDriver -Name '{{Driver}}' -ErrorAction SilentlyContinue)) {
              throw "Der Treiber '{{Driver}}' ist nicht installiert (Windows-Feature 'Microsoft Print to PDF' bzw. Serverrolle Druckdienste)."
            }
            if (-not (Get-PrinterPort -Name '{{Q(portName)}}' -ErrorAction SilentlyContinue)) { Add-PrinterPort -Name '{{Q(portName)}}'; 'Anschluss {{Q(portName)}} angelegt.' }
            else { 'Anschluss {{Q(portName)}} vorhanden.' }
            if (Get-Printer -Name '{{Q(name)}}' -ErrorAction SilentlyContinue) {
              Set-Printer -Name '{{Q(name)}}' -DriverName '{{Driver}}' -PortName '{{Q(portName)}}'; 'Drucker {{Q(name)}} aktualisiert.'
            } else {
              # ein frisch angelegter Anschluss ist für die Warteschlange manchmal erst nach einem Moment sichtbar
              $ok = $false
              for ($i = 1; $i -le 3 -and -not $ok; $i++) {
                try { Add-Printer -Name '{{Q(name)}}' -DriverName '{{Driver}}' -PortName '{{Q(portName)}}'; $ok = $true }
                catch { if ($i -eq 3) { throw } ; Start-Sleep -Seconds 2 }
              }
              'Drucker {{Q(name)}} angelegt.'
            }
            if ({{(share ? "$true" : "$false")}}) { Set-Printer -Name '{{Q(name)}}' -Shared $true -ShareName '{{Q(name)}}'; 'Freigegeben als {{Q(name)}}.' }
            else { Set-Printer -Name '{{Q(name)}}' -Shared $false; 'Nicht freigegeben.' }
            """);
    }

    /// <summary>Drucker und seinen Anschluss aus Windows entfernen.</summary>
    public static void Remove(string name, string portName, Action<string> log)
    {
        var output = PowerShell($$"""
            $ErrorActionPreference = 'Stop'
            if (Get-Printer -Name '{{Q(name)}}' -ErrorAction SilentlyContinue) { Remove-Printer -Name '{{Q(name)}}'; 'Drucker {{Q(name)}} entfernt.' } else { 'Drucker {{Q(name)}} war nicht vorhanden.' }
            Start-Sleep -Milliseconds 500
            if (Get-PrinterPort -Name '{{Q(portName)}}' -ErrorAction SilentlyContinue) { Remove-PrinterPort -Name '{{Q(portName)}}'; 'Anschluss {{Q(portName)}} entfernt.' }
            """);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) log(line);
    }

    private static string Q(string s) => s.Replace("'", "''");

    /// <summary>Fehler aus dem Skript; <see cref="Done"/> = was bis dahin erledigt wurde.</summary>
    public sealed class PowerShellException(string message, string done) : InvalidOperationException(message)
    {
        public string Done { get; } = done;
    }

    private static string PowerShell(string script)
    {
        // Fehler als lesbare Zeile auf die Standardausgabe (stderr kommt bei umgeleiteter Ausgabe oft nur als CLIXML)
        script = "$ProgressPreference = 'SilentlyContinue'\ntry {\n" + script
            + "\n} catch {\n  $m = $_.Exception.Message; if ($_.CategoryInfo.Activity) { $m = $_.CategoryInfo.Activity + ': ' + $m }\n  '##FEHLER## ' + $m\n  exit 1\n}";
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        psi.Environment["PSModulePath"] = Environment.GetEnvironmentVariable("PSModulePath") ?? "";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell nicht startbar.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000)) { try { p.Kill(); } catch { } throw new InvalidOperationException("PowerShell antwortet nicht."); }
        var err = stderr.Result.Trim();
        var marked = stdout.Result.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("##FEHLER## ", StringComparison.Ordinal));
        if (marked is not null)
        {
            // bis dahin Erledigtes trotzdem melden, dann den eigentlichen Fehler
            var done = string.Join("\n", stdout.Result.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("##FEHLER## ", StringComparison.Ordinal)));
            throw new PowerShellException(marked["##FEHLER## ".Length..], done);
        }
        if (p.ExitCode != 0)
            throw new InvalidOperationException(FirstLine(err.Length > 0 && !err.StartsWith("#< CLIXML", StringComparison.Ordinal) ? err : stdout.Result.Length > 0 ? stdout.Result : err));
        return stdout.Result.Trim();
    }

    private static string FirstLine(string s)
    {
        // PowerShell-Fehlertext: erste aussagekräftige Zeile
        var line = s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("At line", StringComparison.Ordinal) && !l.StartsWith("In Zeile", StringComparison.Ordinal)) ?? s;
        return line.Length > 400 ? line[..400] : line;
    }
}

/// <summary>Ein angenommener Druck (für die Anzeige in den Einstellungen).</summary>
public sealed record PrintIntakeEntry(DateTimeOffset At, int? JobId, string? Document, string? User, string? Machine, int Bytes, string? VorgangId, string Origin, string? Problem);

/// <summary>
/// Nimmt die Drucke des Druckers „Belegpost“ an: lauscht auf <c>\\.\pipe\belegpost-druck</c>, liest Benutzer, Rechner und Dokumentname
/// aus der Druckwarteschlange und – falls eingeschaltet – den ERP-Benutzer aus dem druckenden Prozess (wwa.exe).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PrinterIntake : BackgroundService
{
    private readonly IOptionsMonitor<InboxOptions> _opt;
    private readonly DocumentProcessor _processor;
    private readonly PrintProcessTracker _tracker;
    private readonly Settings.SettingsStore _settings;
    private readonly ILogger<PrinterIntake> _log;
    private readonly System.Collections.Concurrent.ConcurrentQueue<PrintIntakeEntry> _recent = new();

    public PrinterIntake(IOptionsMonitor<InboxOptions> opt, DocumentProcessor processor, PrintProcessTracker tracker, Settings.SettingsStore settings, ILogger<PrinterIntake> log)
    {
        _opt = opt; _processor = processor; _tracker = tracker; _settings = settings; _log = log;
    }

    /// <summary>Hauptdrucker nimmt an.</summary>
    public bool Listening => _listening.ContainsKey(PrinterSetup.PipeName);
    /// <summary>Kennungen der zusätzlichen Drucker, deren Anschluss bereit ist.</summary>
    public bool ListeningFor(string pipeName) => _listening.ContainsKey(pipeName);
    public IReadOnlyList<PrintIntakeEntry> Recent() => _recent.Reverse().ToList();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _listening = new();

    /// <summary>Je Drucker (Haupt- und zusätzliche) ein eigener Anschluss; Änderungen der Einstellungen werden laufend übernommen.</summary>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var running = new Dictionary<string, (CancellationTokenSource Cts, Task Task)>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var wanted = new HashSet<string> { PrinterSetup.PipeName };
                foreach (var x in _opt.CurrentValue.Printers.Where(x => IsValidId(x.Id))) wanted.Add(x.PipeName);
                foreach (var gone in running.Keys.Where(k => !wanted.Contains(k)).ToList())
                {
                    running[gone].Cts.Cancel(); running.Remove(gone); _listening.TryRemove(gone, out _);
                }
                foreach (var pipeName in wanted.Where(w => !running.ContainsKey(w)))
                {
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    running[pipeName] = (cts, Task.Run(() => ListenAsync(pipeName, cts.Token), cts.Token));
                }
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { break; }
            }
        }
        finally { foreach (var r in running.Values) r.Cts.Cancel(); _listening.Clear(); }
    }

    public static bool IsValidId(string? id) => id is { Length: >= 1 and <= 32 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>Zum Anschluss gehörender Drucker: Hauptdrucker (null) oder ein zusätzlicher.</summary>
    private ExtraPrinter? ExtraFor(string pipeName) =>
        pipeName == PrinterSetup.PipeName ? null : _opt.CurrentValue.Printers.FirstOrDefault(x => x.PipeName == pipeName);

    private async Task ListenAsync(string pipeName, CancellationToken ct)
    {
        var security = new PipeSecurity();
        // Der Druckspooler (SYSTEM) schreibt – je nach Anschluss auch im Namen des druckenden Benutzers
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1024 * 1024, security);
                _listening[pipeName] = true;
                await pipe.WaitForConnectionAsync(ct);
                var extra = ExtraFor(pipeName);
                var printerName = extra?.Name ?? _opt.CurrentValue.PrinterName;
                // Sofort nachsehen, welcher Auftrag gerade gedruckt wird – solange er noch in der Warteschlange steht
                SpoolJob? job = null;
                try { job = SpoolJobs.Current(printerName); } catch (Exception ex) { _log.LogDebug(ex, "Druckwarteschlange"); }
                var p = pipe;
                pipe = null;
                _ = Task.Run(() => HandleAsync(p, job, extra, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _listening.TryRemove(pipeName, out _);
                _log.LogWarning(ex, "Drucker-Pipe {Pipe}", pipeName);
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
            }
            finally { pipe?.Dispose(); }
        }
        _listening.TryRemove(pipeName, out _);
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, SpoolJob? job, ExtraPrinter? extra, CancellationToken ct)
    {
        byte[] data;
        try
        {
            using (pipe)
            {
                using var ms = new MemoryStream();
                await pipe.CopyToAsync(ms, ct);
                data = ms.ToArray();
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Druck konnte nicht gelesen werden"); return; }
        if (data.Length == 0) return;

        var o = _opt.CurrentValue;
        PrintOriginResult? origin = null;
        string originText = "aus der Druckwarteschlange";
        if (o.ProcessUserDetection)
        {
            if (!_tracker.Running) originText = "Prozesserkennung aus: " + _tracker.Problem;
            else
            {
                // kurz warten, bis ETW die letzten Aufrufe geliefert hat (Puffer werden jede Sekunde geleert)
                try { await Task.Delay(1500, ct); } catch (OperationCanceledException) { return; }
                origin = _tracker.Match(job?.SubmittedUtc ?? DateTime.UtcNow);
                originText = origin.Operator is not null ? $"Prozess {origin.Explanation}" : origin.Explanation;
            }
        }

        var name = string.IsNullOrWhiteSpace(job?.Document) ? "Druck" : job!.Document!;
        if (!(data.Length > 4 && data[0] == '%' && data[1] == 'P' && data[2] == 'D' && data[3] == 'F'))
        {
            var dir = Path.Combine(Documents.InboxWatcher.ResolveFolder(o, _settings), "fehler");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}_druck_{job?.JobId}.bin");
            await File.WriteAllBytesAsync(file, data, ct);
            Remember(new PrintIntakeEntry(DateTimeOffset.Now, job?.JobId, name, job?.UserName, job?.MachineName, data.Length, null, originText, "Kein PDF – abgelegt unter " + file));
            _log.LogWarning("Druck {Job} ist kein PDF – abgelegt unter {File}", job?.JobId, file);
            return;
        }

        try
        {
            // Mandant: /M: des druckenden Prozesses → Vorgabe des Druckers (@@pm@@ im Beleg geht ohnehin vor)
            var originMandant = MandantOptions.FromCommandLine(origin?.CommandLine);
            var mandantReason = originMandant is not null ? $"Parameter /M: des druckenden Prozesses ({origin!.Exe})" : null;
            if (originMandant is null && MandantOptions.Normalize(extra?.Mandant) is { } pm) { originMandant = pm; mandantReason = $"Drucker „{extra!.Name}“"; }
            var v = _processor.Process(data, "Drucker", name, job?.UserName, job?.MachineName, processOperator: origin?.Operator,
                originMandant: originMandant, originMandantReason: mandantReason, printerId: extra?.Id, beforeSave: x =>
            {
                x.PrintProcess = origin?.Pid is not null ? $"{origin.Explanation}{(string.IsNullOrEmpty(origin.CommandLine) ? "" : " – " + origin.CommandLine)}" : null;
                if (origin?.Warning is { } w) x.Messages.Add(new CheckMessage(DocumentChecker.Warning, w, "druck"));
                x.History.Add(new HistoryEntry(DateTimeOffset.Now, "Drucker", "druck",
                    $"Druckauftrag {job?.JobId}{(extra is null ? "" : $" (Drucker „{extra.Name}“)")} von {job?.UserName ?? "?"}{(string.IsNullOrEmpty(job?.MachineName) ? "" : " @ " + job!.MachineName)} · Benutzer: {originText}"));
            });
            Remember(new PrintIntakeEntry(DateTimeOffset.Now, job?.JobId, name, job?.UserName, job?.MachineName, data.Length, v.Id, originText, null));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Druck {Job} konnte nicht verarbeitet werden", job?.JobId);
            Remember(new PrintIntakeEntry(DateTimeOffset.Now, job?.JobId, name, job?.UserName, job?.MachineName, data.Length, null, originText, ex.Message));
        }
    }

    private void Remember(PrintIntakeEntry e)
    {
        _recent.Enqueue(e);
        while (_recent.Count > 20) _recent.TryDequeue(out _);
    }
}
