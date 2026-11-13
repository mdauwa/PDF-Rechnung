using System.Net;
using System.Security.Claims;
using PdfRechnung.Settings;

namespace PdfRechnung.Services;

/// <summary>Wer ist angemeldet und was darf er? Lokaler Administrator (nur direkt am Server) und Portal-Admins dürfen alles.</summary>
public sealed record AccessInfo(bool Authenticated, bool IsAdmin, bool IsLocalAdmin, bool CanSend, string? UserId, string Name, string? Login,
    bool SeeAll = false, IReadOnlySet<string>? Visible = null)
{
    public static readonly AccessInfo Anonymous = new(false, false, false, false, null, "", null);

    /// <summary>Darf diesen Beleg sehen? Admins und „alle“ sehen alles, sonst nur eigene (und freigegebene) Belege.</summary>
    public bool CanSee(PdfRechnung.Documents.Vorgang v) =>
        Authenticated && (SeeAll || v.OwnerUserId is { } o && Visible is not null && Visible.Contains(o));
}

public sealed class PortalAccess
{
    public const string Scheme = "portal";
    public const string EntraScheme = "entra";
    public const string ClaimUser = "uid", ClaimKind = "kind", ClaimLogin = "login";

    private readonly UserStore _users;
    public PortalAccess(UserStore users) => _users = users;

    public static bool IsLocalRequest(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

    public AccessInfo For(HttpContext ctx)
    {
        if (ctx.Items.TryGetValue(typeof(AccessInfo), out var cached) && cached is AccessInfo c) return c;
        var info = Resolve(ctx.User, IsLocalRequest(ctx));
        ctx.Items[typeof(AccessInfo)] = info;
        return info;
    }

    /// <summary>Rechte eines Portal-Benutzers (z. B. für den Melder, der mit Schlüssel statt Cookie kommt).</summary>
    public static AccessInfo ForUser(PortalUser pu, string? login = null)
    {
        var seeAll = pu.IsAdmin || pu.Visibility == "alle";
        var visible = new HashSet<string> { pu.Id };
        if (pu.Visibility == "auswahl") visible.UnionWith(pu.AlsoSees);
        return new AccessInfo(true, pu.IsAdmin, false, pu.IsAdmin || pu.CanSend, pu.Id, pu.Name, login ?? pu.Email, seeAll, visible);
    }

    private AccessInfo Resolve(ClaimsPrincipal user, bool local)
    {
        if (user.Identity?.IsAuthenticated != true) return AccessInfo.Anonymous;
        var kind = user.FindFirst(ClaimKind)?.Value;
        if (kind == "local")
            return local ? new AccessInfo(true, true, true, true, null, "Administrator (Server)", null, SeeAll: true) : AccessInfo.Anonymous;

        var pu = _users.FindById(user.FindFirst(ClaimUser)?.Value);
        if (pu is null || !pu.Active) return AccessInfo.Anonymous;
        var login = user.FindFirst(ClaimLogin)?.Value ?? pu.Email;
        return ForUser(pu, login);
    }

    public static ClaimsPrincipal Principal(string kind, string name, string? userId = null, string? login = null)
    {
        var claims = new List<Claim> { new(ClaimKind, kind), new(ClaimTypes.Name, name) };
        if (userId is not null) claims.Add(new Claim(ClaimUser, userId));
        if (login is not null) claims.Add(new Claim(ClaimLogin, login));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme, ClaimTypes.Name, ClaimTypes.Role));
    }
}
