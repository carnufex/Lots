using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Core.Policy;

/// <summary>Resolves who is calling the API from the authenticated user.</summary>
public interface ICurrentPrincipal
{
    Principal Get(HttpContext http);
}

/// <summary>Claim names used to read identity, shared by the OIDC and the dev scheme.</summary>
public sealed class AuthClaimNames
{
    public string User { get; init; } = "sub";
    public string Roles { get; init; } = "roles";

    /// <summary>
    /// If set, only values starting with it count as roles and the prefix is removed (e.g. group "lots-admin" with
    /// prefix "lots-" is the role "admin"). Lets an IdP's group claim double as role claim without exposing other groups.
    /// </summary>
    public string? RolePrefix { get; init; }
}

public sealed class ClaimsCurrentPrincipal(AuthClaimNames names) : ICurrentPrincipal
{
    public Principal Get(HttpContext http) =>
        Map(http.User.Claims, names) ?? throw new InvalidOperationException("Authenticated request without a user claim.");

    /// <summary>The user and roles a set of claims maps to (also used to test a mapping with a sample token); null without a user claim.</summary>
    public static Principal? Map(IEnumerable<Claim> claims, AuthClaimNames names)
    {
        var list = claims.ToList();
        var user = list.FirstOrDefault(c => string.Equals(c.Type, names.User, StringComparison.OrdinalIgnoreCase))?.Value;
        if (user is null) return null;
        var roles = list.Where(c => string.Equals(c.Type, names.Roles, StringComparison.OrdinalIgnoreCase))
            .SelectMany(c => c.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct().ToList();
        if (!string.IsNullOrEmpty(names.RolePrefix))
            roles = roles.Where(r => r.StartsWith(names.RolePrefix, StringComparison.OrdinalIgnoreCase))
                .Select(r => r[names.RolePrefix.Length..]).Where(r => r.Length > 0).Distinct().ToList();
        return new Principal(user, roles);
    }
}

/// <summary>
/// Authentication wiring. <c>Auth:Mode</c> is mandatory and explicit (validated at startup):
/// <list type="bullet">
/// <item><c>Oidc</c>: JWT bearer validation against <c>Auth:Oidc:Authority</c> (e.g. Authentik); roles from the roles claim.</item>
/// <item><c>Dev</c>: no IdP. Everyone is the configured dev user. Never use outside local development.</item>
/// </list>
/// Both schemes are registered; a policy scheme picks one per request from the configuration, so nothing is
/// read from configuration before the host is fully built.
/// </summary>
public static class AuthSetup
{
    public const string DevScheme = "LotsDev";
    private const string Selector = "Lots";

    public static void AddLotsAuth(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            return new AuthClaimNames
            {
                User = config["Auth:Oidc:UserClaim"] ?? "sub",
                Roles = config["Auth:Oidc:RoleClaim"] ?? "roles",
                // Only meaningful for real tokens; the dev scheme issues plain role names.
                RolePrefix = IsOidc(config) ? config["Auth:Oidc:RolePrefix"] : null,
            };
        });
        builder.Services.AddSingleton<ICurrentPrincipal, ClaimsCurrentPrincipal>();
        builder.Services.AddAuthorization();

        builder.Services.AddAuthentication(Selector)
            .AddPolicyScheme(Selector, Selector, o =>
                o.ForwardDefaultSelector = ctx =>
                    IsOidc(ctx.RequestServices.GetRequiredService<IConfiguration>())
                        ? JwtBearerDefaults.AuthenticationScheme
                        : DevScheme)
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                // Runs when the options are first built, i.e. after the host configuration is complete.
                var config = builder.Configuration;
                o.Authority = config["Auth:Oidc:Authority"];
                o.MapInboundClaims = false; // keep claim names as issued (sub, roles, ...)
                var audience = config["Auth:Oidc:Audience"];
                o.Audience = audience;
                o.TokenValidationParameters.ValidateAudience = !string.IsNullOrEmpty(audience);
                o.RequireHttpsMetadata = config.GetValue("Auth:Oidc:RequireHttpsMetadata", true);
            })
            .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevScheme, null);
    }

    public static bool IsOidc(IConfiguration config) =>
        string.Equals(config["Auth:Mode"], "oidc", StringComparison.OrdinalIgnoreCase);

    /// <summary>Fails startup unless the mode is explicit and, for Oidc, an authority is configured.</summary>
    public static void Validate(IConfiguration config)
    {
        var mode = config["Auth:Mode"];
        if (string.Equals(mode, "oidc", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(config["Auth:Oidc:Authority"]))
                throw new InvalidOperationException("Auth:Mode is Oidc but Auth:Oidc:Authority is not set.");
        }
        else if (!string.Equals(mode, "dev", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Auth:Mode must be set explicitly to 'Oidc' or 'Dev'.");
        }
    }
}

/// <summary>
/// Development scheme: authenticates every request as the configured user (<c>Auth:Dev:UserId</c>,
/// <c>Auth:Dev:Roles</c>). With <c>Auth:Dev:AllowHeaders</c> a caller may pick another identity via the
/// <c>X-Dev-User</c> / <c>X-Dev-Roles</c> headers. Local development only.
/// </summary>
public sealed class DevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IConfiguration config, AuthClaimNames names)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = config["Auth:Dev:UserId"] ?? "dev";
        var roles = config["Auth:Dev:Roles"] ?? "operator";

        if (config.GetValue("Auth:Dev:AllowHeaders", false))
        {
            if (Request.Headers.TryGetValue("X-Dev-User", out var u) && !string.IsNullOrWhiteSpace(u)) user = u.ToString();
            if (Request.Headers.TryGetValue("X-Dev-Roles", out var r)) roles = r.ToString();
        }

        var claims = new List<Claim> { new(names.User, user) };
        claims.AddRange(roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(r => new Claim(names.Roles, r)));
        var identity = new ClaimsIdentity(claims, AuthSetup.DevScheme);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthSetup.DevScheme)));
    }
}
