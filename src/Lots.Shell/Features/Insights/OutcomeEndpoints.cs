using System.Text.Json;
using FastEndpoints;
using Lots.Shell.Core.Outcomes;
using Lots.Shell.Core.Policy;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Insights;

public sealed class OutcomeQuery
{
    [QueryParam] public DateTimeOffset? From { get; set; }
    [QueryParam] public DateTimeOffset? To { get; set; }
    [QueryParam] public string? Profile { get; set; }
    [QueryParam] public string? Model { get; set; }
    [QueryParam] public string? Channel { get; set; }
    [QueryParam] public string? Status { get; set; }
    [QueryParam] public string? Problem { get; set; }
    [QueryParam] public int? Limit { get; set; }
}

public sealed record OutcomeGroup(string Key, int Runs, double SuccessRate, long P50WallMs, long P95WallMs, double Cost, int ToolErrors,
    int Denials, int ThumbsDown, IReadOnlyDictionary<string, int> Problems);

public sealed record ToolGroup(string Tool, int Calls, int Errors, double ErrorRate);

public sealed record OutcomeItem(Guid RunId, DateTimeOffset EndedAt, string Profile, int ProfileVersion, string? Model, string Channel, string Status,
    string Problem, long WallMs, int ToolCalls, int ToolErrors, int? FeedbackRating, bool FollowUp, bool UserRetried, string? TraceId);

public sealed record OutcomeReport(DateTimeOffset From, DateTimeOffset To, OutcomeGroup Total, IReadOnlyList<OutcomeGroup> ByProfileVersion,
    IReadOnlyList<OutcomeGroup> ByModel, IReadOnlyList<OutcomeGroup> ByChannel, IReadOnlyList<ToolGroup> ByTool, IReadOnlyList<OutcomeItem> Items);

/// <summary>
/// Run outcomes and their aggregates (#141): success rate and p50/p95 wall time per profile version, model, channel and tool.
/// The same readers as the audit log (<c>Auth:AuditRoles</c>, default admin, auditor). No content: outcomes hold none.
/// </summary>
public sealed class OutcomesEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config, TimeProvider clock) : Endpoint<OutcomeQuery, OutcomeReport>
{
    public override void Configure() => Get("/insights/outcomes");

    public override async Task HandleAsync(OutcomeQuery q, CancellationToken ct)
    {
        var roles = (config["Auth:AuditRoles"] ?? "admin,auditor").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!who.Get(HttpContext).Roles.Any(r => roles.Contains(r, StringComparer.OrdinalIgnoreCase)))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var to = q.To ?? clock.GetUtcNow();
        var from = q.From ?? to.AddDays(-7);
        var rows = db.RunOutcomes.AsNoTracking().Where(o => o.EndedAt >= from && o.EndedAt <= to);
        if (!string.IsNullOrWhiteSpace(q.Profile)) rows = rows.Where(o => o.Profile == q.Profile);
        if (!string.IsNullOrWhiteSpace(q.Model)) rows = rows.Where(o => o.Model == q.Model);
        if (!string.IsNullOrWhiteSpace(q.Channel)) rows = rows.Where(o => o.Channel == q.Channel);
        if (!string.IsNullOrWhiteSpace(q.Status)) rows = rows.Where(o => o.Status == q.Status);
        // Aggregates over at most 20 000 runs of the period: enough for trends, bounded by construction.
        var all = await rows.OrderByDescending(o => o.EndedAt).Take(20_000).ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(q.Problem)) all = all.Where(o => RunOutcomes.Problem(o) == q.Problem).ToList();

        var tools = all.Where(o => o.ToolsJson is not null)
            .SelectMany(o => JsonSerializer.Deserialize<Dictionary<string, ToolTally>>(o.ToolsJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .GroupBy(kv => kv.Key)
            .Select(g => (Tool: g.Key, Calls: g.Sum(kv => kv.Value.Calls), Errors: g.Sum(kv => kv.Value.Errors)))
            .Select(t => new ToolGroup(t.Tool, t.Calls, t.Errors, t.Calls == 0 ? 0 : Math.Round((double)t.Errors / t.Calls, 4)))
            .OrderByDescending(t => t.Calls).ToList();

        await Send.OkAsync(new OutcomeReport(from, to, Aggregate("all", all),
            all.GroupBy(o => $"{o.Profile} v{o.ProfileVersion}").Select(g => Aggregate(g.Key, g.ToList())).OrderByDescending(g => g.Runs).ToList(),
            all.GroupBy(o => o.Model ?? "(none)").Select(g => Aggregate(g.Key, g.ToList())).OrderByDescending(g => g.Runs).ToList(),
            all.GroupBy(o => o.Channel).Select(g => Aggregate(g.Key, g.ToList())).OrderByDescending(g => g.Runs).ToList(),
            tools,
            all.Take(Math.Clamp(q.Limit ?? 100, 1, 1000)).Select(o => new OutcomeItem(o.RunId, o.EndedAt, o.Profile, o.ProfileVersion, o.Model, o.Channel,
                o.Status, RunOutcomes.Problem(o), o.WallMs, o.ToolCalls, o.ToolErrors, o.FeedbackRating, o.FollowUp, o.UserRetried, o.TraceId)).ToList()), ct);
    }

    /// <summary>Success = completed, with a non-empty answer, and not rated bad.</summary>
    public static OutcomeGroup Aggregate(string key, IReadOnlyList<RunOutcomeRecord> runs)
    {
        var walls = runs.Select(o => o.WallMs).Order().ToList();
        long P(int p) => walls.Count == 0 ? 0 : walls[Math.Clamp((int)Math.Ceiling(p / 100.0 * walls.Count) - 1, 0, walls.Count - 1)];
        var ok = runs.Count(o => o.Status == "Completed" && !o.EmptyAnswer && o.FeedbackRating is not < 0);
        return new OutcomeGroup(key, runs.Count, runs.Count == 0 ? 0 : Math.Round((double)ok / runs.Count, 4), P(50), P(95),
            Math.Round(runs.Sum(o => o.Cost), 4), runs.Sum(o => o.ToolErrors), runs.Sum(o => o.PolicyDenials), runs.Count(o => o.FeedbackRating < 0),
            runs.GroupBy(RunOutcomes.Problem).Where(g => g.Key != "none").ToDictionary(g => g.Key, g => g.Count()));
    }
}
