using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PdfRechnung.Documents;
using PdfRechnung.Settings;

namespace PdfRechnung.Services;

/// <summary>
/// Belegpost-Drucker am RDS-Server ohne Melder: Der Dienst öffnet Outlook (Entwurf) bzw. das Portal direkt in der
/// Windows-Sitzung des Benutzers am Server. Dafür startet er dort kurz den Melder im Einmal-Modus
/// (<c>Belegpost-Melder.exe --entwurf &lt;datei&gt;</c> bzw. <c>--oeffnen &lt;url&gt;</c>). MAPI bleibt unberührt.
/// Nur als Windows-Dienst (SYSTEM) möglich: Start in einer fremden Sitzung braucht WTSQueryUserToken.
/// </summary>
public sealed class SessionLauncher
{
    public const string ModeMelder = "melder", ModeSession = "sitzung";

    private readonly UserStore _users;
    private readonly SendService _send;
    private readonly DocumentStore _store;
    private readonly SettingsStore _settings;
    private readonly IOptionsMonitor<MapiOptions> _mapi;
    private readonly IOptionsMonitor<ServerOptions> _server;
    private readonly IOptionsMonitor<PortalOptions> _portal;
    private readonly ILogger<SessionLauncher> _log;

    public SessionLauncher(UserStore users, SendService send, DocumentStore store, SettingsStore settings,
        IOptionsMonitor<MapiOptions> mapi, IOptionsMonitor<ServerOptions> server, IOptionsMonitor<PortalOptions> portal, ILogger<SessionLauncher> log)
    {
        _users = users; _send = send; _store = store; _settings = settings; _mapi = mapi; _server = server; _portal = portal; _log = log;
    }

    /// <summary>Öffnet Outlook/Portal für diesen Benutzer direkt in seiner Sitzung am Server (statt über den Melder)?</summary>
    public bool UsesSession(PortalUser? u)
    {
        if (u is null) return false;
        var mode = string.IsNullOrEmpty(u.OutlookTarget) ? _mapi.CurrentValue.OutlookTarget : u.OutlookTarget;
        return mode == ModeSession;
    }

    /// <summary>Windows-Konto, in dessen Sitzung geöffnet wird: der druckende Benutzer, sonst der erste Druck-Benutzer des Portal-Benutzers.</summary>
    private static string? Account(Vorgang? v, PortalUser u) =>
        v is not null && v.OwnerUserId == u.Id && !string.IsNullOrWhiteSpace(v.PrintUser) ? v.PrintUser
        : u.PrintNames.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? (v?.OwnerUserId == u.Id ? v.PrintUser : null);

    /// <summary>Entwurf in Outlook in der Sitzung des Benutzers öffnen. Wartet, bis der Entwurf offen ist (höchstens 2 Minuten).</summary>
    public async Task<(bool Ok, string Message)> OpenOutlookAsync(Vorgang v, PortalUser u, string by, CancellationToken ct)
    {
        var account = Account(v, u);
        if (account is null) return (false, $"Für {u.Name} ist kein Windows-Benutzer hinterlegt (Einstellungen → Benutzer → Druck-Benutzer).");
        var (mail, err) = await _send.BuildMail(v, ct);
        if (mail is null) return (false, err ?? "Mail konnte nicht erstellt werden.");

        var payload = new
        {
            id = v.Id, to = mail.To, cc = mail.Cc, bcc = mail.Bcc, subject = mail.Subject, html = mail.HtmlBody, text = mail.Body,
            attachments = mail.Attachments.Select(a => new { name = a.Name, contentType = a.ContentType, data = Convert.ToBase64String(a.Data), contentId = (string?)null })
                .Concat(mail.Images.Select(i => new { name = i.Name, contentType = i.ContentType, data = Convert.ToBase64String(i.Data), contentId = (string?)i.ContentId }))
        };
        var dir = Path.Combine(_settings.DataDirectory, "temp", "outlook");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllBytesAsync(file, JsonSerializer.SerializeToUtf8Bytes(payload), ct);
            var (code, msg) = await RunInSessionAsync(account, $"--entwurf \"{file}\"", file, TimeSpan.FromMinutes(2), ct);
            if (code == 0)
            {
                _send.MarkOutlook(v.Id, $"{by} (Outlook am Server)");
                return (true, "Outlook öffnet den Entwurf in der Sitzung am Server.");
            }
            var text = msg ?? code switch
            {
                2 => "Am Server ist kein klassisches Outlook installiert.",
                3 => "Outlook konnte den Entwurf nicht anlegen (Outlook-Profil eingerichtet?).",
                4 => "Die Mail-Daten konnten in der Sitzung nicht gelesen werden.",
                _ => $"Der Melder hat mit Code {code} beendet."
            };
            _store.AddEvent(v.Id, StatusCode.OutlookFehler, "Outlook", by, text);
            return (false, text);
        }
        finally { try { File.Delete(file); } catch { /* egal */ } }
    }

    /// <summary>Portal (z. B. den neuen Vorgang) im Standardbrowser der Sitzung öffnen. Wartet nicht.</summary>
    public async Task<(bool Ok, string Message)> OpenPortalAsync(Vorgang v, PortalUser u, CancellationToken ct)
    {
        var account = Account(v, u);
        if (account is null) return (false, "kein Windows-Benutzer hinterlegt");
        var url = BaseUrl() + "/#/vorgang?id=" + Uri.EscapeDataString(v.Id);
        var (code, msg) = await RunInSessionAsync(account, $"--oeffnen \"{url}\"", null, TimeSpan.FromSeconds(30), ct);
        return (code == 0, msg ?? (code == 0 ? "Portal geöffnet." : $"Code {code}"));
    }

    private string BaseUrl()
    {
        var p = _portal.CurrentValue.PublicUrl;
        return string.IsNullOrWhiteSpace(p) ? $"https://{Environment.MachineName.ToLowerInvariant()}:{_server.CurrentValue.HttpsPort}" : p.TrimEnd('/');
    }

    /// <summary>Startet den Melder mit <paramref name="args"/> in der Sitzung des Windows-Kontos und liefert den Exitcode.</summary>
    private async Task<(int Code, string? Message)> RunInSessionAsync(string account, string args, string? grantFile, TimeSpan timeout, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return (-1, "Nur unter Windows möglich.");
        if (!File.Exists(MelderApi.ExePath)) return (-1, $"{MelderApi.ExeName} fehlt neben Belegpost.exe.");
        var session = FindSession(account);
        if (session is null) return (-1, $"{account} ist am Server nicht angemeldet – Outlook kann dort nicht geöffnet werden.");
        if (grantFile is not null) Grant(grantFile, session.Value.Account);
        try
        {
            return await Task.Run(() => Launch(session.Value.Id, $"\"{MelderApi.ExePath}\" {args}", timeout), ct);
        }
        catch (Win32Exception ex)
        {
            _log.LogWarning(ex, "Start in Sitzung {Session} ({Account}) fehlgeschlagen", session.Value.Id, account);
            return (-1, ex.NativeErrorCode == 1314
                ? "Direktes Öffnen geht nur, wenn Belegpost als Dienst läuft." : "Start in der Sitzung fehlgeschlagen: " + ex.Message);
        }
    }

    // ---------------- Windows ----------------

    /// <summary>Sitzung des Kontos: angemeldet (aktiv) vor getrennt; bei mehreren die neueste.</summary>
    [SupportedOSPlatform("windows")]
    private static (int Id, string Account)? FindSession(string account)
    {
        var shortName = UserStore.Short(account);
        var found = new List<(int Id, string Account, bool Active)>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buf, out var count)) return null;
        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(buf + i * size);
                if (info.State is not (0 /* Active */ or 4 /* Disconnected */)) continue;
                var user = Query(info.SessionId, 5); // WTSUserName
                if (string.IsNullOrEmpty(user) || !string.Equals(user, shortName, StringComparison.OrdinalIgnoreCase)) continue;
                var domain = Query(info.SessionId, 7); // WTSDomainName
                // Domäne nur vergleichen, wenn das Konto eine trägt
                if (account.Contains('\\') && !string.IsNullOrEmpty(domain)
                    && !string.Equals(account[..account.IndexOf('\\')], domain, StringComparison.OrdinalIgnoreCase)) continue;
                found.Add((info.SessionId, string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}", info.State == 0));
            }
        }
        finally { WTSFreeMemory(buf); }
        var pick = found.OrderByDescending(f => f.Active).ThenByDescending(f => f.Id).FirstOrDefault();
        return found.Count == 0 ? null : (pick.Id, pick.Account);
    }

    [SupportedOSPlatform("windows")]
    private static string? Query(int session, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, infoClass, out var p, out _)) return null;
        try { return Marshal.PtrToStringUni(p); } finally { WTSFreeMemory(p); }
    }

    /// <summary>Datei für den Benutzer lesbar/löschbar machen (der Datenordner ist sonst nur für Administratoren und SYSTEM).</summary>
    [SupportedOSPlatform("windows")]
    private void Grant(string file, string account)
    {
        try
        {
            var fi = new FileInfo(file);
            var sec = fi.GetAccessControl();
            sec.AddAccessRule(new FileSystemAccessRule(new NTAccount(account), FileSystemRights.Read | FileSystemRights.Delete, AccessControlType.Allow));
            fi.SetAccessControl(sec);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Rechte für {Account} auf {File} nicht gesetzt", account, file); }
    }

    [SupportedOSPlatform("windows")]
    private static (int, string?) Launch(int sessionId, string commandLine, TimeSpan timeout)
    {
        if (!WTSQueryUserToken(sessionId, out var userToken)) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr primary = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(userToken, 0x02000000 /* MAXIMUM_ALLOWED */, IntPtr.Zero, 2 /* Impersonation */, 1 /* Primary */, out primary))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            CreateEnvironmentBlock(out env, primary, false);
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            if (!CreateProcessAsUser(primary, null, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0x00000400 /* UNICODE_ENVIRONMENT */,
                    env, Path.GetDirectoryName(MelderApi.ExePath), ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (WaitForSingleObject(pi.hProcess, (uint)timeout.TotalMilliseconds) != 0) return (5, "Outlook hat nicht rechtzeitig reagiert – bitte in der Sitzung nachsehen.");
                GetExitCodeProcess(pi.hProcess, out var code);
                return ((int)code, null);
            }
            finally { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); }
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            CloseHandle(userToken);
        }
    }

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

    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr info, out int count);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr p);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(int session, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int level, int type, out IntPtr newToken);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
}
