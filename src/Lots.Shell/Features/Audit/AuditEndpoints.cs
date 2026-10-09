using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Audit;

public sealed record AuditQuery(string? User = null, DateTimeOffset? From = null, DateTimeOffset? To = null, Guid? RunId = null, int? Limit = null);

public sealed record AuditDto(
    Guid Id, DateTimeOffset At, string User, string Roles, string Profile, int ProfileVersion, Guid RunId,
    string Tool, string? Arguments, string Decision, string Reason, string? Approver, string? Result);

/// <summary>
/// Read access to the audit log for admins and auditors (<c>Auth:AuditRoles</c>, default admin,auditor).
/// There is deliberately no write or delete endpoint: the log is append-only.
/// </summary>
public sealed class ListAuditEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config)
    : Endpoint<AuditQuery, List<AuditDto>>
{
    public override void Configure()
    {
        Get("/audit");
        AllowAnonymous(); // identity comes from ICurrentPrincipal
    }

    public override async Task HandleAsync(AuditQuery req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var allowed = (config["Auth:AuditRoles"] ?? "admin,auditor").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!me.Roles.Any(r => allowed.Contains(r, StringComparer.OrdinalIgnoreCase)))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var q = db.AuditLog.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(req.User)) q = q.Where(a => a.UserId == req.User);
        if (req.From is { } from) q = q.Where(a => a.At >= from);
        if (req.To is { } to) q = q.Where(a => a.At <= to);
        if (req.RunId is { } runId) q = q.Where(a => a.RunId == runId);

        var rows = await q.OrderByDescending(a => a.At).Take(Math.Clamp(req.Limit ?? 100, 1, 1000)).ToListAsync(ct);

        await Send.OkAsync(rows.Select(a => new AuditDto(
            a.Id, a.At, a.UserId, a.Roles, a.Profile, a.ProfileVersion, a.RunId, a.Tool, a.ArgumentsJson,
            a.Decision.ToString(), a.Reason, a.ApproverId, a.ResultStatus)).ToList(), ct);
    }
}
