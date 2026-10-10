using System.Collections.Concurrent;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Core.Notifications;

/// <summary>
/// Records who people are from their login (#136): e-mail claim and current roles, at most every ten minutes per user. Only for
/// real logins (OIDC); dev identities are not people.
/// </summary>
public sealed class UserDirectoryMiddleware(RequestDelegate next, IServiceScopeFactory scopes, TimeProvider clock)
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> Seen = new();

    public async Task InvokeAsync(HttpContext http, ICurrentPrincipal who, IConfiguration config)
    {
        await next(http);
        if (http.User.Identity?.IsAuthenticated != true || !AuthSetup.IsOidc(config)) return;
        // The directory records interactive logins; an API token's roles are a snapshot and must not overwrite them (#88).
        if (http.User.FindFirst(Security.ApiTokens.AuthMethodClaim) is not null) return;
        Principal me;
        try { me = who.Get(http); }
        catch (InvalidOperationException) { return; }
        var now = clock.GetUtcNow();
        if (Seen.TryGetValue(me.UserId, out var last) && now - last < TimeSpan.FromMinutes(10)) return;
        Seen[me.UserId] = now;
        var email = http.User.FindFirst("email")?.Value;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var row = await db.UserProfiles.SingleOrDefaultAsync(p => p.UserId == me.UserId);
        if (row is null) db.UserProfiles.Add(row = new UserProfileRecord { UserId = me.UserId });
        row.Email = email ?? row.Email;
        row.Roles = string.Join(',', me.Roles);
        row.LastSeenAt = now;
        await db.SaveChangesAsync();
    }
}

public sealed record ApproverRecipient(string UserId, string Email, string? OnBehalfOf);

/// <summary>
/// Who should hear about an approval request: everyone whose roles may approve it (not the requester). An approver who is away is
/// replaced by their delegate, but only when the delegate may approve it themselves: delegation routes messages, never permissions.
/// </summary>
public static class ApproverRouting
{
    public static List<ApproverRecipient> Recipients(Profile profile, string tool, string requestedBy, IReadOnlyList<UserProfileRecord> people,
        IReadOnlyDictionary<string, UserSettingsRecord> settings, DateTimeOffset now)
    {
        bool CanApprove(UserProfileRecord p) =>
            p.UserId != requestedBy && PolicyEngine.CanApprove(new Principal(p.UserId, p.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries)), profile, tool);

        var byId = people.ToDictionary(p => p.UserId);
        var result = new List<ApproverRecipient>();
        foreach (var p in people.Where(CanApprove))
        {
            if (settings.TryGetValue(p.UserId, out var s) && s.AwayUntil > now)
            {
                if (s.DelegateTo is { } d && byId.TryGetValue(d, out var delegate_) && CanApprove(delegate_) && delegate_.Email is { } de)
                    result.Add(new ApproverRecipient(d, de, p.UserId));
                continue;
            }
            if (p.Email is { } e) result.Add(new ApproverRecipient(p.UserId, e, null));
        }
        return result.GroupBy(r => r.UserId).Select(g => g.First()).ToList();
    }
}
