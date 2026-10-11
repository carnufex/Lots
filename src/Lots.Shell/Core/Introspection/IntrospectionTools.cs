using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lots.Shell.Core.Outcomes;
using Lots.Shell.Core.Tools;
using Lots.Shell.Features.Runs;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Core.Introspection;

public sealed class IntrospectionOptions
{
    public const string Section = "Introspection";
    /// <summary>Loki base URL for <c>search_logs</c>, e.g. http://loki:3100. Empty: the tool says logs are not configured.</summary>
    public string? LokiUrl { get; set; }
    /// <summary>Prometheus base URL for <c>promql_query</c>, e.g. http://prometheus:9090.</summary>
    public string? PrometheusUrl { get; set; }
    public int MaxDays { get; set; } = 30;
    public int MaxRows { get; set; } = 25;
}

/// <summary>
/// How the shell has been doing, as read-only tools for an analysing agent (#142, ADR 0019). They exist only where a profile
/// declares them (the <c>self-improve</c> profile), so policy, audit and the untrusted-data envelope apply like for any tool.
/// Bounded by construction: days, rows and output are capped. Metadata comes from run outcomes and the audit log; content (prompts,
/// tool arguments and results, error text) only for runs the caller may read anyway (owner, admin, viewer).
/// </summary>
public sealed class IntrospectionToolSource(IServiceScopeFactory scopes, Microsoft.Extensions.Options.IOptions<IntrospectionOptions> options,
    IHttpClientFactory http, TimeProvider clock, IConfiguration config) : IToolSource
{
    public const string ServerName = "introspect";

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly string Days = """ "days": { "type": "integer", "description": "How many days back (1-30, default 7)." } """;
    private static readonly string Filters = Days + """, "profile": { "type": "string" }, "channel": { "type": "string", "description": "web, voice, api, schedule, slack, email, delegate" } """;

    public static readonly IReadOnlyList<ToolDescriptor> Tools =
    [
        new("outcomes_summary", "Success rate, p50/p95 time, cost and the most common problems per profile version and channel, from run outcomes.",
            Schema("{ \"type\": \"object\", \"properties\": {" + Filters + "} }")),
        new("list_failed_runs", "Runs that went wrong (failed, timed out, denied, tool errors, refused, rated bad, ...) with their run ids, newest first.",
            Schema("{ \"type\": \"object\", \"properties\": {" + Filters + ", \"problem\": { \"type\": \"string\", \"description\": \"timeout, step_limit, failed, cancelled, approval_refused, denied, tool_error, empty_answer, voice_skipped_tools, refused, thumbs_down\" }, \"limit\": { \"type\": \"integer\" } } }")),
        new("get_run_trace", "The steps of one run as a compact tree: model and tool calls with time, tokens and policy decisions. Content only for runs you may read.",
            Schema("""{ "type": "object", "properties": { "run_id": { "type": "string" } }, "required": ["run_id"] }""")),
        new("get_run_audit", "The audit rows of one run: tool, decision, reason, approver and result status.",
            Schema("""{ "type": "object", "properties": { "run_id": { "type": "string" } }, "required": ["run_id"] }""")),
        new("tool_error_breakdown", "Per tool: calls, errors and error rate, with the kinds of errors (timeout, denied, not found, other).",
            Schema("{ \"type\": \"object\", \"properties\": {" + Filters + "} }")),
        new("slow_stages", "Where time goes: p50/p95 of model, tool and total time per profile version and channel, and the slowest runs.",
            Schema("{ \"type\": \"object\", \"properties\": {" + Filters + "} }")),
        new("compare_profile_versions", "Outcomes of each version of one profile side by side, to see whether a change helped.",
            Schema("{ \"type\": \"object\", \"properties\": {" + Days + ", \"profile\": { \"type\": \"string\" } }, \"required\": [\"profile\"] }")),
        new("retrieval_misses", "Knowledge searches that found nothing, with their runs (the query text only for runs you may read).",
            Schema("{ \"type\": \"object\", \"properties\": {" + Days + "} }")),
        new("knowledge_retrieval_debug", "Runs a knowledge search as the caller and shows every stage (#158): sources searched, vector and word rankings with " +
            "scores, the fused result, matches hidden by access rights (counts only) and why nothing was found. Same code as search_knowledge.",
            Schema("""{ "type": "object", "properties": { "query": { "type": "string" }, "k": { "type": "integer", "description": "1-20, default 4" }, "source": { "type": "string" } }, "required": ["query"] }""")),
        new("knowledge_health", "Health of the knowledge index (#158): vectors from another embedding model, broken vectors, empty/oversized and duplicate " +
            "chunks, documents without chunks, stale documents and failed sources.", Schema("{ \"type\": \"object\", \"properties\": {} }")),
        new("search_logs", "Log lines of the shell from Loki that contain a text, newest first (needs Introspection:LokiUrl).",
            Schema("""{ "type": "object", "properties": { "contains": { "type": "string" }, "level": { "type": "string", "description": "error, warn, info" }, "minutes": { "type": "integer", "description": "1-1440, default 60" } } }""")),
        new("propose_change", "Proposes a change to a profile's instructions, description, model alias or conflict detection, with the evidence. " +
            "It only creates a proposal for a person to evaluate and merge; nothing changes in the running shell.",
            Schema("""{ "type": "object", "properties": { "profile": { "type": "string" }, "title": { "type": "string" }, "rationale": { "type": "string", "description": "Why, citing run ids and numbers" }, "instructions": { "type": "string", "description": "The complete new instructions" }, "description": { "type": "string" }, "model": { "type": "string" }, "run_ids": { "type": "array", "items": { "type": "string" } } }, "required": ["profile", "title", "rationale"] }""")),
        new("promql_query", "A PromQL query over Lots metrics only (names starting with lots_), as a range (needs Introspection:PrometheusUrl).",
            Schema("""{ "type": "object", "properties": { "query": { "type": "string" }, "minutes": { "type": "integer", "description": "1-1440, default 60" } }, "required": ["query"] }""")),
    ];

    public Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct) => Task.FromResult(Tools);

    public Task<string?> ServerOfAsync(string toolName, CancellationToken ct) => Task.FromResult<string?>(Tools.Any(t => t.Name == toolName) ? ServerName : null);

    public Task<IReadOnlyList<ServerStatus>> StatusAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ServerStatus>>(
        [new ServerStatus(ServerName, "builtin:introspect", ServerStatus.BuiltIn, "ok", Tools, null, clock.GetUtcNow())]);

    public async Task<string> CallAsync(string name, string argumentsJson, CancellationToken ct)
    {
        var context = ToolCallContext.Current ?? throw new InvalidOperationException("Introspection needs the caller's identity.");
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var a = doc.RootElement;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var text = name switch
        {
            "outcomes_summary" => Summary(await OutcomesAsync(db, a, ct)),
            "list_failed_runs" => FailedRuns(await OutcomesAsync(db, a, ct), Str(a, "problem"), Int(a, "limit", options.Value.MaxRows, 1, options.Value.MaxRows)),
            "get_run_trace" => await TraceAsync(db, a, context, ct),
            "get_run_audit" => await AuditAsync(db, a, context, ct),
            "tool_error_breakdown" => await ToolErrorsAsync(db, await OutcomesAsync(db, a, ct), ct),
            "slow_stages" => Slow(await OutcomesAsync(db, a, ct)),
            "compare_profile_versions" => Versions(await OutcomesAsync(db, a, ct), Str(a, "profile") ?? ""),
            "retrieval_misses" => await RetrievalMissesAsync(db, a, context, ct),
            "knowledge_retrieval_debug" => await RetrievalDebugAsync(a, context, scope.ServiceProvider, ct),
            "knowledge_health" => await KnowledgeHealthAsync(scope.ServiceProvider, ct),
            "search_logs" => await LogsAsync(a, ct),
            "promql_query" => await PromAsync(a, ct),
            "propose_change" => await ProposeAsync(db, a, context, scope.ServiceProvider, ct),
            _ => throw new InvalidOperationException($"Unknown tool '{name}'."),
        };
        return text;
    }

    // ---- knowledge (#158) ----

    private static async Task<string> RetrievalDebugAsync(JsonElement a, ToolCallContext context, IServiceProvider sp, CancellationToken ct)
    {
        var store = sp.GetRequiredService<Knowledge.IKnowledgeStore>();
        var embeddings = sp.GetRequiredService<Knowledge.IEmbeddingModel>();
        var query = Str(a, "query") ?? "";
        if (query.Length == 0) return "Error: query is required.";
        // As the caller: an agent debugging retrieval sees what its user would see, never more.
        var trace = await Knowledge.KnowledgeToolSource.TraceAsync(store, embeddings, query, Knowledge.KnowledgeAccess.TokensOf(context.Principal),
            Int(a, "k", 4, 1, 20), Str(a, "source"), explain: true, ct);
        var readable = (await store.ListSourcesAsync(ct)).Where(s => trace.SearchedSources.Contains(s.Id)).ToList();
        var sb = new StringBuilder($"Model {trace.Model ?? "none"} ({trace.Dims} dims); searched sources: {string.Join(", ", trace.SearchedSources)}; terms: {string.Join(" ", trace.Terms)}\n");
        sb.AppendLine("By vector: " + string.Join("; ", trace.ByVector.Take(8).Select(h => $"#{h.Rank} {h.ChunkId} {h.Title} ({h.Raw})")));
        sb.AppendLine("By words: " + string.Join("; ", trace.ByText.Take(8).Select(h => $"#{h.Rank} {h.ChunkId} {h.Title} ({h.Raw})")));
        sb.AppendLine("Final: " + string.Join("; ", trace.Final.Select((h, i) => $"k{i + 1} {h.ChunkId} {h.Title} (rrf {h.Score}, v{h.VectorRank?.ToString() ?? "-"}/t{h.TextRank?.ToString() ?? "-"})")));
        if (trace.Hidden.Count > 0) sb.AppendLine("Hidden by access rights: " + string.Join(", ", trace.Hidden.Select(h => $"{h.Name} {h.Matches}")));
        if (Knowledge.KnowledgeInspection.Explain(trace, readable) is { } why) sb.AppendLine("Why: " + why);
        return sb.ToString().TrimEnd();
    }

    private static async Task<string> KnowledgeHealthAsync(IServiceProvider sp, CancellationToken ct)
    {
        var store = sp.GetRequiredService<Knowledge.IKnowledgeStore>();
        var embeddings = sp.GetRequiredService<Knowledge.IEmbeddingModel>();
        var sources = await store.ListSourcesAsync(ct);
        var docs = new Dictionary<string, List<Knowledge.DocumentState>>();
        foreach (var s in sources) docs[s.Id] = await store.DocumentsAsync(s.Id, ct);
        var h = Knowledge.KnowledgeInspection.Check(sources, await store.ChunkMetaAsync(50_000, ct), docs, embeddings.Configured ? embeddings.Model : null, null,
            DateTimeOffset.UtcNow);
        var sb = new StringBuilder($"{h.Documents} documents, {h.Chunks} chunks; query model {h.CurrentModel ?? "none"}.\n");
        foreach (var s in h.Sources)
            sb.AppendLine($"{s.Name} ({s.Status}): {s.Documents} docs, {s.Chunks} chunks, on current model {s.OnCurrentModel}, models {string.Join(", ", s.Models.Select(m => $"{m.Key}={m.Value}"))}");
        foreach (var g in h.Issues.GroupBy(i => i.Kind))
            sb.AppendLine($"{g.Key}: {g.Count()} (e.g. {g.First().Detail})");
        return sb.ToString().TrimEnd();
    }

    // ---- data ----

    private async Task<List<RunOutcomeRecord>> OutcomesAsync(LotsDbContext db, JsonElement a, CancellationToken ct)
    {
        var since = clock.GetUtcNow().AddDays(-Int(a, "days", 7, 1, options.Value.MaxDays));
        var q = db.RunOutcomes.AsNoTracking().Where(o => o.EndedAt >= since);
        if (Str(a, "profile") is { } p) q = q.Where(o => o.Profile == p);
        if (Str(a, "channel") is { } c) q = q.Where(o => o.Channel == c);
        return await q.OrderByDescending(o => o.EndedAt).Take(20_000).ToListAsync(ct);
    }

    private static string Summary(List<RunOutcomeRecord> rows)
    {
        if (rows.Count == 0) return "No finished runs in this period.";
        var sb = new StringBuilder($"{rows.Count} runs.\n");
        foreach (var g in rows.GroupBy(o => $"{o.Profile} v{o.ProfileVersion} / {o.Channel}").OrderByDescending(g => g.Count()))
            sb.AppendLine(Line(g.Key, g.ToList()));
        return sb.ToString().TrimEnd();
    }

    private static string Line(string key, List<RunOutcomeRecord> runs)
    {
        var agg = Features.Insights.OutcomesEndpoint.Aggregate(key, runs);
        var problems = string.Join(", ", agg.Problems.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value}"));
        return $"{key}: {agg.Runs} runs, success {agg.SuccessRate:P0}, p50 {agg.P50WallMs} ms, p95 {agg.P95WallMs} ms, cost {agg.Cost.ToString(CultureInfo.InvariantCulture)}" +
               (problems.Length > 0 ? $", problems: {problems}" : "");
    }

    private static string FailedRuns(List<RunOutcomeRecord> rows, string? problem, int limit)
    {
        var bad = rows.Select(o => (o, p: o.FeedbackRating < 0 && RunOutcomes.Problem(o) == "none" ? "thumbs_down" : RunOutcomes.Problem(o)))
            .Where(x => x.p != "none" && (problem is null || x.p == problem)).Take(limit).ToList();
        if (bad.Count == 0) return "No runs with problems in this period" + (problem is null ? "." : $" for '{problem}'.");
        var sb = new StringBuilder();
        foreach (var (o, p) in bad)
            sb.AppendLine($"run {o.RunId} | {o.EndedAt:u} | {o.Profile} v{o.ProfileVersion} | {o.Channel} | {p} | model {o.Model ?? "-"} | {o.WallMs} ms" +
                          (o.FeedbackRating is { } r ? $" | rated {(r > 0 ? "good" : "bad")}" : "") + (o.FollowUp ? " | followed up" : "") + (o.UserRetried ? " | retried" : ""));
        return sb.ToString().TrimEnd();
    }

    private async Task<string> TraceAsync(LotsDbContext db, JsonElement a, ToolCallContext context, CancellationToken ct)
    {
        if (!Guid.TryParse(Str(a, "run_id"), out var id)) return "Error: run_id must be a run id.";
        var run = await db.Runs.AsNoTracking().Include(r => r.Steps).SingleOrDefaultAsync(r => r.Id == id, ct);
        if (run is null) return $"No run {id}.";
        var content = RunAccess.CanRead(run, context.Principal, config);
        var decisions = await db.AuditLog.AsNoTracking().Where(x => x.RunId == id).ToListAsync(ct);
        var sb = new StringBuilder($"run {run.Id} | {run.Profile} | {run.Status} | {run.CreatedAt:u} | trace {run.TraceId ?? "-"}\n");
        if (content)
        {
            sb.AppendLine($"prompt: {Cut(run.Prompt, 300)}");
            if (run.Error is { } e) sb.AppendLine($"error: {Cut(e, 300)}");
        }
        else sb.AppendLine("(content hidden: you may not read this run; metadata only)");
        foreach (var s in run.Steps.OrderBy(s => s.Seq))
        {
            var decision = s.Kind == StepKind.ToolCall ? decisions.LastOrDefault(d => d.ToolCallId == s.ToolCallId)?.Decision.ToString() : null;
            sb.Append($"  {s.Seq}. {(s.Kind == StepKind.ModelCall ? "model" : "tool")} {s.Name} {s.LatencyMs} ms");
            if (s.PromptTokens is { } pt) sb.Append($" tokens {pt}/{s.CompletionTokens}");
            if (decision is not null) sb.Append($" decision {decision}");
            if (s.Routing is { } r) sb.Append($" rerouted: {r}");
            if (s.Flagged) sb.Append(" [flagged as possible injection]");
            sb.AppendLine();
            if (content && s.Kind == StepKind.ToolCall)
            {
                if (s.ArgumentsJson is { } args) sb.AppendLine($"     args: {Cut(args, 200)}");
                if (s.Result is { } res) sb.AppendLine($"     result: {Cut(res, 300)}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> AuditAsync(LotsDbContext db, JsonElement a, ToolCallContext context, CancellationToken ct)
    {
        if (!Guid.TryParse(Str(a, "run_id"), out var id)) return "Error: run_id must be a run id.";
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (run is null) return $"No run {id}.";
        var rows = await db.AuditLog.AsNoTracking().Where(x => x.RunId == id).OrderBy(x => x.At).Take(100).ToListAsync(ct);
        if (rows.Count == 0) return $"No audit rows for run {id}.";
        var content = RunAccess.CanRead(run, context.Principal, config);
        var sb = new StringBuilder();
        foreach (var r in rows)
            sb.AppendLine($"{r.At:u} | {r.Tool} | {r.Decision} | {r.Reason}" + (r.ApproverId is { } ap ? $" | approver {(content ? ap : "(hidden)")}" : "") +
                          (r.ResultStatus is { } rs ? $" | result {rs}" : "") + (r.BackendAuth is { } b ? $" | backend {b}" : ""));
        return sb.ToString().TrimEnd();
    }

    private static async Task<string> ToolErrorsAsync(LotsDbContext db, List<RunOutcomeRecord> rows, CancellationToken ct)
    {
        var ids = rows.Where(o => o.ToolErrors > 0 || o.PolicyDenials > 0).Select(o => o.RunId).Take(2000).ToList();
        var steps = await db.RunSteps.AsNoTracking().Where(s => ids.Contains(s.RunId) && s.Kind == StepKind.ToolCall)
            .Select(s => new { s.Name, s.Result }).ToListAsync(ct);
        var tallies = rows.Where(o => o.ToolsJson is not null)
            .SelectMany(o => JsonSerializer.Deserialize<Dictionary<string, ToolTally>>(o.ToolsJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .GroupBy(kv => kv.Key).Select(g => (Tool: g.Key, Calls: g.Sum(x => x.Value.Calls), Errors: g.Sum(x => x.Value.Errors)))
            .OrderByDescending(t => t.Errors).ThenByDescending(t => t.Calls).ToList();
        if (tallies.Count == 0) return "No tool calls in this period.";
        var sb = new StringBuilder();
        foreach (var t in tallies.Take(30))
        {
            var kinds = steps.Where(s => s.Name == t.Tool && s.Result?.Contains("Error:", StringComparison.Ordinal) == true)
                .GroupBy(s => ErrorKind(s.Result!)).Select(g => $"{g.Key} {g.Count()}");
            sb.AppendLine($"{t.Tool}: {t.Calls} calls, {t.Errors} errors ({(t.Calls == 0 ? 0 : (double)t.Errors / t.Calls):P0})" +
                          (t.Errors > 0 ? $" [{string.Join(", ", kinds)}]" : ""));
        }
        return sb.ToString().TrimEnd();
    }

    private static string ErrorKind(string result) =>
        result.Contains("timed out", StringComparison.OrdinalIgnoreCase) ? "timeout"
        : result.Contains("not permitted", StringComparison.OrdinalIgnoreCase) || result.Contains("denied", StringComparison.OrdinalIgnoreCase) ? "denied"
        : result.Contains("not found", StringComparison.OrdinalIgnoreCase) || result.Contains("no such", StringComparison.OrdinalIgnoreCase) ? "not found"
        : "other";

    private static string Slow(List<RunOutcomeRecord> rows)
    {
        if (rows.Count == 0) return "No finished runs in this period.";
        static long P(IEnumerable<long> v, int p) => EvalPercentile(v.ToList(), p);
        var sb = new StringBuilder();
        foreach (var g in rows.GroupBy(o => $"{o.Profile} v{o.ProfileVersion} / {o.Channel}").OrderByDescending(g => g.Count()))
            sb.AppendLine($"{g.Key}: total p50 {P(g.Select(o => o.WallMs), 50)} / p95 {P(g.Select(o => o.WallMs), 95)} ms, model p95 {P(g.Select(o => o.ModelMs), 95)} ms, " +
                          $"tools p95 {P(g.Select(o => o.ToolMs), 95)} ms ({g.Count()} runs)");
        sb.AppendLine("slowest: " + string.Join("; ", rows.OrderByDescending(o => o.WallMs).Take(5).Select(o => $"run {o.RunId} {o.WallMs} ms ({o.Channel})")));
        return sb.ToString().TrimEnd();
    }

    private static long EvalPercentile(List<long> v, int p)
    {
        if (v.Count == 0) return 0;
        v.Sort();
        return v[Math.Clamp((int)Math.Ceiling(p / 100.0 * v.Count) - 1, 0, v.Count - 1)];
    }

    private static string Versions(List<RunOutcomeRecord> rows, string profile)
    {
        var mine = rows.Where(o => o.Profile == profile).ToList();
        if (mine.Count == 0) return $"No runs of profile '{profile}' in this period.";
        return string.Join("\n", mine.GroupBy(o => o.ProfileVersion).OrderBy(g => g.Key).Select(g => Line($"{profile} v{g.Key}", g.ToList())));
    }

    private async Task<string> RetrievalMissesAsync(LotsDbContext db, JsonElement a, ToolCallContext context, CancellationToken ct)
    {
        var since = clock.GetUtcNow().AddDays(-Int(a, "days", 7, 1, options.Value.MaxDays));
        var misses = await db.RunSteps.AsNoTracking()
            .Where(s => s.Kind == StepKind.ToolCall && s.Name == Knowledge.KnowledgeToolSource.ToolName && s.CreatedAt >= since
                        && s.Result != null && s.Result.Contains("No matching knowledge was found"))
            .OrderByDescending(s => s.CreatedAt).Take(options.Value.MaxRows).Select(s => new { s.RunId, s.ArgumentsJson, s.CreatedAt }).ToListAsync(ct);
        if (misses.Count == 0) return "No knowledge searches without results in this period.";
        var runs = await db.Runs.AsNoTracking().Where(r => misses.Select(m => m.RunId).Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var sb = new StringBuilder($"{misses.Count} searches found nothing:\n");
        foreach (var m in misses)
            sb.AppendLine($"run {m.RunId} | {m.CreatedAt:u}" + (runs.TryGetValue(m.RunId, out var r) && RunAccess.CanRead(r, context.Principal, config)
                ? $" | query {Cut(m.ArgumentsJson ?? "", 150)}" : " | (query hidden)"));
        return sb.ToString().TrimEnd();
    }

    private async Task<string> LogsAsync(JsonElement a, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.LokiUrl)) return "Logs are not configured (Introspection:LokiUrl).";
        var minutes = Int(a, "minutes", 60, 1, 1440);
        var contains = (Str(a, "contains") ?? "").Replace("\"", "").Replace("\\", "");
        var level = Str(a, "level") is { } l && l is "error" or "warn" or "info" ? $" | detected_level=\"{l}\"" : "";
        var query = "{service_name=\"lots-shell\"}" + (contains.Length > 0 ? $" |= \"{contains[..Math.Min(contains.Length, 200)]}\"" : "") + level;
        var end = clock.GetUtcNow();
        var url = $"{options.Value.LokiUrl!.TrimEnd('/')}/loki/api/v1/query_range?query={Uri.EscapeDataString(query)}&limit={options.Value.MaxRows}" +
                  $"&start={end.AddMinutes(-minutes).ToUnixTimeMilliseconds() * 1_000_000}&end={end.ToUnixTimeMilliseconds() * 1_000_000}&direction=backward";
        var body = await http.CreateClient(ServerName).GetFromJsonAsync<JsonElement>(url, ct);
        var lines = body.GetProperty("data").GetProperty("result").EnumerateArray()
            .SelectMany(s => s.GetProperty("values").EnumerateArray().Select(v => (Ns: long.Parse(v[0].GetString()!, CultureInfo.InvariantCulture), Line: v[1].GetString() ?? "",
                Trace: s.GetProperty("stream").TryGetProperty("trace_id", out var t) ? t.GetString() : null)))
            .OrderByDescending(x => x.Ns).Take(options.Value.MaxRows).ToList();
        if (lines.Count == 0) return "No matching log lines.";
        return string.Join("\n", lines.Select(x => $"{DateTimeOffset.FromUnixTimeMilliseconds(x.Ns / 1_000_000):u} {Cut(x.Line, 300)}" + (x.Trace is { } tr ? $" (trace {tr})" : "")));
    }

    /// <summary>Only Lots metrics: every identifier that looks like a metric name must start with lots_.</summary>
    public static bool AllowedPromQl(string query)
    {
        var names = System.Text.RegularExpressions.Regex.Matches(query, @"(?<![\w""{=,])([a-zA-Z_:][a-zA-Z0-9_:]*)\s*(\{|\[|$|\)|\s)")
            .Select(m => m.Groups[1].Value)
            .Where(n => !PromQlWords.Contains(n));
        return query.Length <= 500 && names.All(n => n.StartsWith("lots_", StringComparison.Ordinal));
    }

    private static readonly HashSet<string> PromQlWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "sum", "avg", "min", "max", "count", "rate", "irate", "increase", "histogram_quantile", "by", "without", "on", "ignoring", "group_left",
        "group_right", "and", "or", "unless", "topk", "bottomk", "clamp_min", "clamp_max", "abs", "round", "delta", "deriv", "time", "vector",
        "scalar", "sort", "sort_desc", "label_replace", "quantile", "stddev", "offset", "le", "bool",
    };

    private async Task<string> PromAsync(JsonElement a, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.PrometheusUrl)) return "Metrics are not configured (Introspection:PrometheusUrl).";
        var query = Str(a, "query") ?? "";
        if (!AllowedPromQl(query)) return "Error: only Lots metrics (names starting with lots_) and queries up to 500 characters are allowed.";
        var minutes = Int(a, "minutes", 60, 1, 1440);
        var end = clock.GetUtcNow();
        var step = Math.Max(30, minutes * 60 / 60); // at most 60 points per series
        var url = $"{options.Value.PrometheusUrl!.TrimEnd('/')}/api/v1/query_range?query={Uri.EscapeDataString(query)}" +
                  $"&start={end.AddMinutes(-minutes).ToUnixTimeSeconds()}&end={end.ToUnixTimeSeconds()}&step={step}";
        var body = await http.CreateClient(ServerName).GetFromJsonAsync<JsonElement>(url, ct);
        var series = body.GetProperty("data").GetProperty("result").EnumerateArray().Take(10).ToList();
        if (series.Count == 0) return "No data.";
        var sb = new StringBuilder();
        foreach (var s in series)
        {
            var labels = string.Join(",", s.GetProperty("metric").EnumerateObject().Where(p => p.Name != "__name__").Select(p => $"{p.Name}={p.Value.GetString()}"));
            var values = s.GetProperty("values").EnumerateArray().Select(v => v[1].GetString()).ToList();
            sb.AppendLine($"{{{labels}}}: last {values.LastOrDefault()}, min {values.Min(v => Num(v))}, max {values.Max(v => Num(v))} ({values.Count} points)");
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> ProposeAsync(LotsDbContext db, JsonElement a, ToolCallContext context, IServiceProvider services, CancellationToken ct)
    {
        var runIds = a.TryGetProperty("run_ids", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => Guid.TryParse(x.GetString(), out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Take(50).ToList()
            : [];
        try
        {
            var p = Proposals.ProposalBuilder.Create(Str(a, "profile") ?? "",
                new Proposals.ProposedChanges(Str(a, "instructions"), Str(a, "description"), Str(a, "model")),
                Str(a, "title") ?? "", Str(a, "rationale") ?? "", new Proposals.ProposalEvidence(runIds), context.Principal.UserId,
                services.GetRequiredService<Profiles.ProfileRegistry>(), db, new Proposals.ModelCatalogLike(services.GetService<Models.ModelCatalog>()), clock.GetUtcNow());
            if (string.IsNullOrWhiteSpace(p.Title) || string.IsNullOrWhiteSpace(p.Rationale)) return "Error: a proposal needs a title and a rationale.";
            db.Proposals.Add(p);
            await db.SaveChangesAsync(ct);
            return $"Proposal {p.Id} created for {p.Profile} v{p.BaseVersion} -> v{p.BaseVersion + 1}. A person evaluates and merges it; nothing is live yet.\n{p.Diff}";
        }
        catch (Proposals.ProposalException ex)
        {
            return "Error: " + ex.Message;
        }
    }

    private static double Num(string? v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsNaN(d) ? Math.Round(d, 4) : 0;

    private static string? Str(JsonElement a, string name) =>
        a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    private static int Int(JsonElement a, string name, int fallback, int min, int max) =>
        a.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? Math.Clamp(n, min, max) : fallback;

    private static string Cut(string s, int n) => (s.Length <= n ? s : s[..n] + "…").ReplaceLineEndings(" ");
}
