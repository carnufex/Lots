using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Lots.Shell.Core.Policy;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Core.Security;

/// <summary>
/// Personal API tokens for scripts, CI and lotsctl (#88): <c>Authorization: Bearer lots_pat_…</c>. A token acts as the user who
/// made it, with at most the roles they had then (and never more than they have now, when the shell has seen them log in since),
/// limited further by scopes. Only a SHA-256 hash is stored; the token is shown once. Tokens always expire.
/// </summary>
public static class ApiTokens
{
    public const string Prefix = "lots_pat_";
    public const string Scheme = "LotsApiToken";
    /// <summary>Claim on every principal authenticated by a token, so the rest of the shell can tell (and the directory is not updated).</summary>
    public const string AuthMethodClaim = "lots:auth";
    public const string ScopesClaim = "lots:scopes";

    public const string ScopeRead = "read";
    public const string ScopeRuns = "runs";
    public const string ScopeApprovals = "approvals";
    public const string ScopeAdmin = "admin";
    public static readonly IReadOnlyList<string> AllScopes = [ScopeRead, ScopeRuns, ScopeApprovals, ScopeAdmin];

    public static string NewToken()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder(Prefix);
        foreach (var b in bytes) sb.Append(alphabet[b % alphabet.Length]);
        return sb.ToString();
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool IsToken(string? authorization) =>
        authorization is not null && authorization.StartsWith("Bearer " + Prefix, StringComparison.Ordinal);

    /// <summary>
    /// The scope a request needs. Reading needs any scope; a token never manages tokens. Everything that is not a run or an
    /// approval decision is administration.
    /// </summary>
    public static string? RequiredScope(string method, PathString path)
    {
        if (path.StartsWithSegments("/me/tokens")) return null; // not with a token: tokens are made by a person in the UI
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) return ScopeRead;
        if (path.StartsWithSegments("/runs") || path.StartsWithSegments("/voice") || path.StartsWithSegments("/conversations")) return ScopeRuns;
        if (path.StartsWithSegments("/approvals")) return ScopeApprovals;
        return ScopeAdmin;
    }

    public static bool Allows(IReadOnlyCollection<string> scopes, string? required) =>
        required is not null && (scopes.Contains(required) || (required == ScopeRead && scopes.Count > 0));
}

public sealed class ApiTokenOptions
{
    public const string Section = "Auth:ApiTokens";
    public bool Enabled { get; set; } = true;
    public int MaxDays { get; set; } = 90;
    public int MaxPerUser { get; set; } = 20;
}

/// <summary>Authenticates <c>Bearer lots_pat_…</c>: looks the hash up, checks expiry and revocation, records last use.</summary>
public sealed class ApiTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    LotsDbContext db, AuthClaimNames names, IOptions<ApiTokenOptions> tokenOptions, TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (!ApiTokens.IsToken(header)) return AuthenticateResult.NoResult();
        if (!tokenOptions.Value.Enabled) return AuthenticateResult.Fail("API tokens are disabled.");

        var hash = ApiTokens.Hash(header!["Bearer ".Length..].Trim());
        var row = await db.ApiTokens.SingleOrDefaultAsync(t => t.Hash == hash, Context.RequestAborted);
        var now = clock.GetUtcNow();
        if (row is null || row.RevokedAt is not null || row.ExpiresAt <= now) return AuthenticateResult.Fail("Invalid, revoked or expired API token.");

        // Never more than the user has now: the directory holds the roles of their latest interactive login.
        var roles = row.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == row.UserId, Context.RequestAborted) is { } current
            && current.LastSeenAt > row.CreatedAt)
            roles = roles.Intersect(current.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase).ToList();

        if (row.LastUsedAt is null || now - row.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            row.LastUsedAt = now;
            await db.SaveChangesAsync(Context.RequestAborted);
        }

        var claims = new List<Claim>
        {
            new(names.User, row.UserId),
            new(ApiTokens.AuthMethodClaim, "api-token"),
            new(ApiTokens.ScopesClaim, row.Scopes),
            new("lots:token_id", row.Id.ToString()),
        };
        claims.AddRange(roles.Select(r => new Claim(names.Roles, (names.RolePrefix ?? "") + r)));
        var identity = new ClaimsIdentity(claims, ApiTokens.Scheme, names.User, names.Roles);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), ApiTokens.Scheme));
    }
}

/// <summary>Rejects token requests outside the token's scopes, before any endpoint runs.</summary>
public sealed class ApiTokenScopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        if (http.User.FindFirst(ApiTokens.AuthMethodClaim)?.Value == "api-token")
        {
            var scopes = (http.User.FindFirst(ApiTokens.ScopesClaim)?.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            var required = ApiTokens.RequiredScope(http.Request.Method, http.Request.Path);
            if (!ApiTokens.Allows(scopes, required))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsJsonAsync(new
                {
                    message = required is null ? "API tokens cannot manage API tokens." : $"This API token lacks the '{required}' scope.",
                });
                return;
            }
        }
        await next(http);
    }
}
