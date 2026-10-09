using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Approvals;

public sealed record ApprovalDto(
    Guid Id, Guid RunId, string Tool, string? Arguments, string RequestedBy, DateTimeOffset RequestedAt,
    string Status, string? DecidedBy, DateTimeOffset? DecidedAt, string? Comment);

internal static class ApprovalMapping
{
    public static ApprovalDto ToDto(ApprovalRecord a) => new(
        a.Id, a.RunId, a.ToolName, a.ArgumentsJson, a.RequestedBy, a.RequestedAt,
        a.Status.ToString(), a.DecidedBy, a.DecidedAt, a.Comment);
}

public sealed class ListApprovalsEndpoint(LotsDbContext db, ProfileRegistry profiles, ICurrentPrincipal who)
    : EndpointWithoutRequest<List<ApprovalDto>>
{
    public override void Configure()
    {
        Get("/approvals");
        AllowAnonymous(); // identity comes from ICurrentPrincipal
    }

    /// <summary>Pending approvals the caller is allowed to decide.</summary>
    public override async Task HandleAsync(CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var pending = await db.Approvals.AsNoTracking()
            .Where(a => a.Status == ApprovalStatus.Pending)
            .Join(db.Runs.AsNoTracking(), a => a.RunId, r => r.Id, (a, r) => new { Approval = a, r.Profile })
            .OrderBy(x => x.Approval.RequestedAt)
            .ToListAsync(ct);

        var visible = pending
            .Where(x => profiles.Find(x.Profile) is { } p && PolicyEngine.CanApprove(me, p, x.Approval.ToolName))
            .Select(x => ApprovalMapping.ToDto(x.Approval))
            .ToList();

        await Send.OkAsync(visible, ct);
    }
}

public sealed record DecideRequest(Guid Id, string? Comment = null);

public abstract class DecideEndpoint(LotsDbContext db, ProfileRegistry profiles, ICurrentPrincipal who, TimeProvider clock, ApprovalStatus outcome)
    : Endpoint<DecideRequest, ApprovalDto>
{
    public override async Task HandleAsync(DecideRequest req, CancellationToken ct)
    {
        var approval = await db.Approvals.SingleOrDefaultAsync(a => a.Id == req.Id, ct);
        var run = approval is null ? null : await db.Runs.SingleOrDefaultAsync(r => r.Id == approval.RunId, ct);
        if (approval is null || run is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var me = who.Get(HttpContext);
        var profile = profiles.Find(run.Profile);
        if (profile is null || !PolicyEngine.CanApprove(me, profile, approval.ToolName))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        if (approval.Status != ApprovalStatus.Pending)
        {
            AddError($"Approval is already {approval.Status}.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var now = clock.GetUtcNow();
        approval.Status = outcome;
        approval.DecidedBy = me.UserId;
        approval.DecidedAt = now;
        approval.Comment = req.Comment;

        db.AuditLog.Add(new AuditRecord
        {
            Id = Guid.NewGuid(), At = now, UserId = run.UserId, Roles = run.Roles, Profile = run.Profile,
            ProfileVersion = profile.Version, RunId = run.Id, Tool = approval.ToolName, ArgumentsJson = approval.ArgumentsJson,
            Decision = outcome == ApprovalStatus.Approved ? AuditDecision.ApprovalGranted : AuditDecision.ApprovalRefused,
            Reason = req.Comment ?? "", ApproverId = me.UserId,
        });

        // The worker picks the run up again and continues right after the paused tool call.
        if (run.Status == RunStatus.WaitingForApproval) run.Status = RunStatus.Pending;
        run.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        await Send.OkAsync(ApprovalMapping.ToDto(approval), ct);
    }
}

public sealed class ApproveEndpoint(LotsDbContext db, ProfileRegistry profiles, ICurrentPrincipal who, TimeProvider clock)
    : DecideEndpoint(db, profiles, who, clock, ApprovalStatus.Approved)
{
    public override void Configure()
    {
        Post("/approvals/{Id}/approve");
        AllowAnonymous();
    }
}

public sealed class DenyEndpoint(LotsDbContext db, ProfileRegistry profiles, ICurrentPrincipal who, TimeProvider clock)
    : DecideEndpoint(db, profiles, who, clock, ApprovalStatus.Denied)
{
    public override void Configure()
    {
        Post("/approvals/{Id}/deny");
        AllowAnonymous();
    }
}
