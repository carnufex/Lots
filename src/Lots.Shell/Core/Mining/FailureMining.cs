using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lots.Shell.Core.Knowledge;
using Lots.Shell.Core.Outcomes;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Core.Mining;

public enum CandidateState { Open, Accepted, Rejected }

/// <summary>
/// A cluster of runs that went wrong the same way (#143), and the eval case drafted from it. Nothing reaches a dataset until a person
/// accepts it (and may edit it first).
/// </summary>
public sealed class EvalCandidateRecord
{
    public Guid Id { get; set; }
    /// <summary>Stable cluster identity: signature plus the representative prompt's hash, so a re-run of mining updates the same row.</summary>
    public required string ClusterKey { get; set; }
    public required string Signature { get; set; }
    public required string Profile { get; set; }
    public int ProfileVersion { get; set; }
    public required string Problem { get; set; }
    public int Runs { get; set; }
    public int Impact { get; set; }
    /// <summary>The most recent run ids of the cluster (up to 20), as JSON.</summary>
    public required string RunIdsJson { get; set; }
    /// <summary>The drafted case in the dataset format of Lots.Evals, personal data and secrets masked.</summary>
    public required string DraftJson { get; set; }
    /// <summary>The case as accepted (possibly edited).</summary>
    public string? CaseJson { get; set; }
    public CandidateState State { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

public sealed class MiningOptions
{
    public const string Section = "Mining";
    public bool Enabled { get; set; } = true;
    public int IntervalHours { get; set; } = 24;
    public int WindowDays { get; set; } = 7;
    /// <summary>Prompts closer than this (cosine) share a cluster inside one signature, when an embedding model is configured.</summary>
    public double Similarity { get; set; } = 0.82;
    public int MaxRunsPerSignature { get; set; } = 60;
}

public sealed record MinedCase(string Id, string Question, string? Profile = null, List<string>? ExpectedTools = null, List<string>? ForbiddenTools = null,
    bool ExpectRefusal = false, string? Judge = null);

public sealed record MiningSummary(int RunsWithSignals, int Clusters, int NewCandidates, int UpdatedCandidates);

public static class FailureMiner
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Security.PiiKind[] AllPii = Enum.GetValues<Security.PiiKind>();

    /// <summary>How bad a problem is for the user; impact = runs x severity.</summary>
    public static int Severity(string problem) => problem switch
    {
        "failed" or "timeout" or "step_limit" or "thumbs_down" => 3,
        "denied" or "approval_refused" or "tool_error" or "voice_skipped_tools" or "empty_answer" => 2,
        _ => 1,
    };

    /// <summary>The problem a run is mined for, or null when it went fine. Thumbs-down, retries and slowness count as problems here.</summary>
    public static string? Signal(RunOutcomeRecord o, long slowMs)
    {
        var p = RunOutcomes.Problem(o);
        if (p is not ("none" or "refused" or "cancelled")) return p; // a refusal is often right; a cancel is the user's choice
        if (o.FeedbackRating < 0) return "thumbs_down";
        if (o.UserRetried) return "retried";
        if (slowMs > 0 && o.WallMs > slowMs) return "slow";
        return null;
    }

    public static async Task<MiningSummary> MineAsync(LotsDbContext db, IEmbeddingModel? embeddings, MiningOptions options, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var since = now.AddDays(-options.WindowDays);
        var outcomes = await db.RunOutcomes.AsNoTracking().Where(o => o.EndedAt >= since).ToListAsync(ct);
        // Slow = more than three times the median of the same profile and channel, and at least 10 s.
        var medians = outcomes.GroupBy(o => (o.Profile, o.Channel)).ToDictionary(g => g.Key, g => Median(g.Select(o => o.WallMs)));
        var signalled = outcomes
            .Select(o => (o, problem: Signal(o, Math.Max(10_000, 3 * medians[(o.Profile, o.Channel)]))))
            .Where(x => x.problem is not null).ToList();
        if (signalled.Count == 0) return new MiningSummary(0, 0, 0, 0);

        var ids = signalled.Select(x => x.o.RunId).ToList();
        var runs = await db.Runs.AsNoTracking().Include(r => r.Steps).Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var audit = (await db.AuditLog.AsNoTracking().Where(a => ids.Contains(a.RunId) && (a.Decision == AuditDecision.Denied || a.Decision == AuditDecision.ApprovalDenied))
            .Select(a => new { a.RunId, a.Tool }).ToListAsync(ct)).ToLookup(a => a.RunId, a => a.Tool);
        var comments = (await db.Feedback.AsNoTracking().Where(f => ids.Contains(f.RunId) && f.Rating < 0 && f.Comment != null)
            .Select(f => new { f.RunId, f.Comment }).ToListAsync(ct)).ToDictionary(f => f.RunId, f => f.Comment!);

        var groups = signalled.Where(x => runs.ContainsKey(x.o.RunId))
            .Select(x => (x.o, x.problem!, run: runs[x.o.RunId], detail: Detail(x.problem!, runs[x.o.RunId], audit[x.o.RunId].FirstOrDefault())))
            .GroupBy(x => $"{x.o.Profile} v{x.o.ProfileVersion} | {x.Item2}" + (x.detail is null ? "" : $" | {x.detail}"));

        var existing = await db.EvalCandidates.ToDictionaryAsync(c => c.ClusterKey, ct);
        int clusters = 0, added = 0, updated = 0;
        foreach (var group in groups)
        {
            var members = group.OrderByDescending(x => x.o.EndedAt).Take(options.MaxRunsPerSignature).ToList();
            foreach (var cluster in await SplitByPromptAsync(members.Select(m => (m.o, m.Item2, m.run, m.detail)).ToList(), embeddings, options.Similarity, ct))
            {
                clusters++;
                var head = cluster[0];
                var question = Clean(head.run.Prompt);
                var key = group.Key + " | " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(question.ToLowerInvariant())))[..12];
                var draft = Draft(key, question, head.o.Profile, head.problem, head.detail, head.run, comments.GetValueOrDefault(head.o.RunId));
                var runIds = JsonSerializer.Serialize(cluster.Select(c => c.o.RunId).Take(20), Json);
                if (existing.TryGetValue(key, out var row))
                {
                    if (row.State != CandidateState.Open) continue; // decided: a person's accept or reject stands
                    row.Runs = cluster.Count;
                    row.Impact = cluster.Count * Severity(head.problem);
                    row.RunIdsJson = runIds;
                    row.DraftJson = JsonSerializer.Serialize(draft, Json);
                    row.LastSeen = now;
                    updated++;
                }
                else
                {
                    db.EvalCandidates.Add(new EvalCandidateRecord
                    {
                        Id = Guid.NewGuid(), ClusterKey = key, Signature = group.Key, Profile = head.o.Profile, ProfileVersion = head.o.ProfileVersion,
                        Problem = head.problem, Runs = cluster.Count, Impact = cluster.Count * Severity(head.problem), RunIdsJson = runIds,
                        DraftJson = JsonSerializer.Serialize(draft, Json), FirstSeen = now, LastSeen = now,
                    });
                    added++;
                }
            }
        }
        await db.SaveChangesAsync(ct);
        return new MiningSummary(signalled.Count, clusters, added, updated);
    }

    /// <summary>What distinguishes failures of the same problem: the tool that failed or was denied, and how.</summary>
    private static string? Detail(string problem, RunRecord run, string? deniedTool) => problem switch
    {
        "denied" or "approval_refused" => deniedTool,
        "tool_error" => run.Steps.Where(s => s.Kind == StepKind.ToolCall && s.Result?.Contains("Error:", StringComparison.Ordinal) == true)
            .Select(s => $"{s.Name} {ErrorKind(s.Result!)}").FirstOrDefault(),
        "voice_skipped_tools" => "voice",
        _ => null,
    };

    private static string ErrorKind(string result) =>
        result.Contains("timed out", StringComparison.OrdinalIgnoreCase) ? "timeout"
        : result.Contains("not found", StringComparison.OrdinalIgnoreCase) || result.Contains("no such", StringComparison.OrdinalIgnoreCase)
          || result.Contains("No container", StringComparison.OrdinalIgnoreCase) ? "not-found"
        : result.Contains("invalid", StringComparison.OrdinalIgnoreCase) || result.Contains("argument", StringComparison.OrdinalIgnoreCase) ? "bad-arguments"
        : "other";

    /// <summary>Greedy clustering on prompt embeddings; without an embedding model one signature is one cluster.</summary>
    private static async Task<List<List<(RunOutcomeRecord o, string problem, RunRecord run, string? detail)>>> SplitByPromptAsync(
        List<(RunOutcomeRecord o, string problem, RunRecord run, string? detail)> members, IEmbeddingModel? embeddings, double threshold, CancellationToken ct)
    {
        if (embeddings is not { Configured: true } || members.Count < 2) return [members];
        float[][] vectors;
        try
        {
            vectors = (await embeddings.EmbedAsync(members.Select(m => Clean(m.run.Prompt)).ToList(), ct)).Vectors.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [members]; // embeddings are a refinement, never a reason to stop mining
        }
        var clusters = new List<(float[] Centre, List<int> Members)>();
        for (var i = 0; i < members.Count; i++)
        {
            var best = clusters.Select((c, ci) => (ci, sim: Cosine(c.Centre, vectors[i]))).OrderByDescending(x => x.sim).FirstOrDefault();
            if (clusters.Count > 0 && best.sim >= threshold) clusters[best.ci].Members.Add(i);
            else clusters.Add((vectors[i], [i]));
        }
        return clusters.Select(c => c.Members.Select(i => members[i]).ToList()).ToList();
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    /// <summary>The question with secrets and every kind of personal data masked: it may end up in a dataset file.</summary>
    public static string Clean(string prompt) => Security.PiiRedactor.Redact(Security.SecretRedactor.Redact(prompt.Trim()), AllPii);

    private static MinedCase Draft(string key, string question, string profile, string problem, string? detail, RunRecord run, string? comment)
    {
        var id = "mined-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8].ToLowerInvariant();
        var tool = detail?.Split(' ')[0];
        var usedTools = run.Steps.Where(s => s.Kind == StepKind.ToolCall && s.Result?.Contains("Error:", StringComparison.Ordinal) != true)
            .Select(s => s.Name).Distinct().ToList();
        return problem switch
        {
            "denied" => new MinedCase(id, question, profile, ForbiddenTools: tool is null ? null : [tool],
                Judge: $"Answers without trying {tool ?? "a tool"} that this user may not use, or explains why it cannot."),
            "approval_refused" => new MinedCase(id, question, profile,
                Judge: $"Handles the refused approval for {tool ?? "the tool"}: says the action did not happen and does not claim it did."),
            "tool_error" => new MinedCase(id, question, profile, ExpectedTools: tool is null ? null : [tool],
                Judge: $"Answers the question; the call to {tool ?? "the tool"} succeeds ({detail?.Split(' ').LastOrDefault()} before)."),
            "voice_skipped_tools" => new MinedCase(id, question, profile, ExpectedTools: usedTools.Count > 0 ? usedTools : null,
                Judge: "Looks the answer up with the tools instead of answering from memory."),
            "thumbs_down" => new MinedCase(id, question, profile,
                Judge: "A good answer to this question" + (comment is null ? "." : $": the user found fault with the last one ({Clean(comment)}).")),
            "retried" => new MinedCase(id, question, profile, Judge: "Answers well enough that the user does not need to ask again."),
            "slow" => new MinedCase(id, question, profile, Judge: "Answers correctly, without unnecessary tool calls."),
            _ => new MinedCase(id, question, profile, Judge: "Completes with a correct answer instead of failing."),
        };
    }

    private static long Median(IEnumerable<long> values)
    {
        var v = values.Order().ToList();
        return v.Count == 0 ? 0 : v[v.Count / 2];
    }
}

/// <summary>Runs the mining on a schedule (<c>Mining:IntervalHours</c>); POST /insights/mine runs it on demand.</summary>
public sealed class FailureMiningWorker(IServiceScopeFactory scopes, Microsoft.Extensions.Options.IOptions<MiningOptions> options, TimeProvider clock,
    ILogger<FailureMiningWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stop).ContinueWith(_ => { });
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var summary = await FailureMiner.MineAsync(scope.ServiceProvider.GetRequiredService<LotsDbContext>(),
                    scope.ServiceProvider.GetService<IEmbeddingModel>(), options.Value, clock, stop);
                logger.LogInformation("Failure mining: {Runs} runs with signals, {Clusters} clusters, {New} new and {Updated} updated candidates",
                    summary.RunsWithSignals, summary.Clusters, summary.NewCandidates, summary.UpdatedCandidates);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                logger.LogWarning("Failure mining failed: {Error}", ex.Message);
            }
            await Task.Delay(TimeSpan.FromHours(Math.Max(1, options.Value.IntervalHours)), stop).ContinueWith(_ => { });
        }
    }
}
