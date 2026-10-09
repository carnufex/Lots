namespace Lots.Shell.Core.Policy;

/// <summary>Resolves who is calling the API. OIDC will provide the real implementation (see #16).</summary>
public interface ICurrentPrincipal
{
    Principal Get(HttpContext http);
}

/// <summary>
/// Development stand-in: the user comes from configuration (<c>Auth:Dev:UserId</c>, <c>Auth:Dev:Roles</c>).
/// Only when <c>Auth:Dev:AllowHeaders</c> is true may a caller pick another identity with the
/// <c>X-Dev-User</c> / <c>X-Dev-Roles</c> headers. Never enable that outside local development.
/// </summary>
public sealed class DevPrincipal(IConfiguration config) : ICurrentPrincipal
{
    public Principal Get(HttpContext http)
    {
        var user = config["Auth:Dev:UserId"] ?? "dev";
        var roles = config["Auth:Dev:Roles"] ?? "operator";

        if (config.GetValue("Auth:Dev:AllowHeaders", false))
        {
            if (http.Request.Headers.TryGetValue("X-Dev-User", out var u) && !string.IsNullOrWhiteSpace(u)) user = u.ToString();
            if (http.Request.Headers.TryGetValue("X-Dev-Roles", out var r)) roles = r.ToString();
        }

        return new Principal(user, roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
