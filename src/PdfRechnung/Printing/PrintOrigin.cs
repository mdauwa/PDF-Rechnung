namespace PdfRechnung.Printing;

/// <summary>Ein RPC-Aufruf eines überwachten Prozesses (z. B. wwa.exe) – Druckwarteschlange oder 32-Bit-Druckhelfer.</summary>
public sealed record RpcCall(DateTime Utc, int Pid, string Exe, Guid Interface, int ProcNum, string Endpoint, string? CommandLine, string? Operator);

/// <summary>Von welchem Prozess stammt ein Druckauftrag? Operator = ERP-Bedienernummer aus der Befehlszeile.</summary>
public sealed record PrintOriginResult(string? Operator, int? Pid, string? Exe, string? CommandLine, string? Warning, string Explanation);

/// <summary>
/// Ordnet einen Druckauftrag dem druckenden Prozess zu. Windows merkt sich im Druckauftrag nicht, welcher Prozess gedruckt hat –
/// bei der WebWARE laufen alle wwa.exe als SYSTEM. Deshalb werden die Aufrufe überwachter Prozesse an die Druckwarteschlange
/// (RPC-Schnittstelle winspool bzw. der 32-Bit-Druckhelfer splwow64/„UmpdProxy“) mitgeschrieben und zeitlich zugeordnet:
/// Der Aufruf, der dem Einreichen des Druckauftrags am nächsten liegt, gewinnt.
/// </summary>
public static class PrintOriginMatcher
{
    /// <summary>RPC-Schnittstelle der Druckwarteschlange (MS-RPRN).</summary>
    public static readonly Guid Winspool = new("12345678-1234-abcd-ef00-0123456789ab");

    /// <summary>Aufrufe, die zum Drucken gehören: Druckwarteschlange oder Druckhelfer.</summary>
    public static bool IsPrintCall(RpcCall c) =>
        c.Interface == Winspool
        || c.Endpoint.Contains("spool", StringComparison.OrdinalIgnoreCase)
        || c.Endpoint.Contains("umpd", StringComparison.OrdinalIgnoreCase);

    /// <param name="before">So lange vor dem Einreichen zählen Aufrufe (Druckdialog, Aufbereitung).</param>
    /// <param name="after">So lange nach dem Einreichen (Zeitstempel-Ungenauigkeit, Seiten senden).</param>
    /// <param name="ambiguity">Liegt ein Aufruf eines anderen Benutzers so nah am besten Treffer, ist die Zuordnung unsicher.</param>
    public static PrintOriginResult Match(IEnumerable<RpcCall> calls, DateTime submittedUtc, TimeSpan before, TimeSpan after, TimeSpan ambiguity)
    {
        var candidates = calls.Where(IsPrintCall)
            .Where(c => c.Utc >= submittedUtc - before && c.Utc <= submittedUtc + after)
            .Select(c => (Call: c, Distance: (c.Utc - submittedUtc).Duration()))
            .OrderBy(x => x.Distance)
            .ToList();
        if (candidates.Count == 0)
            return new PrintOriginResult(null, null, null, null, null, "Kein überwachter Prozess hat zu dieser Zeit gedruckt.");

        var best = candidates[0];
        var others = candidates.Skip(1)
            .Where(x => x.Distance - best.Distance <= ambiguity && x.Call.Pid != best.Call.Pid && x.Call.Operator != best.Call.Operator)
            .Select(x => x.Call).DistinctBy(c => c.Pid).ToList();
        var who = Describe(best.Call);
        if (best.Call.Operator is null)
            return new PrintOriginResult(null, best.Call.Pid, best.Call.Exe, best.Call.CommandLine,
                $"Druck von {who}, aber in der Befehlszeile steht keine gültige Benutzernummer.", who);
        string? warning = others.Count == 0 ? null
            : $"Benutzer aus dem Prozess nicht eindeutig: gleichzeitig gedruckt haben auch {string.Join(", ", others.Select(Describe))}. Zugeordnet wurde {who}.";
        return new PrintOriginResult(best.Call.Operator, best.Call.Pid, best.Call.Exe, best.Call.CommandLine, warning, who);
    }

    private static string Describe(RpcCall c) => $"{c.Exe} {c.Operator ?? "?"} (PID {c.Pid})";

    /// <summary>Benutzernummer aus der Befehlszeile: Parameter Nr. <paramref name="index"/> (1 = erster nach dem Programm), z. B. „000“ bei <c>WWA.EXE 000 /M:10000 …</c>.</summary>
    public static string? OperatorFromCommandLine(string? commandLine, int index)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || index < 1) return null;
        var args = SplitArgs(commandLine);
        return index < args.Count ? Settings.UserStore.NormalizeOperator(args[index]) : null;
    }

    /// <summary>Befehlszeile in Argumente zerlegen (Anführungszeichen wie Windows).</summary>
    public static List<string> SplitArgs(string commandLine)
    {
        var list = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (var ch in commandLine)
        {
            if (ch == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (any) { list.Add(sb.ToString()); sb.Clear(); any = false; }
                continue;
            }
            sb.Append(ch); any = true;
        }
        if (any) list.Add(sb.ToString());
        return list;
    }

    /// <summary>Programmnamen aus der Einstellung („wwa.exe; andere.exe“).</summary>
    public static List<string> ProcessNames(string? setting) =>
        (setting ?? "").Split(new[] { ';', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
