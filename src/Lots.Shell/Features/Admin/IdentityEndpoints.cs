using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;

namespace Lots.Shell.Features.Admin;

public sealed record ServerIdentity(string Server, string Profiles, string Auth, string? CredentialType, string? TokenUrl);

public sealed record IdentityDto(
    string Mode, string? Authority, string? ClientId, string? Audience, string UserClaim, string RoleClaim, string? RolePrefix,
    string AdminRoles, string AuditRoles, IReadOnlyList<string> KnownRoles, IReadOnlyList<ServerIdentity> Servers, bool DevHeaders);

/// <summary>
/// How identity works in this deployment (#72): login settings, claim-to-role mapping, and which backend each server is reached
/// with (the user's own delegated token or a shared service account). Admins only: end users never see which account was used.
/// </summary>
public sealed class IdentityEndpoint(ProfileRegistry profiles, IConfiguration config, ICurrentPrincipal who) : AdminEndpoint<EmptyRequest, IdentityDto>(config, who)
{
    private readonly IConfiguration _config = config;

    public override void Configure() => Get("/admin/v1/identity");

    public override async Task HandleAsync(EmptyRequest _, CancellationToken ct)
    {
        if (!await AllowedAsync(ct)) return;
        var oidc = AuthSetup.IsOidc(_config);
        var servers = profiles.Servers.Select(s => new ServerIdentity(s.Name,
            string.Join(", ", profiles.All.Where(p => p.Servers.Any(x => x.Name == s.Name)).Select(p => p.Name)),
            s.Auth, s.Credentials?.Type, s.Credentials?.TokenUrl)).ToList();
        await Send.OkAsync(new IdentityDto(
            oidc ? "Oidc" : "Dev", _config["Auth:Oidc:Authority"], _config["Auth:Oidc:ClientId"], _config["Auth:Oidc:Audience"],
            _config["Auth:Oidc:UserClaim"] ?? "sub", _config["Auth:Oidc:RoleClaim"] ?? "roles", oidc ? _config["Auth:Oidc:RolePrefix"] : null,
            _config["Auth:AdminRoles"] ?? "admin", _config["Auth:AuditRoles"] ?? "admin,auditor",
            profiles.All.SelectMany(p => p.Roles.Select(r => r.Name)).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList(),
            servers, _config.GetValue("Auth:Dev:AllowHeaders", false)), ct);
    }
}

public sealed record MappingTestRequest(string Token);

public sealed record MappingTestResult(string? User, IReadOnlyList<string> Roles, IReadOnlyList<string> UnknownRoles, bool IsAdmin, bool IsAuditor,
    DateTimeOffset? ExpiresAt, string? Issuer, string Note);

/// <summary>
/// Shows which user and roles a sample ID/access token maps to with the current settings. The token is decoded, not validated, and
/// never stored or logged; use a test user's token.
/// </summary>
public sealed class MappingTestEndpoint(ProfileRegistry profiles, IConfiguration config, ICurrentPrincipal who) : AdminEndpoint<MappingTestRequest, MappingTestResult>(config, who)
{
    private readonly IConfiguration _config = config;

    public override void Configure() => Post("/admin/v1/identity/test");

    public override async Task HandleAsync(MappingTestRequest req, CancellationToken ct)
    {
        if (!await AllowedAsync(ct)) return;
        JwtSecurityToken jwt;
        try { jwt = new JwtSecurityTokenHandler().ReadJwtToken(req.Token.Trim().Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase)); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or System.Text.Json.JsonException)
        {
            AddError(x => x.Token, "Not a JWT.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        var names = new AuthClaimNames
        {
            User = _config["Auth:Oidc:UserClaim"] ?? "sub",
            Roles = _config["Auth:Oidc:RoleClaim"] ?? "roles",
            RolePrefix = _config["Auth:Oidc:RolePrefix"],
        };
        var p = ClaimsCurrentPrincipal.Map(jwt.Claims, names);
        var known = profiles.All.SelectMany(x => x.Roles.Select(r => r.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roles = p?.Roles ?? [];
        string Csv(string key, string def) => _config[key] ?? def;
        bool Has(string csv) => roles.Any(r => csv.Split(',', StringSplitOptions.TrimEntries).Contains(r, StringComparer.OrdinalIgnoreCase));
        await Send.OkAsync(new MappingTestResult(p?.UserId, roles, roles.Where(r => !known.Contains(r)).ToList(),
            Has(Csv("Auth:AdminRoles", "admin")), Has(Csv("Auth:AuditRoles", "admin,auditor")),
            jwt.ValidTo == DateTime.MinValue ? null : new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero), jwt.Issuer,
            p is null ? $"The token has no '{names.User}' claim: this login would be rejected." : "Decoded only (signature not checked)."), ct);
    }
}

public sealed record UserDto(string User, IReadOnlyList<string> Roles, DateTimeOffset FirstSeen, DateTimeOffset LastActive, int Runs, int Runs30Days,
    long Tokens30Days, int Approvals30Days, bool OwnVoice, int PersonalSources);

/// <summary>
/// Users the shell has seen (#73). There are no local accounts: identity and groups live in the IdP; this is who used Lots, with which
/// roles (as of their latest run), how much, and what personal data they keep here.
/// </summary>
public sealed class UsersEndpoint(Lots.Shell.Persistence.LotsDbContext db, Lots.Shell.Core.Knowledge.IKnowledgeStore knowledge, TimeProvider clock,
    IConfiguration config, ICurrentPrincipal who) : AdminEndpoint<EmptyRequest, List<UserDto>>(config, who)
{
    public override void Configure() => Get("/admin/v1/users");

    public override async Task HandleAsync(EmptyRequest _, CancellationToken ct)
    {
        if (!await AllowedAsync(ct)) return;
        var since = clock.GetUtcNow().AddDays(-30);
        var runs = await (db.Runs.Select(r => new { r.Id, r.UserId, r.Roles, r.CreatedAt, r.UpdatedAt })).ToListAsync(ct);
        var tokens = await (from s in db.RunSteps join r in db.Runs on s.RunId equals r.Id
            where s.CreatedAt >= since
            group s by r.UserId into g
            select new { User = g.Key, Tokens = g.Sum(x => (long)((x.PromptTokens ?? 0) + (x.CompletionTokens ?? 0))) }).ToListAsync(ct);
        var approvals = await (db.Approvals.Where(a => a.DecidedAt >= since && a.DecidedBy != null).GroupBy(a => a.DecidedBy!).Select(g => new { User = g.Key, Count = g.Count() })).ToListAsync(ct);
        var voices = (await (db.UserSettings.Where(s => s.VoiceId != null).Select(s => s.UserId)).ToListAsync(ct)).ToHashSet();
        var personal = (await knowledge.ListSourcesAsync(ct)).Where(s => s.Readers.Count == 1 && s.Readers[0] == "user:" + s.Owner)
            .GroupBy(s => s.Owner).ToDictionary(g => g.Key, g => g.Count());

        var users = runs.GroupBy(r => r.UserId).Select(g =>
        {
            var latest = g.MaxBy(r => r.CreatedAt)!;
            return new UserDto(g.Key, latest.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries), g.Min(r => r.CreatedAt), g.Max(r => r.UpdatedAt),
                g.Count(), g.Count(r => r.CreatedAt >= since), tokens.FirstOrDefault(t => t.User == g.Key)?.Tokens ?? 0,
                approvals.FirstOrDefault(a => a.User == g.Key)?.Count ?? 0, voices.Contains(g.Key), personal.GetValueOrDefault(g.Key));
        }).OrderByDescending(u => u.LastActive).ToList();
        await Send.OkAsync(users, ct);
    }
}
