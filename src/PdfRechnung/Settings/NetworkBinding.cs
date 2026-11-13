using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PdfRechnung.Settings;

/// <summary>Bindung des Portals an bestimmte IP-Adressen (Einstellungen → Server &amp; Zertifikat).</summary>
public static class NetworkBinding
{
    /// <summary>IPv4- und IPv6-Adressen dieses Servers (ohne Loopback und Link-Local).</summary>
    public static List<string> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork || a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal)
                .OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6).ThenBy(a => a.ToString())
                .Select(a => a.ToString()).Distinct().ToList();
        }
        catch { return new(); }
    }

    /// <summary>
    /// Liefert die Adressen zum Binden oder null (= alle Adressen). Adressen, die es auf dem Server nicht (mehr) gibt,
    /// werden übersprungen, damit der Dienst trotzdem startet; bleibt keine übrig, wird an alle gebunden.
    /// </summary>
    public static List<IPAddress>? Resolve(IEnumerable<string>? configured, out List<string> warnings)
    {
        warnings = new();
        var wanted = (configured ?? Enumerable.Empty<string>()).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (wanted.Count == 0) return null;
        var present = LocalAddresses().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<IPAddress> { IPAddress.Loopback };
        foreach (var w in wanted)
        {
            if (!IPAddress.TryParse(w, out var ip)) { warnings.Add($"„{w}“ ist keine gültige IP-Adresse – übersprungen."); continue; }
            if (IPAddress.IsLoopback(ip)) continue;
            if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return null;
            if (present.Count > 0 && !present.Contains(ip.ToString())) { warnings.Add($"IP-Adresse {ip} gibt es auf diesem Server nicht – übersprungen."); continue; }
            if (!result.Contains(ip)) result.Add(ip);
        }
        if (result.Count == 1) { warnings.Add("Keine der eingestellten IP-Adressen ist vorhanden – das Portal ist an alle Adressen gebunden."); return null; }
        return result;
    }
}
