using System.Text.Json;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Core.Outcomes;

/// <summary>
/// How one run went, as one queryable row (#141): the self-improvement loop (ADR 0019) reads these instead of raw traces. Derived
/// entirely from the run, its steps, messages, audit rows, feedback and later runs, so it can be recomputed (and backfilled) at
/// any time. No content: no prompt or answer text, and the user only as a keyed hash.
/// </summary>
public sealed class RunOutcomeRecord
{
    public Guid RunId { get; set; }
    public Guid? ConversationId { get; set; }
    public required string UserHash { get; set; }
    public required string Profile { get; set; }
    public int ProfileVersion { get; set; }
    public string? Model { get; set; }
    public required string Channel { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public long WallMs { get; set; }
    public long ModelMs { get; set; }
    public long ToolMs { get; set; }
    public required string Status { get; set; }
    public int Steps { get; set; }
    public int ModelCalls { get; set; }
    public int ToolCalls { get; set; }
    public int DistinctTools { get; set; }
    public int Fallbacks { get; set; }
    public int TokensIn { get; set; }
    public int TokensOut { get; set; }
    public double Cost { get; set; }
    public int ToolErrors { get; set; }
    public int PolicyDenials { get; set; }
    public int ApprovalRefusals { get; set; }
    public bool TimedOut { get; set; }
    public bool StepLimitHit { get; set; }
    public bool VoiceSkippedTools { get; set; }
    public bool EmptyAnswer { get; set; }
    public bool Refused { get; set; }
    public int AnswerChars { get; set; }
    public bool Spoken { get; set; }
    public bool IsRetry { get; set; }
    /// <summary>How the context was chosen (#150): manual, only, auto, sticky, chosen, corrected, default.</summary>
    public string? Routing { get; set; }
    /// <summary>The user had this turn answered again in another context: the router (or the user's choice) was wrong.</summary>
    public bool Rerouted { get; set; }
    /// <summary>The contexts a multi-context run asked (#151), comma separated; null for a single context.</summary>
    public string? Contexts { get; set; }
    /// <summary>The user ran the same prompt again, or retried this run: the answer did not do it.</summary>
    public bool UserRetried { get; set; }
    /// <summary>Another turn in the same conversation started within <see cref="OutcomeOptions.FollowUpMinutes"/>: often a correction.</summary>
    public bool FollowUp { get; set; }
    public int? FeedbackRating { get; set; }
    public double? JudgeScore { get; set; }
    public string? JudgeRubric { get; set; }
    /// <summary>Per tool: calls and errors, <c>{"list_containers":{"calls":2,"errors":0}}</c>.</summary>
    public string? ToolsJson { get; set; }
    public string? TraceId { get; set; }
    public DateTimeOffset ComputedAt { get; set; }
    /// <summary>Set when something it depends on changed (feedback): the worker computes it again.</summary>
    public bool Dirty { get; set; }
}

public sealed class OutcomeOptions
{
    public const string Section = "Outcomes";
    public int FollowUpMinutes { get; set; } = 2;
    public int IntervalSeconds { get; set; } = 30;
    public int BatchSize { get; set; } = 200;
}

public sealed record ToolTally(int Calls, int Errors);

public static class RunOutcomes
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Phrases of a refusal ("I can't", "inte tillåtet"), English and Swedish, as in the evals.</summary>
    private static readonly string[] RefusalMarkers =
    [
        "not allowed", "not permitted", "no permission", "don't have permission", "do not have permission", "not authorized",
        "cannot do that", "can't do that", "i can't", "i cannot", "unable to",
        "inte tillåt", "kan inte", "saknar behörighet", "har inte behörighet", "får inte",
    ];

    public static bool IsFinished(RunStatus s) => s is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled;

    /// <summary>Computes (or recomputes) the outcomes of these runs and saves them. Unfinished runs are skipped.</summary>
    public static async Task<int> ComputeAsync(LotsDbContext db, IReadOnlyCollection<Guid> runIds, Profiles.ProfileRegistry profiles,
        Features.Usage.PriceTable prices, OutcomeOptions options, TimeProvider clock, CancellationToken ct)
    {
        if (runIds.Count == 0) return 0;
        var runs = await db.Runs.AsNoTracking().Include(r => r.Steps).Include(r => r.Messages)
            .Where(r => runIds.Contains(r.Id)).ToListAsync(ct);
        runs = runs.Where(r => IsFinished(r.Status)).ToList();
        if (runs.Count == 0) return 0;
        var ids = runs.Select(r => r.Id).ToList();
        var audit = (await db.AuditLog.AsNoTracking().Where(a => ids.Contains(a.RunId))
            .Select(a => new { a.RunId, a.Decision }).ToListAsync(ct)).ToLookup(a => a.RunId, a => a.Decision);
        var feedback = (await db.Feedback.AsNoTracking().Where(f => ids.Contains(f.RunId)).ToListAsync(ct))
            .Where(f => runs.Any(r => r.Id == f.RunId && r.UserId == f.UserId)).ToDictionary(f => f.RunId, f => f.Rating);
        var spoken = (await db.VoiceUsage.AsNoTracking().Where(v => v.RunId != null && ids.Contains(v.RunId.Value) && v.Direction == "Tts" && v.Outcome == "ok")
            .Select(v => v.RunId!.Value).ToListAsync(ct)).ToHashSet();
        var users = runs.Select(r => r.UserId).Distinct().ToList();
        var since = runs.Min(r => r.CreatedAt);
        // Later runs of the same users, for the retry and follow-up signals.
        var later = await db.Runs.AsNoTracking().Where(r => users.Contains(r.UserId) && r.CreatedAt >= since)
            .Select(r => new { r.Id, r.UserId, r.Prompt, r.ConversationId, r.CreatedAt, r.RetryOf, r.Profile }).ToListAsync(ct);
        var existing = await db.Set<RunOutcomeRecord>().Where(o => ids.Contains(o.RunId)).ToDictionaryAsync(o => o.RunId, ct);

        foreach (var run in runs)
        {
            var steps = run.Steps.OrderBy(s => s.Seq).ToList();
            var models = steps.Where(s => s.Kind == StepKind.ModelCall).ToList();
            var tools = steps.Where(s => s.Kind == StepKind.ToolCall).ToList();
            var decisions = audit[run.Id].ToList();
            var tally = tools.GroupBy(t => t.Name).ToDictionary(g => g.Key, g => new ToolTally(g.Count(), g.Count(IsToolError)));
            var answer = run.FinalAnswer ?? "";
            var ended = run.UpdatedAt;
            var o = existing.GetValueOrDefault(run.Id) ?? new RunOutcomeRecord
            {
                RunId = run.Id, UserHash = "", Profile = run.Profile, Channel = "", Status = "",
            };
            o.ConversationId = run.ConversationId;
            o.UserHash = Telemetry.UserHash.Of(run.UserId);
            o.Profile = run.Profile;
            o.ProfileVersion = profiles.Find(run.Profile)?.Version ?? 0;
            o.Model = models.FirstOrDefault()?.Name;
            o.Channel = Telemetry.Tracing.Channel(run);
            o.StartedAt = run.CreatedAt;
            o.EndedAt = ended;
            o.WallMs = Math.Max(0, (long)(ended - run.CreatedAt).TotalMilliseconds);
            o.ModelMs = models.Sum(s => s.LatencyMs);
            o.ToolMs = tools.Sum(s => s.LatencyMs);
            o.Status = run.Status.ToString();
            o.Steps = steps.Count;
            o.ModelCalls = models.Count;
            o.ToolCalls = tools.Count;
            o.DistinctTools = tally.Count;
            o.Fallbacks = models.Count(s => s.Routing is not null);
            o.TokensIn = models.Sum(s => s.PromptTokens ?? 0);
            o.TokensOut = models.Sum(s => s.CompletionTokens ?? 0);
            o.Cost = Math.Round(models.Sum(s => prices.Cost(s.Name, s.PromptTokens ?? 0, s.CompletionTokens ?? 0)), 6);
            o.ToolErrors = tally.Values.Sum(t => t.Errors);
            o.PolicyDenials = decisions.Count(d => d == AuditDecision.Denied);
            o.ApprovalRefusals = decisions.Count(d => d == AuditDecision.ApprovalDenied);
            o.TimedOut = run.Error?.StartsWith("Timed out", StringComparison.Ordinal) == true;
            o.StepLimitHit = run.Error?.StartsWith("Stopped after", StringComparison.Ordinal) == true;
            o.VoiceSkippedTools = run.Messages.Any(m => m.Role == "user" && m.Content == Runs.AgentRunner.VoiceToolNudgeText);
            o.EmptyAnswer = run.Status == RunStatus.Completed && string.IsNullOrWhiteSpace(answer);
            o.Refused = RefusalMarkers.Any(m => answer.Contains(m, StringComparison.OrdinalIgnoreCase));
            o.AnswerChars = answer.Length;
            o.Spoken = spoken.Contains(run.Id);
            o.IsRetry = run.RetryOf is not null;
            o.Routing = run.RoutingMode;
            o.Contexts = run.SuperviseJson is null ? null : string.Join(',', JsonSerializer.Deserialize<List<string>>(run.SuperviseJson) ?? []);
            o.Rerouted = later.Any(l => l.RetryOf == run.Id && !string.Equals(l.Profile, run.Profile, StringComparison.OrdinalIgnoreCase));
            o.UserRetried = later.Any(l => l.Id != run.Id && l.UserId == run.UserId && l.CreatedAt > run.CreatedAt
                                            && (l.RetryOf == run.Id || string.Equals(l.Prompt.Trim(), run.Prompt.Trim(), StringComparison.OrdinalIgnoreCase)));
            o.FollowUp = run.ConversationId is { } conv && later.Any(l => l.Id != run.Id && l.ConversationId == conv
                && l.CreatedAt >= ended && l.CreatedAt <= ended.AddMinutes(options.FollowUpMinutes));
            o.FeedbackRating = feedback.TryGetValue(run.Id, out var rating) ? rating : null;
            o.ToolsJson = tally.Count == 0 ? null : JsonSerializer.Serialize(tally, Json);
            o.TraceId = run.TraceId;
            o.ComputedAt = clock.GetUtcNow();
            o.Dirty = false;
            if (!existing.ContainsKey(run.Id))
            {
                db.Set<RunOutcomeRecord>().Add(o);
                Telemetry.LotsMetrics.RunOutcomes.Add(1, new("profile", o.Profile), new("channel", o.Channel), new("status", o.Status),
                    new("problem", Problem(o)));
            }
        }
        await db.SaveChangesAsync(ct);
        return runs.Count;
    }

    /// <summary>The single most telling problem of a run, for a low-cardinality metric label.</summary>
    public static string Problem(RunOutcomeRecord o) =>
        o.TimedOut ? "timeout" : o.StepLimitHit ? "step_limit" : o.Status == "Failed" ? "failed" : o.Status == "Cancelled" ? "cancelled"
        : o.Rerouted ? "misrouted" : o.ApprovalRefusals > 0 ? "approval_refused" : o.PolicyDenials > 0 ? "denied" : o.ToolErrors > 0 ? "tool_error"
        : o.EmptyAnswer ? "empty_answer" : o.VoiceSkippedTools ? "voice_skipped_tools" : o.Refused ? "refused" : "none";

    private static bool IsToolError(RunStepRecord s) => s.Result?.Contains("Error:", StringComparison.Ordinal) == true;
}

/// <summary>
/// Keeps outcomes current (#141): finished runs without one (this also backfills old runs), outcomes marked dirty (feedback), and
/// runs that ended recently, whose retry and follow-up signals may still change. Idempotent, so every replica may run it.
/// </summary>
public sealed class RunOutcomeWorker(IServiceScopeFactory scopes, Microsoft.Extensions.Options.IOptions<OutcomeOptions> options, TimeProvider clock,
    ILogger<RunOutcomeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stop).ContinueWith(_ => { });
        while (!stop.IsCancellationRequested)
        {
            try
            {
                while (await RunOnceAsync(stop) >= options.Value.BatchSize) { } // backfill in batches until caught up
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                logger.LogWarning("Run outcome update failed: {Error}", ex.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.IntervalSeconds), stop).ContinueWith(_ => { });
        }
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var o = options.Value;
        var recent = clock.GetUtcNow().AddMinutes(-(o.FollowUpMinutes + 1));
        var finished = new[] { RunStatus.Completed, RunStatus.Failed, RunStatus.Cancelled };
        var missing = await db.Runs.AsNoTracking().Where(r => finished.Contains(r.Status) && !db.Set<RunOutcomeRecord>().Any(x => x.RunId == r.Id))
            .OrderBy(r => r.CreatedAt).Select(r => r.Id).Take(o.BatchSize).ToListAsync(ct);
        var stale = await db.Set<RunOutcomeRecord>().AsNoTracking().Where(x => x.Dirty || x.EndedAt >= recent)
            .Select(x => x.RunId).Take(o.BatchSize).ToListAsync(ct);
        try
        {
            await RunOutcomes.ComputeAsync(db, missing.Concat(stale).Distinct().ToList(),
                scope.ServiceProvider.GetRequiredService<Profiles.ProfileRegistry>(),
                scope.ServiceProvider.GetRequiredService<Features.Usage.PriceTable>(), o, clock, ct);
            return missing.Count; // a full batch of new ones: there may be more to backfill
        }
        catch (DbUpdateException)
        {
            return 0; // another replica wrote the same outcomes first: fine, they are equal
        }
    }
}
