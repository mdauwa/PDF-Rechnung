using System.Text.Json;
using PdfRechnung.Documents;

namespace PdfRechnung.Delivery;

/// <summary>Kunden mit Versandwegen und Regeln, gespeichert in daten\kunden.json.</summary>
public sealed class CustomerStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private readonly object _lock = new();
    private readonly string _path;
    private List<Customer> _items;

    public CustomerStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "kunden.json");
        _items = Load();
    }

    private List<Customer> Load()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<List<Customer>>(File.ReadAllText(_path), Json) ?? new() : new(); }
        catch { return new(); }
    }

    public List<Customer> All() { lock (_lock) return Clone(_items); }

    public Customer? Get(string id) { lock (_lock) return Clone(_items.FirstOrDefault(c => c.Id == id)); }

    public Customer Save(Customer c)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(c.Id)) c.Id = Guid.NewGuid().ToString("N")[..10];
            foreach (var ch in c.Channels.Where(ch => string.IsNullOrWhiteSpace(ch.Id))) ch.Id = Guid.NewGuid().ToString("N")[..8];
            var i = _items.FindIndex(x => x.Id == c.Id);
            if (i >= 0) _items[i] = Clone(c)!; else _items.Add(Clone(c)!);
            Write();
            return Clone(c)!;
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var n = _items.RemoveAll(c => c.Id == id);
            if (n > 0) Write();
            return n > 0;
        }
    }

    private void Write()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_items, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>
    /// Kunde eines Belegs: zuerst die Kundennummer (ggf. nur im passenden Mandanten), sonst eine der Empfängeradressen.
    /// </summary>
    public (Customer? Customer, string? Reason) Find(Vorgang v)
    {
        lock (_lock)
        {
            var active = _items.Where(c => c.Active).ToList();
            bool MandantOk(Customer c) => string.IsNullOrWhiteSpace(c.Mandant) || MandantOptions.Same(c.Mandant, v.Mandant);
            if (!string.IsNullOrWhiteSpace(v.BuyerId))
            {
                var byNo = active.Where(c => !string.IsNullOrWhiteSpace(c.Number) && SameNumber(c.Number, v.BuyerId!) && MandantOk(c))
                    .OrderByDescending(c => !string.IsNullOrWhiteSpace(c.Mandant)).FirstOrDefault();
                if (byNo is not null) return (Clone(byNo), $"Kundennummer {v.BuyerId}");
            }
            var mails = v.Mail.To.Concat(v.MapiTo).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
            foreach (var c in active.Where(MandantOk))
                foreach (var m in mails)
                    if (c.Emails.Any(p => !string.IsNullOrWhiteSpace(p) && MailRuleEngine.Like(m, p.Trim())))
                        return (Clone(c), $"E-Mail-Adresse {m}");
            return (null, null);
        }
    }

    /// <summary>Kundennummern vergleichen; rein numerisch ohne Vornullen.</summary>
    public static bool SameNumber(string a, string b) =>
        string.Equals(MandantOptions.Key(a), MandantOptions.Key(b), StringComparison.OrdinalIgnoreCase);

    private static List<Customer> Clone(List<Customer> l) => JsonSerializer.Deserialize<List<Customer>>(JsonSerializer.Serialize(l, Json), Json)!;
    private static Customer? Clone(Customer? c) => c is null ? null : JsonSerializer.Deserialize<Customer>(JsonSerializer.Serialize(c, Json), Json);
}
