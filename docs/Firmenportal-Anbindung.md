# Belegpost im Firmenportal einbinden (Anmeldung übergeben)

Belegpost kann im internen Firmenportal angezeigt werden (Menüpunkt, Link oder `iframe`). Die Anmeldung übernimmt das Firmenportal: Es erzeugt pro Aufruf ein kurzlebiges, signiertes Übergabe-Token. Belegpost prüft das Token und meldet den Benutzer an. Eine zweite Anmeldung ist nicht nötig.

## Einrichtung in Belegpost

Einstellungen → **Anmeldung** → „Einbindung ins Firmenportal“:

1. Anmeldung aus dem Firmenportal übernehmen und Einbettung erlauben: **an**.
2. **Adresse des Firmenportals**, z. B. `https://firmenportal.erpaustria.com:5443`. Nur diese Herkunft darf Belegpost einbetten (`Content-Security-Policy: frame-ancestors`).
3. **Schlüssel erzeugen** und kopieren. Der Schlüssel wird nur einmal angezeigt.
4. Speichern und den Dienst neu starten. Der Neustart ist nötig, weil das Anmelde-Cookie für die Einbettung `SameSite=None` braucht.
5. Optional: „Unbekannte Benutzer automatisch anlegen“. Neue Benutzer sehen nur eigene Belege.

Die Zuordnung läuft über die E-Mail bzw. den Anmeldenamen unter Einstellungen → **Benutzer**.

## Was das Firmenportal tun muss

Für den angemeldeten Benutzer ein JWT erzeugen:

| Teil | Wert |
|---|---|
| Header | `{"alg":"HS256","typ":"JWT"}` |
| `iss` | `"Firmenportal"` |
| `aud` | `"Belegpost"` |
| `email` | E-Mail / UPN des Benutzers (alternativ `upn` oder `preferred_username`) |
| `name` | Anzeigename (optional) |
| `jti` | eindeutige ID, jedes Token gilt nur **einmal** |
| `iat`, `exp` | Unix-Sekunden; `exp` höchstens 5 Minuten nach `iat` (empfohlen: 2 Minuten) |
| Signatur | HMAC-SHA256 mit dem gemeinsamen Schlüssel (UTF-8 des Schlüsseltexts) |

Danach aufrufen bzw. als `iframe`-Quelle setzen:

```
https://BELEGPOST:5444/auth/firmenportal?token=<JWT>&ziel=<Ziel, URL-kodiert>
```

Mögliche Ziele (beginnen immer mit `/`):

- `/#/vorgaenge?f=meine`: meine Belege
- `/#/vorgaenge?f=offen`: zu prüfen
- `/#/vorgang?id=20261113-171904-b5cea1`: ein bestimmter Vorgang

Das Token wird bei jedem Öffnen neu erzeugt, es darf nicht wiederverwendet werden. Eingebettet blendet Belegpost die eigene Marke und „Abmelden“ aus. Läuft die Anmeldung ab, steht im Rahmen der Hinweis „bitte im Firmenportal neu öffnen“.

### Beispiel (C#, ASP.NET Core im Firmenportal)

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

static string BelegpostToken(string secret, string email, string? name)
{
    var now = DateTimeOffset.UtcNow;
    var head = B64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
    var body = B64Url(JsonSerializer.SerializeToUtf8Bytes(new
    {
        iss = "Firmenportal", aud = "Belegpost", email, name,
        jti = Guid.NewGuid().ToString("N"),
        iat = now.ToUnixTimeSeconds(), exp = now.AddMinutes(2).ToUnixTimeSeconds()
    }));
    var sig = B64Url(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(head + "." + body)));
    return head + "." + body + "." + sig;
}

// Endpunkt im Firmenportal: leitet den angemeldeten Benutzer weiter (auch als iframe-src verwendbar)
app.MapGet("/belegpost", (HttpContext ctx, string? ziel) =>
{
    var email = ctx.User.FindFirst("preferred_username")?.Value ?? ctx.User.FindFirst(ClaimTypes.Email)?.Value;
    if (email is null) return Results.Unauthorized();
    var token = BelegpostToken(belegpostSecret, email, ctx.User.Identity?.Name);
    var target = string.IsNullOrEmpty(ziel) ? "/#/vorgaenge?f=meine" : ziel;
    return Results.Redirect($"{belegpostUrl}/auth/firmenportal?token={token}&ziel={Uri.EscapeDataString(target)}");
});
```

Im Firmenportal dann z. B. `<iframe src="/belegpost?ziel=%2F%23%2Fvorgaenge%3Ff%3Dmeine" style="width:100%;height:100%;border:0"></iframe>`.

Belegpost selbst enthält dieselbe Logik als Referenz: `HandoverLogin.Create` und `HandoverLogin.Validate` in `src/PdfRechnung/Services/HandoverLogin.cs`.

## Hinweise

- **Zertifikat:** Belegpost und Firmenportal sollten ein im Browser vertrauenswürdiges Zertifikat haben. Bei einem selbst signierten Belegpost-Zertifikat bleibt der Rahmen sonst leer.
- **Microsoft-Anmeldung im Rahmen:** Microsoft lässt sich nicht in einem `iframe` anzeigen. Deshalb ist die Übergabe über das Firmenportal der Weg für die Einbettung. Direkt aufgerufen funktioniert die Microsoft-Anmeldung weiterhin.
- **Fehler:** Abgelehnte Tokens stehen im Protokoll als „Firmenportal-Übergabe abgelehnt: …“, zum Beispiel bei falschem Schlüssel, abgelaufenem Token oder doppelt verwendetem `jti`.
