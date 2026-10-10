using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Features.Conversations;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Usage;

/// <summary>Price of a model per million tokens (Models:Prices:&lt;model&gt;). Local models can carry an estimate (power, hardware) or 0.</summary>
public sealed class ModelPrice
{
    public double InputPerMillion { get; set; }
    public double OutputPerMillion { get; set; }
}

/// <summary>Token prices by model from configuration; unknown models cost 0 (and are listed as unpriced).</summary>
public sealed class PriceTable(IConfiguration config)
{
    public string Currency { get; } = config["Models:Currency"] ?? "USD";
    private readonly Dictionary<string, ModelPrice> _prices = config.GetSection("Models:Prices").GetChildren()
        .ToDictionary(c => c.Key, c => c.Get<ModelPrice>() ?? new ModelPrice(), StringComparer.OrdinalIgnoreCase);

    public bool Priced(string model) => _prices.ContainsKey(Key(model));

    public double Cost(string model, long prompt, long completion) =>
        _prices.TryGetValue(Key(model), out var p) ? (prompt * p.InputPerMillion + completion * p.OutputPerMillion) / 1_000_000 : 0;

    /// <summary>Configuration keys cannot hold ':' (qwen3.5:latest), so it may be written as '_' (qwen3.5_latest).</summary>
    private static string Key(string model) => model.Replace(':', '_');
}

public sealed record UsageQuery(DateTimeOffset? From = null, DateTimeOffset? To = null, string? GroupBy = null, string? User = null);

public sealed record UsageRow(string Key, int Runs, int FailedRuns, int ModelCalls, long PromptTokens, long CompletionTokens, double Cost,
    double ModelSecondsP95, double ErrorRate);

public sealed record UsageReport(string Currency, string GroupBy, DateTimeOffset From, DateTimeOffset To, IReadOnlyList<UsageRow> Rows, UsageRow Total,
    IReadOnlyList<string> UnpricedModels);

/// <summary>
/// Tokens, cost, latency and error rate over time or per user, profile or model (#77). Users see their own usage; admins everyone's
/// (and may filter by user). Cost is computed from the configured price table at read time.
/// </summary>
public sealed class UsageEndpoint(LotsDbContext db, PriceTable prices, ICurrentPrincipal who, IConfiguration config, TimeProvider clock) : Endpoint<UsageQuery, UsageReport>
{
    public override void Configure() => Get("/usage");

    public override async Task HandleAsync(UsageQuery req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var admin = ConversationViews.IsAdmin(me, config);
        var to = req.To ?? clock.GetUtcNow();
        var from = req.From ?? to.AddDays(-30);
        var groupBy = req.GroupBy is "user" or "profile" or "model" ? req.GroupBy : "day";
        if (groupBy == "user" && !admin) groupBy = "day";

        var runs = db.Runs.AsNoTracking().Where(r => r.CreatedAt >= from && r.CreatedAt <= to);
        if (!admin) runs = runs.Where(r => r.UserId == me.UserId);
        else if (!string.IsNullOrWhiteSpace(req.User)) runs = runs.Where(r => r.UserId == req.User);

        var runRows = await runs.Select(r => new RunInfo(r.Id, r.UserId, r.Profile, r.Status, r.CreatedAt)).ToListAsync(ct);
        var ids = runRows.Select(r => r.Id).ToList();
        var steps = await db.RunSteps.AsNoTracking().Where(s => s.Kind == StepKind.ModelCall && ids.Contains(s.RunId))
            .Select(s => new StepInfo(s.RunId, s.Name, s.PromptTokens ?? 0, s.CompletionTokens ?? 0, s.LatencyMs)).ToListAsync(ct);
        var runById = runRows.ToDictionary(r => r.Id);

        string KeyOfRun(Guid id) => groupBy switch
        {
            "user" => runById[id].UserId,
            "profile" => runById[id].Profile,
            _ => runById[id].CreatedAt.UtcDateTime.ToString("yyyy-MM-dd"),
        };

        var stepGroups = steps.GroupBy(s => groupBy == "model" ? s.Model : KeyOfRun(s.RunId)).ToDictionary(g => g.Key, g => g.ToList());
        var runGroups = groupBy == "model"
            ? steps.GroupBy(s => s.Model).ToDictionary(g => g.Key, g => g.Select(s => runById[s.RunId]).DistinctBy(r => r.Id).ToList())
            : runRows.GroupBy(r => KeyOfRun(r.Id)).ToDictionary(g => g.Key, g => g.ToList());

        var keys = runGroups.Keys.Union(stepGroups.Keys).Order(StringComparer.Ordinal).ToList();
        var rows = keys.Select(k => Row(k, runGroups.GetValueOrDefault(k) ?? [], stepGroups.GetValueOrDefault(k) ?? [])).ToList();
        if (groupBy != "day") rows = rows.OrderByDescending(r => r.Cost).ThenByDescending(r => r.PromptTokens + r.CompletionTokens).ToList();
        var total = Row("total", runRows, steps);
        await Send.OkAsync(new UsageReport(prices.Currency, groupBy, from, to, rows, total,
            steps.Select(s => s.Model).Distinct().Where(m => !prices.Priced(m)).Order().ToList()), ct);
    }

    private sealed record RunInfo(Guid Id, string UserId, string Profile, RunStatus Status, DateTimeOffset CreatedAt);

    private sealed record StepInfo(Guid RunId, string Model, int PromptTokens, int CompletionTokens, long LatencyMs);

    private UsageRow Row(string key, IReadOnlyCollection<RunInfo> runs, IReadOnlyCollection<StepInfo> steps)
    {
        var latencies = steps.Select(s => s.LatencyMs / 1000.0).Order().ToList();
        var p95 = latencies.Count == 0 ? 0 : latencies[(int)Math.Clamp(Math.Ceiling(0.95 * latencies.Count) - 1, 0, latencies.Count - 1)];
        var failed = runs.Count(r => r.Status == RunStatus.Failed);
        return new UsageRow(key, runs.Count, failed, steps.Count, steps.Sum(s => (long)s.PromptTokens), steps.Sum(s => (long)s.CompletionTokens),
            Math.Round(steps.Sum(s => prices.Cost(s.Model, s.PromptTokens, s.CompletionTokens)), 4),
            Math.Round(p95, 2), runs.Count == 0 ? 0 : Math.Round((double)failed / runs.Count, 3));
    }
}
