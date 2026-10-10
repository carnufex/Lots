using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Features.Conversations;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.ToolCalls;

public sealed record ToolCallQuery(
    string? User = null, string? Profile = null, string? Tool = null, string? Status = null,
    DateTimeOffset? From = null, DateTimeOffset? To = null, int? Limit = null, int? Offset = null);

/// <summary>One executed (or refused) tool call. Status: ok, error or denied.</summary>
public sealed record ToolCallDto(
    Guid RunId, int Seq, DateTimeOffset At, string User, string Profile, string Tool, string? Arguments, string? Result,
    long LatencyMs, string Status, string? Decision, string? Reason, string? Approver, string? BackendAuth);

public sealed record ToolStats(string Tool, int Calls, int Errors, int Denied, double ErrorRate, long P50Ms, long P95Ms);

public sealed record ToolCallList(IReadOnlyList<ToolCallDto> Calls, IReadOnlyList<ToolStats> Tools, int Total, bool Truncated);

/// <summary>
/// Every tool call across runs, from the trace (run_steps) joined with the policy decision (audit_log).
/// Users see calls from their own runs; admins and auditors see everyone's. Arguments and results are tool data
/// (untrusted) and are returned as plain text for the UI to render as text.
/// </summary>
public sealed class ListToolCallsEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config)
    : Endpoint<ToolCallQuery, ToolCallList>
{
    /// <summary>Filters and statistics are computed over at most this many of the newest matching calls.</summary>
    internal const int Window = 5000;

    public override void Configure()
    {
        Get("/tool-calls");
    }

    public override async Task HandleAsync(ToolCallQuery req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var auditRoles = (config["Auth:AuditRoles"] ?? "admin,auditor").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var seesAll = ConversationViews.IsAdmin(me, config) || me.Roles.Any(r => auditRoles.Contains(r, StringComparer.OrdinalIgnoreCase));

        var q = from s in db.RunSteps.AsNoTracking()
                join r in db.Runs.AsNoTracking() on s.RunId equals r.Id
                where s.Kind == StepKind.ToolCall
                select new { Step = s, r.UserId, r.Profile };
        if (!seesAll) q = q.Where(x => x.UserId == me.UserId);
        else if (!string.IsNullOrWhiteSpace(req.User)) q = q.Where(x => x.UserId == req.User);
        if (!string.IsNullOrWhiteSpace(req.Profile)) q = q.Where(x => x.Profile == req.Profile);
        if (!string.IsNullOrWhiteSpace(req.Tool)) q = q.Where(x => x.Step.Name == req.Tool);
        if (req.From is { } from) q = q.Where(x => x.Step.CreatedAt >= from);
        if (req.To is { } to) q = q.Where(x => x.Step.CreatedAt <= to);

        var rows = await q.OrderByDescending(x => x.Step.CreatedAt).Take(Window + 1).ToListAsync(ct);
        var truncated = rows.Count > Window;
        if (truncated) rows.RemoveAt(rows.Count - 1);

        var runIds = rows.Select(x => x.Step.RunId).Distinct().ToList();
        var audit = (await db.AuditLog.AsNoTracking()
                .Where(a => runIds.Contains(a.RunId) && a.Decision != AuditDecision.ApprovalRequested)
                .ToListAsync(ct))
            .GroupBy(a => a.RunId).ToDictionary(g => g.Key, g => g.ToList());

        var calls = rows.Select(x =>
        {
            var decision = Match(audit.GetValueOrDefault(x.Step.RunId), x.Step);
            return new ToolCallDto(
                x.Step.RunId, x.Step.Seq, x.Step.CreatedAt, x.UserId, x.Profile, x.Step.Name, x.Step.ArgumentsJson, x.Step.Result,
                x.Step.LatencyMs, StatusOf(decision, x.Step.Result), decision?.Decision.ToString(), decision?.Reason,
                decision?.ApproverId, decision?.BackendAuth);
        }).ToList();

        if (!string.IsNullOrWhiteSpace(req.Status))
            calls = calls.Where(c => string.Equals(c.Status, req.Status, StringComparison.OrdinalIgnoreCase)).ToList();

        var stats = calls.GroupBy(c => c.Tool).Select(g =>
        {
            var executed = g.Where(c => c.Status != "denied").Select(c => c.LatencyMs).Order().ToList();
            var errors = g.Count(c => c.Status == "error");
            return new ToolStats(g.Key, g.Count(), errors, g.Count(c => c.Status == "denied"),
                executed.Count == 0 ? 0 : Math.Round((double)errors / executed.Count, 3),
                Percentile(executed, 0.50), Percentile(executed, 0.95));
        }).OrderByDescending(t => t.Calls).ThenBy(t => t.Tool).ToList();

        var page = calls.Skip(Math.Max(0, req.Offset ?? 0)).Take(Math.Clamp(req.Limit ?? 100, 1, 1000)).ToList();
        await Send.OkAsync(new ToolCallList(page, stats, calls.Count, truncated), ct);
    }

    /// <summary>
    /// The audit row for a trace step: by tool call id, or for rows written before the id was recorded, the last decision
    /// for the same tool and arguments at or before the step.
    /// </summary>
    internal static AuditRecord? Match(List<AuditRecord>? rows, RunStepRecord step)
    {
        if (rows is null) return null;
        if (step.ToolCallId is { } id && rows.FirstOrDefault(a => a.ToolCallId == id) is { } byId) return byId;
        return rows.Where(a => a.ToolCallId is null && a.Tool == step.Name && a.ArgumentsJson == step.ArgumentsJson && a.At <= step.CreatedAt.AddSeconds(1))
            .MaxBy(a => a.At);
    }

    internal static string StatusOf(AuditRecord? decision, string? result) => decision?.Decision switch
    {
        AuditDecision.Denied or AuditDecision.ApprovalDenied => "denied",
        _ when decision?.ResultStatus is { } s => s,
        _ => result?.StartsWith("Error:", StringComparison.Ordinal) == true ? "error" : "ok",
    };

    internal static long Percentile(List<long> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[(int)Math.Clamp(Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
}
