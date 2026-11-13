using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PdfRechnung.Services;

/// <summary>
/// Anmeldung aus dem Firmenportal übernehmen („Übergabe-Token“).
/// Das Firmenportal erzeugt ein kurzlebiges JWT (HS256, gemeinsamer Schlüssel) und ruft
///   https://belegpost:5444/auth/firmenportal?token=…&amp;ziel=/%23/vorgang%3Fid%3D…
/// auf – direkt oder im eingebetteten Rahmen (iframe). Belegpost prüft Signatur, Ablauf, Aussteller und Empfänger,
/// sucht den Benutzer über E-Mail/UPN und meldet ihn an. Jedes Token gilt nur einmal (jti).
/// </summary>
public static class HandoverLogin
{
    public const string Issuer = "Firmenportal", Audience = "Belegpost";
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Used = new();

    public sealed record Result(bool Ok, string? Error, IReadOnlyList<string> Names, string? DisplayName);

    /// <summary>Neuer gemeinsamer Schlüssel (256 Bit, Base64url).</summary>
    public static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static Result Validate(string? token, string secret, DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(token)) return Fail("Kein Übergabe-Token.");
        var parts = token.Trim().Split('.');
        if (parts.Length != 3) return Fail("Übergabe-Token ist ungültig.");
        JsonElement header, payload;
        try
        {
            header = JsonDocument.Parse(B64(parts[0])).RootElement;
            payload = JsonDocument.Parse(B64(parts[1])).RootElement;
        }
        catch { return Fail("Übergabe-Token ist ungültig."); }
        if (Str(header, "alg") != "HS256") return Fail("Übergabe-Token: nur HS256 wird angenommen.");

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
        byte[] sig;
        try { sig = B64(parts[2]); } catch { return Fail("Übergabe-Token ist ungültig."); }
        if (!CryptographicOperations.FixedTimeEquals(expected, sig)) return Fail("Übergabe-Token: Signatur passt nicht (gemeinsamer Schlüssel unterschiedlich?).");

        if (Str(payload, "iss") != Issuer) return Fail($"Übergabe-Token: Aussteller muss „{Issuer}“ sein.");
        var aud = payload.TryGetProperty("aud", out var a) ? a : default;
        var audOk = aud.ValueKind == JsonValueKind.String ? aud.GetString() == Audience
            : aud.ValueKind == JsonValueKind.Array && aud.EnumerateArray().Any(x => x.GetString() == Audience);
        if (!audOk) return Fail($"Übergabe-Token: Empfänger muss „{Audience}“ sein.");

        var exp = Num(payload, "exp");
        var iat = Num(payload, "iat");
        var skew = TimeSpan.FromSeconds(60);
        if (exp is null || DateTimeOffset.FromUnixTimeSeconds(exp.Value) + skew < t) return Fail("Übergabe-Token ist abgelaufen – bitte im Firmenportal erneut öffnen.");
        if (iat is not null && DateTimeOffset.FromUnixTimeSeconds(iat.Value) - skew > t) return Fail("Übergabe-Token: Uhrzeit in der Zukunft (Serverzeit prüfen).");
        if (DateTimeOffset.FromUnixTimeSeconds(exp.Value) - (iat is null ? t : DateTimeOffset.FromUnixTimeSeconds(iat.Value)) > MaxAge + skew)
            return Fail("Übergabe-Token gilt zu lange (höchstens 5 Minuten).");

        var jti = Str(payload, "jti");
        if (string.IsNullOrEmpty(jti)) return Fail("Übergabe-Token: jti fehlt.");
        foreach (var old in Used.Where(x => x.Value < t - TimeSpan.FromMinutes(15)).Select(x => x.Key).ToList()) Used.TryRemove(old, out _);
        if (!Used.TryAdd(jti, t)) return Fail("Übergabe-Token wurde schon verwendet.");

        var names = new[] { "email", "preferred_username", "upn", "unique_name", "sub" }
            .Select(n => Str(payload, n)).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0) return Fail("Übergabe-Token enthält keinen Benutzer (email/upn).");
        return new Result(true, null, names, Str(payload, "name"));
    }

    /// <summary>Token erzeugen – so wie das Firmenportal es tun soll (für Tests und als Vorlage).</summary>
    public static string Create(string secret, string email, string? name = null, DateTimeOffset? now = null, TimeSpan? life = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        string Enc(object o) => B64Url(JsonSerializer.SerializeToUtf8Bytes(o));
        var head = Enc(new { alg = "HS256", typ = "JWT" });
        var body = Enc(new
        {
            iss = Issuer, aud = Audience, email, name, jti = Guid.NewGuid().ToString("N"),
            iat = t.ToUnixTimeSeconds(), exp = (t + (life ?? TimeSpan.FromMinutes(2))).ToUnixTimeSeconds()
        });
        var sig = B64Url(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(head + "." + body)));
        return head + "." + body + "." + sig;
    }

    private static Result Fail(string e) => new(false, e, Array.Empty<string>(), null);
    private static string? Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Num(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;
    private static byte[] B64(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }
    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Herkunft (Schema://Host:Port) einer Adresse, für frame-ancestors.</summary>
    public static string? Origin(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http") ? u.GetLeftPart(UriPartial.Authority) : null;
}
