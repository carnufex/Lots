using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Approvals;

public sealed record ApprovalDto(
    Guid Id, Guid RunId, string Tool, string? Arguments, string RequestedBy, DateTimeOffset RequestedAt,
    string Status, string? DecidedBy, DateTimeOffset? DecidedAt, string? Comment,
    string? Risk = null, int RequiredApprovals = 1, IReadOnlyList<string>? ApprovedBy = null, DateTimeOffset? ExpiresAt = null, bool CommentRequired = false);

internal static class ApprovalMapping
{
    public static List<string> Approvers(ApprovalRecord a) => a.ApprovedBy.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

    public static bool CommentRequired(ApprovalRecord a, Profile? p) =>
        p is not null && Enum.TryParse<ToolRisk>(a.Risk, out var risk) && p.ApprovalRules.RequireComment.Contains(risk);

    public static ApprovalDto ToDto(ApprovalRecord a, Profile? p = null) => new(
        a.Id, a.RunId, a.ToolName, a.ArgumentsJson, a.RequestedBy, a.RequestedAt,
        a.Status.ToString(), a.DecidedBy, a.DecidedAt, a.Comment, a.Risk, a.RequiredApprovals, Approvers(a), a.ExpiresAt, CommentRequired(a, p));

    /// <summary>
    /// Whether this person may decide this request now: an approving role, and for two-person requests neither the requester nor
    /// someone who already approved it.
    /// </summary>
    public static bool MayDecide(ApprovalRecord a, Profile p, Principal me) =>
        PolicyEngine.CanApprove(me, p, a.ToolName)
        && !(a.RequiredApprovals > 1 && (a.RequestedBy == me.UserId || Approvers(a).Contains(me.UserId)));
}

public sealed class ListApprovalsEndpoint(LotsDbContext db, ProfileRegistry profiles, ICurrentPrincipal who)
    : EndpointWithoutRequest<List<ApprovalDto>>
{
    public override void Configure()
    {
        Get("/approvals");
    }

    /// <summary>Pending approvals the caller is allowed to decide.</summary>
    public override async Task HandleAsync(CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var pending = await db.Approvals.AsNoTracking()
            .Where(a => a.Status == ApprovalStatus.Pending)
            .Join(db.Runs.AsNoTracking(), a => a.RunId, r => r.Id, (a, r) => new { Approval = a, r.Profile, r.Status })
            .Where(x => x.Status != RunStatus.Cancelled) // nothing left to decide for a stopped run
            .OrderBy(x => x.Approval.RequestedAt)
            .ToListAsync(ct);

        var visible = pending
            .Where(x => profiles.Find(x.Profile) is { } p && ApprovalMapping.MayDecide(x.Approval, p, me))
            .Select(x => ApprovalMapping.ToDto(x.Approval, profiles.Find(x.Profile)))
            .ToList();

        await Send.OkAsync(visible, ct);
    }
}

public sealed record DecideRequest(Guid Id, string? Comment = null);

/// <summary>The outcome of a decision: the approval on success, otherwise an HTTP status and a message safe to show.</summary>
public sealed record DecisionResult(ApprovalDto? Approval, int Status, string? Error);

/// <summary>
/// Approving or denying, with every rule in one place (#74, #107): the API and channel buttons (Slack) both decide through here, so
/// a button can never do more than the web UI.
/// </summary>
public static class ApprovalDecisions
{
    public static async Task<DecisionResult> DecideAsync(LotsDbContext db, ProfileRegistry profiles, Principal me, Guid approvalId,
        ApprovalStatus outcome, string? comment, DateTimeOffset now, CancellationToken ct)
    {
        var approval = await db.Approvals.SingleOrDefaultAsync(a => a.Id == approvalId, ct);
        var run = approval is null ? null : await db.Runs.SingleOrDefaultAsync(r => r.Id == approval.RunId, ct);
        if (approval is null || run is null) return new(null, 404, "No such approval.");

        var profile = profiles.Find(run.Profile);
        if (profile is null || !PolicyEngine.CanApprove(me, profile, approval.ToolName)) return new(null, 403, "You may not decide this request.");
        if (!ApprovalMapping.MayDecide(approval, profile, me))
            return new(null, 403, approval.RequestedBy == me.UserId
                ? "This request needs two approvers other than the requester."
                : "You already approved this request; it needs a second, different approver.");
        if (ApprovalMapping.CommentRequired(approval, profile) && string.IsNullOrWhiteSpace(comment))
            return new(null, 400, $"A reason is required for {approval.Risk} tools.");
        if (run.Status == RunStatus.Cancelled) return new(null, 409, "The run was cancelled; there is nothing to approve.");
        if (approval.Status != ApprovalStatus.Pending) return new(null, 409, $"Approval is already {approval.Status}.");

        // Two-person requests: the first approval is recorded; the request is approved when the required number is reached.
        var approvers = ApprovalMapping.Approvers(approval);
        if (outcome == ApprovalStatus.Approved) approvers.Add(me.UserId);
        var complete = outcome == ApprovalStatus.Denied || approvers.Count >= approval.RequiredApprovals;
        approval.ApprovedBy = string.Join(',', approvers);
        if (complete)
        {
            Lots.Shell.Core.Telemetry.Tracing.ApprovalWait(run, approval, outcome.ToString(), now);
            Lots.Shell.Core.Telemetry.LotsMetrics.Approvals.Add(1, new("event", outcome == ApprovalStatus.Approved ? "approved" : "denied"), new("risk", approval.Risk));
            approval.Status = outcome;
            approval.DecidedBy = string.Join(", ", outcome == ApprovalStatus.Approved ? approvers : [me.UserId]);
            approval.DecidedAt = now;
            approval.Comment = comment;
            Lots.Shell.Core.Notifications.Outbox.Add(db, Lots.Shell.Core.Notifications.NotificationEvents.ApprovalDecided, new
            {
                approvalId = approval.Id, runId = run.Id, tool = approval.ToolName, requestedBy = approval.RequestedBy,
                outcome = outcome == ApprovalStatus.Approved ? "approved" : "denied", decidedBy = approval.DecidedBy,
            }, now);
        }

        db.AuditLog.Add(new AuditRecord
        {
            Id = Guid.NewGuid(), At = now, UserId = run.UserId, Roles = run.Roles, Profile = run.Profile,
            ProfileVersion = profile.Version, RunId = run.Id, Tool = approval.ToolName, ArgumentsJson = approval.ArgumentsJson,
            Decision = outcome == ApprovalStatus.Approved ? AuditDecision.ApprovalGranted : AuditDecision.ApprovalRefused,
            Reason = comment ?? "", ApproverId = me.UserId,
        });

        // The worker picks the run up again and continues right after the paused tool call.
        if (complete && run.Status == RunStatus.WaitingForApproval) run.Status = RunStatus.Pending;
        run.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        return new(ApprovalMapping.ToDto(approval, profile), 200, null);
    }
}

public abstract class DecideEndpoint(LotsDbContext db, ProfileRegistry profiles, ICurrentPrincipal who, TimeProvider clock, ApprovalStatus outcome)
    : Endpoint<DecideRequest, ApprovalDto>
{
    public override async Task HandleAsync(DecideRequest req, CancellationToken ct)
    {
        var result = await ApprovalDecisions.DecideAsync(db, profiles, who.Get(HttpContext), req.Id, outcome, req.Comment, clock.GetUtcNow(), ct);
        switch (result.Status)
        {
            case 200: await Send.OkAsync(result.Approval!, ct); break;
            case 404: await Send.NotFoundAsync(ct); break;
            case 403 when result.Error == "You may not decide this request.": await Send.ForbiddenAsync(ct); break;
            default:
                if (result.Error!.StartsWith("A reason", StringComparison.Ordinal)) AddError(x => x.Comment!, result.Error);
                else AddError(result.Error);
                await Send.ErrorsAsync(result.Status, ct);
                break;
        }
    }
}

public sealed class ApproveEndpoint(LotsDbContext db, ProfileRegistry profiles, ICurrentPrincipal who, TimeProvider clock)
    : DecideEndpoint(db, profiles, who, clock, ApprovalStatus.Approved)
{
    public override void Configure()
    {
        Post("/approvals/{Id}/approve");
    }
}

public sealed class DenyEndpoint(LotsDbContext db, ProfileRegistry profiles, ICurrentPrincipal who, TimeProvider clock)
    : DecideEndpoint(db, profiles, who, clock, ApprovalStatus.Denied)
{
    public override void Configure()
    {
        Post("/approvals/{Id}/deny");
    }
}

public sealed record CoverageDto(string User, DateTimeOffset Until, string? DelegateTo);

/// <summary>Approvers who are away right now and who covers for them, for the approvals inbox.</summary>
public sealed class CoverageEndpoint(LotsDbContext db, TimeProvider clock) : EndpointWithoutRequest<List<CoverageDto>>
{
    public override void Configure() => Get("/approvals/coverage");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var away = await db.UserSettings.AsNoTracking().Where(s => s.AwayUntil != null && s.AwayUntil > now).ToListAsync(ct);
        await Send.OkAsync(away.Select(s => new CoverageDto(s.UserId, s.AwayUntil!.Value, s.DelegateTo)).ToList(), ct);
    }
}
