using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Security;
using Lots.Shell.Features.Conversations;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Features.Security;

public sealed record ApiTokenDto(Guid Id, string Name, string Hint, IReadOnlyList<string> Scopes, IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt, string? UserId = null);

public sealed record CreateApiTokenRequest(string Name, List<string>? Scopes, int ExpiresInDays = 30);

public sealed record CreatedApiTokenDto(string Token, ApiTokenDto Info);

public static class ApiTokenViews
{
    public static ApiTokenDto ToDto(ApiTokenRecord t, bool withUser = false) => new(
        t.Id, t.Name, t.Hint, t.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries), t.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries),
        t.CreatedAt, t.ExpiresAt, t.LastUsedAt, t.RevokedAt, withUser ? t.UserId : null);
}

public sealed class ListMyTokensEndpoint(LotsDbContext db, ICurrentPrincipal who) : EndpointWithoutRequest<List<ApiTokenDto>>
{
    public override void Configure() => Get("/me/tokens");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var rows = await db.ApiTokens.AsNoTracking().Where(t => t.UserId == me.UserId).OrderByDescending(t => t.CreatedAt).ToListAsync(ct);
        await Send.OkAsync(rows.Select(t => ApiTokenViews.ToDto(t)).ToList(), ct);
    }
}

/// <summary>Creates a token for the caller. The token is returned once; only its hash is kept.</summary>
public sealed class CreateTokenEndpoint(LotsDbContext db, ICurrentPrincipal who, IOptions<ApiTokenOptions> options, TimeProvider clock,
    ILogger<CreateTokenEndpoint> logger) : Endpoint<CreateApiTokenRequest, CreatedApiTokenDto>
{
    public override void Configure() => Post("/me/tokens");

    public override async Task HandleAsync(CreateApiTokenRequest req, CancellationToken ct)
    {
        var o = options.Value;
        var me = who.Get(HttpContext);
        var scopes = (req.Scopes ?? [ApiTokens.ScopeRead]).Select(s => s.Trim().ToLowerInvariant()).Distinct().ToList();
        if (!o.Enabled) AddError("API tokens are disabled (Auth:ApiTokens:Enabled).");
        if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Length > 100) AddError(x => x.Name, "A name (at most 100 characters) is required.");
        if (scopes.Count == 0 || scopes.Except(ApiTokens.AllScopes).ToList() is { Count: > 0 } unknown && unknown.Count > 0)
            AddError(x => x.Scopes!, $"Scopes must be some of {string.Join(", ", ApiTokens.AllScopes)}.");
        if (req.ExpiresInDays < 1 || req.ExpiresInDays > o.MaxDays) AddError(x => x.ExpiresInDays, $"Expiry must be 1 to {o.MaxDays} days.");
        var now = clock.GetUtcNow();
        if (await db.ApiTokens.CountAsync(t => t.UserId == me.UserId && t.RevokedAt == null && t.ExpiresAt > now, ct) >= o.MaxPerUser)
            AddError($"At most {o.MaxPerUser} active tokens per user; revoke one first.");
        ThrowIfAnyErrors();

        var token = ApiTokens.NewToken();
        var row = new ApiTokenRecord
        {
            Id = Guid.NewGuid(), UserId = me.UserId, Name = req.Name.Trim(), Hash = ApiTokens.Hash(token),
            Hint = token[..(ApiTokens.Prefix.Length + 4)] + "…", Scopes = string.Join(',', scopes), Roles = string.Join(',', me.Roles),
            CreatedAt = now, ExpiresAt = now.AddDays(req.ExpiresInDays),
        };
        db.ApiTokens.Add(row);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("API token {TokenId} ({Name}) created by {User} with scopes {Scopes}, expires {Expires}", row.Id, row.Name, me.UserId, row.Scopes, row.ExpiresAt);
        await Send.OkAsync(new CreatedApiTokenDto(token, ApiTokenViews.ToDto(row)), ct);
    }
}

public sealed record TokenRequest(Guid Id);

public sealed class RevokeMyTokenEndpoint(LotsDbContext db, ICurrentPrincipal who, TimeProvider clock) : Endpoint<TokenRequest>
{
    public override void Configure() => Delete("/me/tokens/{Id}");

    public override async Task HandleAsync(TokenRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var row = await db.ApiTokens.SingleOrDefaultAsync(t => t.Id == req.Id && t.UserId == me.UserId, ct);
        if (row is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        row.RevokedAt ??= clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}

/// <summary>Every token in the deployment, for admins: who has non-interactive access, with which scopes, used when.</summary>
public sealed class ListAllTokensEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config) : EndpointWithoutRequest<List<ApiTokenDto>>
{
    public override void Configure() => Get("/admin/api-tokens");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!ConversationViews.IsAdmin(who.Get(HttpContext), config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var rows = await db.ApiTokens.AsNoTracking().OrderByDescending(t => t.CreatedAt).Take(500).ToListAsync(ct);
        await Send.OkAsync(rows.Select(t => ApiTokenViews.ToDto(t, withUser: true)).ToList(), ct);
    }
}

public sealed class RevokeAnyTokenEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config, TimeProvider clock,
    ILogger<RevokeAnyTokenEndpoint> logger) : Endpoint<TokenRequest>
{
    public override void Configure() => Delete("/admin/api-tokens/{Id}");

    public override async Task HandleAsync(TokenRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        if (!ConversationViews.IsAdmin(me, config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var row = await db.ApiTokens.SingleOrDefaultAsync(t => t.Id == req.Id, ct);
        if (row is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        row.RevokedAt ??= clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        logger.LogInformation("API token {TokenId} of {Owner} revoked by admin {Admin}", row.Id, row.UserId, me.UserId);
        await Send.NoContentAsync(ct);
    }
}
