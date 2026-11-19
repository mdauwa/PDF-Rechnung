using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using PdfRechnung.Documents;

namespace PdfRechnung.Printing;

/// <summary>
/// Druck an gotomaxx unter Windows. Der PDFMAILER verarbeitet einen Druckauftrag in der Sitzung dessen, der druckt.
/// Deshalb startet der Dienst (SYSTEM) den Druck bevorzugt in der Windows-Sitzung des Benutzers am Server
/// (<c>Belegpost.exe --gotomaxx-druck &lt;Ordner&gt;</c>); ist er nicht angemeldet, druckt der Dienst selbst (einstellbar).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsGotomaxxPrinter : IGotomaxxPrinter
{
    public const string HelperArgument = "--gotomaxx-druck";
    private readonly ILogger<WindowsGotomaxxPrinter> _log;

    public WindowsGotomaxxPrinter(ILogger<WindowsGotomaxxPrinter> log) => _log = log;

    public IReadOnlyList<string> Printers() => PdfPrinter.InstalledPrinters();

    public async Task<string> PrintAsync(GotomaxxJob job, byte[] pdf, IReadOnlyCollection<string> windowsUsers, string? clientName, GotomaxxOptions o, CancellationToken ct)
    {
        if (o.UserSession)
        {
            var session = UserSessions.Find(windowsUsers, clientName);
            if (session is not null)
            {
                try { return await Task.Run(() => RunInSession(session, job, pdf, o, ct), ct); }
                catch (SessionStartException ex) when (o.ServiceFallback)
                {
                    _log.LogWarning(ex, "gotomaxx: Druck in der Sitzung von {User} nicht möglich – drucke als Dienst", session.Account);
                    var i1 = await Task.Run(() => PdfPrinter.Print(job, pdf), ct);
                    return $"vom Dienst gedruckt (Sitzung von {session.Account} nicht nutzbar: {ex.Message}){Suffix(i1)}";
                }
            }
            if (!o.ServiceFallback)
                throw new InvalidOperationException(windowsUsers.Count == 0
                    ? "Unbekannt, wer gedruckt hat – kein Windows-Benutzer für den Druck in seiner Sitzung."
                    : $"{string.Join(" / ", windowsUsers)} ist am Server nicht angemeldet.");
        }
        var info = await Task.Run(() => PdfPrinter.Print(job, pdf), ct);
        return (o.UserSession ? "vom Dienst gedruckt (Benutzer nicht am Server angemeldet)" : "vom Dienst gedruckt") + Suffix(info);
    }

    private static string Suffix(string? info) => string.IsNullOrWhiteSpace(info) ? "" : " · " + info.Trim();

    private string RunInSession(UserSession s, GotomaxxJob job, byte[] pdf, GotomaxxOptions o, CancellationToken ct)
    {
        var root = Path.Combine(Settings.SettingsStore.DefaultDataDirectory(), "gotomaxx");
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        IntPtr token = IntPtr.Zero;
        try
        {
            token = UserSessions.PrimaryToken(s.SessionId); // wirft SessionStartException
            var sid = new WindowsIdentity(token).User ?? throw new SessionStartException("Benutzerkennung (SID) nicht lesbar.");

            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "druck.pdf"), pdf);
            File.WriteAllText(Path.Combine(dir, "auftrag.json"), JsonSerializer.Serialize(job, GotomaxxJob.Json));
            // Nur dieser Benutzer (neben SYSTEM/Administratoren) darf den Auftragsordner lesen und das Ergebnis schreiben
            var sec = new DirectoryInfo(dir).GetAccessControl();
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(dir).SetAccessControl(sec);

            var exe = Environment.ProcessPath ?? throw new SessionStartException("Programmpfad unbekannt.");
            var exit = UserSessions.Run(token, exe, $"{HelperArgument} \"{dir}\"", TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 10, 900)), ct);
            var result = Path.Combine(dir, "ergebnis.txt");
            var text = File.Exists(result) ? File.ReadAllText(result).Trim() : "";
            if (exit == 0 && text.StartsWith("OK", StringComparison.Ordinal))
                return $"in der Sitzung von {s.Account}{(string.IsNullOrEmpty(s.ClientName) ? "" : $" ({s.ClientName})")}{Suffix(text[2..])}";
            var msg = text.StartsWith("FEHLER", StringComparison.Ordinal) ? text[6..].Trim(' ', ':', '\r', '\n') : $"Druckprogramm in der Sitzung beendet mit Code {exit}.";
            throw new InvalidOperationException(msg);
        }
        finally
        {
            if (token != IntPtr.Zero) UserSessions.CloseHandle(token);
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// Läuft in der Sitzung des Benutzers: <c>Belegpost.exe --gotomaxx-druck "&lt;Ordner&gt;"</c>.
    /// Liest auftrag.json und druck.pdf, druckt und schreibt ergebnis.txt („OK“ oder „FEHLER: …“).
    /// </summary>
    public static int RunHelper(string[] args)
    {
        var i = Array.IndexOf(args, HelperArgument);
        var dir = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        if (dir is null || !Directory.Exists(dir)) return 2;
        var result = Path.Combine(dir, "ergebnis.txt");
        try
        {
            var job = JsonSerializer.Deserialize<GotomaxxJob>(File.ReadAllText(Path.Combine(dir, "auftrag.json")), GotomaxxJob.Json)
                ?? throw new InvalidOperationException("Auftrag nicht lesbar.");
            var info = PdfPrinter.Print(job, File.ReadAllBytes(Path.Combine(dir, "druck.pdf")));
            File.WriteAllText(result, "OK " + info);
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(result, "FEHLER: " + ex.Message); } catch { }
            return 1;
        }
    }
}

/// <summary>Start des Druckprogramms in der Benutzersitzung ist nicht möglich (dann ggf. Druck durch den Dienst).</summary>
public sealed class SessionStartException(string message) : Exception(message);

public sealed record UserSession(int SessionId, string Account, string? ClientName, bool Active);

/// <summary>Windows-Sitzungen am Server (WTS) und Prozessstart als angemeldeter Benutzer.</summary>
[SupportedOSPlatform("windows")]
public static class UserSessions
{
    public static List<UserSession> All()
    {
        var list = new List<UserSession>();
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buffer, out var count)) return list;
        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(buffer + i * size);
                if (info.State is not (WTSActive or WTSDisconnected)) continue;
                var user = Query(info.SessionId, WTSUserName);
                if (string.IsNullOrEmpty(user)) continue;
                var domain = Query(info.SessionId, WTSDomainName);
                list.Add(new UserSession(info.SessionId, string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}", Query(info.SessionId, WTSClientName), info.State == WTSActive));
            }
        }
        finally { WTSFreeMemory(buffer); }
        return list;
    }

    /// <summary>Sitzung eines der Benutzer; bevorzugt die mit passendem RDP-Client, dann aktive vor getrennten.</summary>
    public static UserSession? Find(IReadOnlyCollection<string> users, string? clientName)
    {
        if (users.Count == 0) return null;
        var matches = All().Where(s => users.Any(u => SameAccount(u, s.Account))).ToList();
        return matches
            .OrderByDescending(s => !string.IsNullOrEmpty(clientName) && Settings.UserStore.SameComputer(s.ClientName, clientName))
            .ThenByDescending(s => s.Active)
            .FirstOrDefault();
    }

    /// <summary>„DOMAIN\name“, „name“ – Domäne nur vergleichen, wenn beide eine haben. E-Mail-Adressen passen nie.</summary>
    public static bool SameAccount(string wanted, string account)
    {
        if (wanted.Contains('@')) return false;
        static (string? Domain, string Name) Split(string a)
        {
            var i = a.IndexOf('\\');
            return i >= 0 ? (a[..i], a[(i + 1)..]) : (null, a);
        }
        var (wd, wn) = Split(wanted.Trim()); var (ad, an) = Split(account.Trim());
        if (!wn.Equals(an, StringComparison.OrdinalIgnoreCase)) return false;
        return wd is null || ad is null || wd.Equals(ad, StringComparison.OrdinalIgnoreCase)
            || (wd == "." && ad.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase));
    }

    public static IntPtr PrimaryToken(int sessionId)
    {
        if (!WTSQueryUserToken(sessionId, out var token))
            throw new SessionStartException($"Kein Zugriff auf die Sitzung {sessionId} ({new Win32Exception(Marshal.GetLastWin32Error()).Message}). Der Druck in Benutzersitzungen geht nur aus dem Dienst.");
        try
        {
            if (!DuplicateTokenEx(token, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var primary))
                throw new SessionStartException("Anmeldetoken nicht nutzbar: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            return primary;
        }
        finally { CloseHandle(token); }
    }

    /// <summary>Startet das Programm unsichtbar in der Sitzung und wartet auf das Ende. Liefert den Exit-Code.</summary>
    public static int Run(IntPtr token, string exe, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        if (!CreateEnvironmentBlock(out var env, token, false)) env = IntPtr.Zero;
        try
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var cmd = $"\"{exe}\" {arguments}";
            if (!CreateProcessAsUserW(token, exe, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, Path.GetDirectoryName(exe), ref si, out var pi))
                throw new SessionStartException("Druckprogramm konnte in der Sitzung nicht gestartet werden: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            try
            {
                var end = DateTime.UtcNow + timeout;
                while (WaitForSingleObject(pi.hProcess, 250) == WAIT_TIMEOUT)
                {
                    if (ct.IsCancellationRequested || DateTime.UtcNow > end)
                    {
                        TerminateProcess(pi.hProcess, 3);
                        throw new InvalidOperationException(ct.IsCancellationRequested ? "Abgebrochen." : $"Der Druck hat länger als {timeout.TotalSeconds:0} Sekunden gedauert und wurde abgebrochen.");
                    }
                }
                return GetExitCodeProcess(pi.hProcess, out var code) ? (int)code : -1;
            }
            finally { CloseHandle(pi.hProcess); CloseHandle(pi.hThread); }
        }
        finally { if (env != IntPtr.Zero) DestroyEnvironmentBlock(env); }
    }

    private static string? Query(int sessionId, int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, infoClass, out var buffer, out _)) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }

    private const int WTSActive = 0, WTSDisconnected = 4, WTSUserName = 5, WTSDomainName = 7, WTSClientName = 10;
    private const uint MAXIMUM_ALLOWED = 0x02000000, CREATE_UNICODE_ENVIRONMENT = 0x400, CREATE_NO_WINDOW = 0x08000000, WAIT_TIMEOUT = 0x102;
    private const int SecurityImpersonation = 2, TokenPrimary = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO { public int SessionId; public IntPtr pWinStationName; public int State; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr info, out int count);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string? application, string commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref STARTUPINFO startup, out PROCESS_INFORMATION info);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
}
