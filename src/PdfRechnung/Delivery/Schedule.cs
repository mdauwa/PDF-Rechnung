using System.Globalization;
using System.Text.RegularExpressions;

namespace PdfRechnung.Delivery;

/// <summary>
/// Zeitplan für Abruf bzw. Ausgangsordner eines Versandwegs.
/// Mode: "" = Standard bzw. wie global · sofort (laufend, ca. alle 15 s) · intervall (alle n Minuten, optional Zeitfenster) · zeiten (feste Uhrzeiten) · aus.
/// </summary>
public sealed class Schedule
{
    public string Mode { get; set; } = "";
    /// <summary>intervall: alle n Minuten.</summary>
    public int Minutes { get; set; }
    /// <summary>intervall: Zeitfenster von/bis (HH:mm). Leer = ganztägig. Über Mitternacht möglich (22:00–06:00).</summary>
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    /// <summary>zeiten: Uhrzeiten, z. B. „07:00; 12:00; 17:30“.</summary>
    public string Times { get; set; } = "";
    /// <summary>Wochentage, z. B. „Mo-Fr“, „Mo,Mi,Fr“, „Sa,So“. Leer = täglich.</summary>
    public string Days { get; set; } = "";

    public Schedule Copy() => (Schedule)MemberwiseClone();
}

public static class ScheduleEngine
{
    public const string Now_ = "sofort", Interval = "intervall", Times = "zeiten", Off = "aus";
    private static readonly string[] DayNames = { "mo", "di", "mi", "do", "fr", "sa", "so" };

    /// <summary>Eigener Zeitplan, sonst global, sonst Standard.</summary>
    public static Schedule Merge(Schedule? global, Schedule? own, Schedule fallback) =>
        own is { Mode.Length: > 0 } ? own.Copy() : global is { Mode.Length: > 0 } ? global.Copy() : fallback;

    /// <summary>Wochentage 1 = Montag … 7 = Sonntag; leer = alle.</summary>
    public static HashSet<int> ParseDays(string? s)
    {
        var set = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(s)) return set;
        foreach (var part in s.ToLowerInvariant().Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var r = part.Split('-', '–');
            int Idx(string x) { var i = Array.FindIndex(DayNames, d => x.Trim().StartsWith(d)); return i < 0 ? -1 : i + 1; }
            var a = Idx(r[0]); var b = r.Length > 1 ? Idx(r[1]) : a;
            if (a < 1 || b < 1) continue;
            for (var d = a; ; d = d % 7 + 1) { set.Add(d); if (d == b) break; }
        }
        return set;
    }

    public static List<TimeOnly> ParseTimes(string? s) =>
        Regex.Matches(s ?? "", @"(\d{1,2})[:.](\d{2})").Select(m => new TimeOnly(Math.Min(23, int.Parse(m.Groups[1].Value)), Math.Min(59, int.Parse(m.Groups[2].Value))))
            .Distinct().Order().ToList();

    private static TimeOnly? T(string? s) => TimeOnly.TryParseExact((s ?? "").Trim().Replace('.', ':'), new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    private static bool DayOk(Schedule s, DateTime d)
    {
        var days = ParseDays(s.Days);
        return days.Count == 0 || days.Contains(d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek);
    }

    private static bool InWindow(Schedule s, DateTime d)
    {
        var f = T(s.From); var t = T(s.To); var now = TimeOnly.FromDateTime(d);
        if (f is null && t is null) return true;
        var from = f ?? TimeOnly.MinValue; var to = t ?? TimeOnly.MaxValue;
        return from <= to ? now >= from && now < to : now >= from || now < to;
    }

    /// <summary>Ist jetzt ein Lauf fällig? last = letzter Lauf (null = noch nie).</summary>
    public static bool Due(Schedule s, DateTime? last, DateTime now)
    {
        switch (s.Mode)
        {
            case Off: return false;
            case Now_: return last is null || now - last.Value >= TimeSpan.FromSeconds(14);
            case Times:
            {
                var slot = LastSlot(s, now);
                return slot is not null && (last is null ? now - slot.Value < TimeSpan.FromMinutes(1) : last.Value < slot.Value);
            }
            default:
                if (!DayOk(s, now) || !InWindow(s, now)) return false;
                return last is null || now - last.Value >= TimeSpan.FromMinutes(Math.Max(1, s.Minutes > 0 ? s.Minutes : 5)) - TimeSpan.FromSeconds(5);
        }
    }

    /// <summary>Letzte geplante Uhrzeit vor jetzt (bis 7 Tage zurück).</summary>
    private static DateTime? LastSlot(Schedule s, DateTime now)
    {
        var times = ParseTimes(s.Times);
        if (times.Count == 0) return null;
        for (var d = 0; d < 8; d++)
        {
            var day = now.Date.AddDays(-d);
            if (!DayOk(s, day)) continue;
            foreach (var t in times.AsEnumerable().Reverse())
            {
                var at = day + t.ToTimeSpan();
                if (at <= now) return at;
            }
        }
        return null;
    }

    /// <summary>Nächster geplanter Lauf (für die Anzeige).</summary>
    public static DateTime? Next(Schedule s, DateTime? last, DateTime now)
    {
        switch (s.Mode)
        {
            case Off: return null;
            case Now_: return now;
            case Times:
            {
                var times = ParseTimes(s.Times);
                for (var d = 0; d < 8 && times.Count > 0; d++)
                {
                    var day = now.Date.AddDays(d);
                    if (!DayOk(s, day)) continue;
                    foreach (var t in times) { var at = day + t.ToTimeSpan(); if (at > now) return at; }
                }
                return null;
            }
            default:
            {
                var step = TimeSpan.FromMinutes(Math.Max(1, s.Minutes > 0 ? s.Minutes : 5));
                var c = last is null ? now : last.Value + step;
                if (c < now) c = now;
                if (DayOk(s, c) && InWindow(s, c)) return c;
                // sonst: nächster Fensterbeginn an einem erlaubten Tag
                var start = T(s.From) ?? TimeOnly.MinValue;
                for (var d = 0; d < 8; d++)
                {
                    var at = c.Date.AddDays(d) + start.ToTimeSpan();
                    if (at > c && DayOk(s, at) && InWindow(s, at)) return at;
                }
                return null;
            }
        }
    }

    public static string Describe(Schedule s)
    {
        var days = string.IsNullOrWhiteSpace(s.Days) ? "" : $" ({s.Days.Trim()})";
        return s.Mode switch
        {
            Off => "aus",
            Now_ => "sofort",
            Times => $"um {string.Join(", ", ParseTimes(s.Times).Select(t => t.ToString("HH:mm")))}{days}",
            _ => $"alle {(s.Minutes > 0 ? s.Minutes : 5)} Min.{(T(s.From) is not null || T(s.To) is not null ? $" von {s.From} bis {s.To}" : "")}{days}"
        };
    }
}
