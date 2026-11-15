using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;

namespace PdfRechnung.Printing;

/// <summary>
/// Schreibt mit, wann überwachte Prozesse (Standard: wwa.exe der WebWARE) die Druckwarteschlange aufrufen.
/// Quelle ist die Windows-Ereignisablaufverfolgung (ETW, Anbieter Microsoft-Windows-RPC, Ereignis 5 „Client-Aufruf gestartet“),
/// gefiltert auf die Programmnamen – es wird also nur in diesen Prozessen mitgeschrieben.
/// Die Befehlszeile (mit der Benutzernummer) wird beim ersten Aufruf eines Prozesses gelesen.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PrintProcessTracker : BackgroundService
{
    public const string SessionName = "Belegpost-Druckherkunft";
    private static readonly Guid RpcProvider = new("6ad52b32-d609-4be9-ae07-ce8dae937e39");
    private const int RpcClientCallStart = 5;

    private readonly IOptionsMonitor<InboxOptions> _opt;
    private readonly ILogger<PrintProcessTracker> _log;
    private readonly ConcurrentQueue<RpcCall> _calls = new();
    private readonly ConcurrentDictionary<int, (string Exe, string? CommandLine, DateTime Seen)> _processes = new();
    private readonly ConcurrentDictionary<(Guid, string), int> _seenInterfaces = new();
    private EventRecordCallback? _callback; // muss leben, solange ETW zurückruft
    private ulong _session, _trace;
    private string? _activeNames;
    private HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

    public PrintProcessTracker(IOptionsMonitor<InboxOptions> opt, ILogger<PrintProcessTracker> log) { _opt = opt; _log = log; }

    /// <summary>Läuft die Überwachung? Sonst der Grund.</summary>
    public string? Problem { get; private set; } = "Noch nicht gestartet.";
    public bool Running => Problem is null;

    /// <summary>Letzte Aufrufe (für die Zuordnung und zur Anzeige in den Einstellungen).</summary>
    public IReadOnlyList<RpcCall> RecentCalls() => _calls.ToArray();

    /// <summary>Welche RPC-Schnittstellen/Endpunkte die überwachten Prozesse benutzt haben (Diagnose).</summary>
    public IReadOnlyList<(Guid Interface, string Endpoint, int Count)> Interfaces() =>
        _seenInterfaces.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value)).OrderByDescending(x => x.Value).Take(30).ToList();

    public PrintOriginResult Match(DateTime submittedUtc) =>
        PrintOriginMatcher.Match(_calls, submittedUtc, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var o = _opt.CurrentValue;
            var list = PrintOriginMatcher.ProcessNames(o.ProcessNames);
            var names = string.Join(";", list) + (o.ProcessFilterByName ? "" : "|ohne Filter");
            if (!o.ProcessUserDetection || names.Length == 0)
            {
                Stop();
                Problem = "Ausgeschaltet (Einstellungen → Drucker & Eingang).";
            }
            else if (_trace == 0 || names != _activeNames)
            {
                Stop();
                try { _names = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase); Start(string.Join(";", list), o.ProcessFilterByName, ct); _activeNames = names; Problem = null; }
                catch (Exception ex) { Problem = ex.Message; _log.LogWarning(ex, "Druckherkunft: Überwachung nicht gestartet"); }
            }
            Prune();
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { break; }
        }
        Stop();
    }

    private void Prune()
    {
        var limit = DateTime.UtcNow.AddMinutes(-10);
        while (_calls.TryPeek(out var c) && (c.Utc < limit || _calls.Count > 5000)) _calls.TryDequeue(out _);
        foreach (var (pid, p) in _processes) if (p.Seen < DateTime.UtcNow.AddHours(-2)) _processes.TryRemove(pid, out _);
    }

    // ---------------- ETW-Sitzung ----------------

    private void Start(string names, bool filterByName, CancellationToken ct)
    {
        // Übrig gebliebene Sitzung (z. B. nach Absturz) beenden
        using (var old = Properties(out var oldPtr)) ControlTraceW(0, SessionName, oldPtr, EVENT_TRACE_CONTROL_STOP);

        using var props = Properties(out var p);
        var rc = StartTraceW(out var session, SessionName, p);
        if (rc != 0) throw new InvalidOperationException($"ETW-Sitzung nicht startbar ({new Win32Exception(rc).Message}). Der Dienst braucht Administrator-/SYSTEM-Rechte.");
        _session = session;

        // Nur in den genannten Programmen mitschreiben (Filter nach Programmname, ab Windows 8.1 / Server 2012 R2)
        var filterText = Marshal.StringToHGlobalUni(names);
        var filter = Marshal.AllocHGlobal(16);
        var parameters = Marshal.AllocHGlobal(48);
        try
        {
            Marshal.WriteInt64(filter, 0, filterText.ToInt64());
            Marshal.WriteInt32(filter, 8, (names.Length + 1) * 2);
            Marshal.WriteInt32(filter, 12, unchecked((int)EVENT_FILTER_TYPE_EXECUTABLE_NAME));
            for (var i = 0; i < 48; i += 4) Marshal.WriteInt32(parameters, i, 0);
            Marshal.WriteInt32(parameters, 0, 2);                    // Version
            Marshal.WriteInt64(parameters, 32, filter.ToInt64());    // EnableFilterDesc
            Marshal.WriteInt32(parameters, 40, filterByName ? 1 : 0); // FilterDescCount
            var provider = RpcProvider;
            rc = EnableTraceEx2(_session, ref provider, EVENT_CONTROL_CODE_ENABLE_PROVIDER, 4, 0, 0, 0, parameters);
            if (rc != 0) throw new InvalidOperationException($"RPC-Ereignisse nicht aktivierbar ({new Win32Exception(rc).Message}).");
        }
        finally { Marshal.FreeHGlobal(parameters); Marshal.FreeHGlobal(filter); Marshal.FreeHGlobal(filterText); }

        // Echtzeit-Verbraucher
        _callback = OnEvent;
        var logfile = Marshal.AllocHGlobal(LogfileSize);
        var loggerName = Marshal.StringToHGlobalUni(SessionName);
        try
        {
            for (var i = 0; i < LogfileSize; i += 8) Marshal.WriteInt64(logfile, i, 0);
            Marshal.WriteIntPtr(logfile, 8, loggerName);                                                     // LoggerName
            Marshal.WriteInt32(logfile, 28, unchecked((int)(PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD)));
            Marshal.WriteIntPtr(logfile, 424, Marshal.GetFunctionPointerForDelegate(_callback));            // EventRecordCallback
            _trace = OpenTraceW(logfile);
            if (_trace == INVALID_PROCESSTRACE_HANDLE) { _trace = 0; throw new InvalidOperationException("ETW-Verbraucher nicht startbar (" + new Win32Exception(Marshal.GetLastWin32Error()).Message + ")."); }
        }
        finally { Marshal.FreeHGlobal(loggerName); Marshal.FreeHGlobal(logfile); }

        var trace = _trace;
        var thread = new Thread(() =>
        {
            var handles = new[] { trace };
            var r = ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
            if (r != 0 && !ct.IsCancellationRequested) { Problem = $"Überwachung beendet ({new Win32Exception(r).Message})."; _trace = 0; }
        }) { IsBackground = true, Name = "Belegpost Druckherkunft" };
        thread.Start();
        _log.LogInformation("Druckherkunft: überwache {Names}", names);
    }

    private void Stop()
    {
        if (_trace != 0) { CloseTrace(_trace); _trace = 0; }
        if (_session != 0)
        {
            using (var props = Properties(out var p)) ControlTraceW(_session, null, p, EVENT_TRACE_CONTROL_STOP);
            _session = 0;
        }
        _activeNames = null;
    }

    /// <summary>EVENT_TRACE_PROPERTIES für eine Echtzeit-Sitzung (Puffer mit Platz für den Sitzungsnamen).</summary>
    private static Buffer Properties(out IntPtr p)
    {
        const int size = 120 + 1024;
        var b = new Buffer(size);
        p = b.Ptr;
        Marshal.WriteInt32(p, 0, size);                                // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 2);                                  // Wnode.ClientContext = Systemzeit
        Marshal.WriteInt32(p, 44, unchecked((int)WNODE_FLAG_TRACED_GUID));
        Marshal.WriteInt32(p, 48, 64);                                 // BufferSize (KB)
        Marshal.WriteInt32(p, 64, unchecked((int)EVENT_TRACE_REAL_TIME_MODE));
        Marshal.WriteInt32(p, 68, 1);                                  // FlushTimer (s)
        Marshal.WriteInt32(p, 116, 120);                               // LoggerNameOffset
        return b;
    }

    private void OnEvent(IntPtr record)
    {
        try
        {
            var provider = Marshal.PtrToStructure<Guid>(record + 24);
            if (provider != RpcProvider || (ushort)Marshal.ReadInt16(record, 40) != RpcClientCallStart) return;
            var pid = Marshal.ReadInt32(record, 12);
            var utc = DateTime.FromFileTimeUtc(Marshal.ReadInt64(record, 16));
            var length = (ushort)Marshal.ReadInt16(record, 86);
            var data = Marshal.ReadIntPtr(record, 96);
            if (data == IntPtr.Zero || length < 24) return;
            var iface = Marshal.PtrToStructure<Guid>(data);
            var procNum = Marshal.ReadInt32(data, 16);
            // danach: Protocol (4), NetworkAddress, Endpoint (je nullterminiert UTF-16)
            var offset = 24;
            ReadString(data, length, ref offset); // NetworkAddress
            var endpoint = ReadString(data, length, ref offset) ?? "";

            var proc = _processes.GetOrAdd(pid, id => { var exe = ProcessName(id); return (exe, _names.Contains(exe) ? CommandLine(id) : null, DateTime.UtcNow); });
            if (!_names.Contains(proc.Exe)) return; // ohne Filter: andere Programme ignorieren
            _seenInterfaces.AddOrUpdate((iface, endpoint), 1, (_, n) => n + 1);
            var call = new RpcCall(utc, pid, proc.Exe, iface, procNum, endpoint, proc.CommandLine,
                PrintOriginMatcher.OperatorFromCommandLine(proc.CommandLine, _opt.CurrentValue.ProcessUserArgument));
            if (PrintOriginMatcher.IsPrintCall(call)) _calls.Enqueue(call);
        }
        catch { /* ein kaputtes Ereignis darf die Überwachung nicht beenden */ }
    }

    private static string? ReadString(IntPtr data, int length, ref int offset)
    {
        if (offset >= length) return null;
        var start = offset;
        while (offset + 1 < length && Marshal.ReadInt16(data, offset) != 0) offset += 2;
        var s = Marshal.PtrToStringUni(data + start, (offset - start) / 2);
        offset += 2;
        return s;
    }

    // ---------------- Prozesse ----------------

    private static string ProcessName(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.ProcessName + ".exe"; }
        catch { return "?"; }
    }

    /// <summary>Befehlszeile eines Prozesses (NtQueryInformationProcess, ProcessCommandLineInformation).</summary>
    public static string? CommandLine(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var size = 4096;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    var status = NtQueryInformationProcess(h, 60, buf, size, out var needed);
                    if (status == 0)
                    {
                        var len = (ushort)Marshal.ReadInt16(buf, 0);
                        var ptr = Marshal.ReadIntPtr(buf, IntPtr.Size == 8 ? 8 : 4);
                        return len == 0 || ptr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(ptr, len / 2);
                    }
                    if (needed <= size) return null;
                    size = needed;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return null;
        }
        finally { CloseHandle(h); }
    }

    // ---------------- Win32 ----------------

    private sealed class Buffer : IDisposable
    {
        public readonly IntPtr Ptr;
        public Buffer(int size) { Ptr = Marshal.AllocHGlobal(size); for (var i = 0; i < size; i += 4) Marshal.WriteInt32(Ptr, i, 0); }
        public void Dispose() => Marshal.FreeHGlobal(Ptr);
    }

    /// <summary>sizeof(EVENT_TRACE_LOGFILEW) unter x64; EventRecordCallback liegt bei Offset 424.</summary>
    private const int LogfileSize = 448;
    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000, EVENT_TRACE_REAL_TIME_MODE = 0x100, EVENT_TRACE_CONTROL_STOP = 1,
        EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1, EVENT_FILTER_TYPE_EXECUTABLE_NAME = 0x80000008,
        PROCESS_TRACE_MODE_REAL_TIME = 0x100, PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const ulong INVALID_PROCESSTRACE_HANDLE = ulong.MaxValue;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void EventRecordCallback(IntPtr record);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int StartTraceW(out ulong handle, string name, IntPtr properties);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int ControlTraceW(ulong handle, string? name, IntPtr properties, uint control);
    [DllImport("advapi32.dll")] private static extern int EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level, ulong anyKeyword, ulong allKeyword, uint timeout, IntPtr parameters);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern ulong OpenTraceW(IntPtr logfile);
    [DllImport("advapi32.dll")] private static extern int ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] private static extern int CloseTrace(ulong handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);
}
