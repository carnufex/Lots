using System.ComponentModel;
using System.Text.Json;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using ModelContextProtocol.Server;

namespace Lots.Shell.Features.Introspection;

/// <summary>
/// The introspection tools for agents outside the shell (#142), e.g. Claude Code: an MCP endpoint at <c>/mcp/introspect</c>. The caller
/// signs in like any client (OIDC token or API token), and every call goes through the same choke point as inside a run: the
/// <c>self-improve</c> profile (<c>Introspection:Profile</c>) must declare the tool and a role of the caller must grant it, the result
/// is wrapped as untrusted data, and the decision is an audit row (run id empty: there is no run).
/// </summary>
public sealed class IntrospectionGateway(ToolInvoker tools, ICurrentPrincipal who, IHttpContextAccessor http, LotsDbContext db, ProfileRegistry profiles,
    IConfiguration config, TimeProvider clock, ILogger<IntrospectionGateway> logger)
{
    public string Profile => config["Introspection:Profile"] ?? "self-improve";

    public async Task<string> CallAsync(string tool, object arguments, CancellationToken ct)
    {
        var principal = who.Get(http.HttpContext ?? throw new InvalidOperationException("No HTTP request."));
        var call = new ToolCall("mcp-" + Guid.NewGuid().ToString("N")[..12], tool, JsonSerializer.Serialize(arguments, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        if (profiles.Find(Profile) is not { } profile)
            return $"Error: the introspection profile '{Profile}' is not loaded on this shell.";
        var policy = tools.Evaluate(call, principal, Profile);
        var allowed = policy.Decision == Decision.Allow;
        var result = allowed ? await tools.InvokeDetailedAsync(call, principal, Profile, ct) : null;
        db.AuditLog.Add(new AuditRecord
        {
            Id = Guid.NewGuid(), At = clock.GetUtcNow(), UserId = principal.UserId, Roles = string.Join(',', principal.Roles), Profile = Profile,
            ProfileVersion = profile.Version, RunId = Guid.Empty, Tool = tool, ToolCallId = call.Id, ArgumentsJson = call.ArgumentsJson,
            Decision = allowed ? AuditDecision.Allowed : AuditDecision.Denied, Reason = policy.Reason + " (via /mcp/introspect)",
            ResultStatus = result is null ? null : result.Text.StartsWith("Error:", StringComparison.Ordinal) ? "error" : "ok",
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Introspection tool {Tool} {Decision} over MCP: {Reason}", tool, allowed ? "Allowed" : "Denied", policy.Reason);
        return result?.ModelText ?? $"Error: tool '{tool}' is not available or not permitted.";
    }
}

[McpServerToolType]
public sealed class IntrospectionMcpTools(IntrospectionGateway gateway)
{
    [McpServerTool(Name = "outcomes_summary", ReadOnly = true), Description("Success rate, p50/p95 time, cost and the most common problems per profile version and channel.")]
    public Task<string> OutcomesSummary(int? days = null, string? profile = null, string? channel = null, CancellationToken ct = default) =>
        gateway.CallAsync("outcomes_summary", new { days, profile, channel }, ct);

    [McpServerTool(Name = "list_failed_runs", ReadOnly = true), Description("Runs that went wrong, newest first, with run ids and their main problem.")]
    public Task<string> ListFailedRuns(int? days = null, string? profile = null, string? channel = null, string? problem = null, int? limit = null, CancellationToken ct = default) =>
        gateway.CallAsync("list_failed_runs", new { days, profile, channel, problem, limit }, ct);

    [McpServerTool(Name = "get_run_trace", ReadOnly = true), Description("The steps of one run with time, tokens and policy decisions; content only for runs you may read.")]
    public Task<string> GetRunTrace(string run_id, CancellationToken ct = default) => gateway.CallAsync("get_run_trace", new { run_id }, ct);

    [McpServerTool(Name = "get_run_audit", ReadOnly = true), Description("The audit rows of one run.")]
    public Task<string> GetRunAudit(string run_id, CancellationToken ct = default) => gateway.CallAsync("get_run_audit", new { run_id }, ct);

    [McpServerTool(Name = "tool_error_breakdown", ReadOnly = true), Description("Per tool: calls, errors, error rate and kinds of errors.")]
    public Task<string> ToolErrorBreakdown(int? days = null, string? profile = null, string? channel = null, CancellationToken ct = default) =>
        gateway.CallAsync("tool_error_breakdown", new { days, profile, channel }, ct);

    [McpServerTool(Name = "slow_stages", ReadOnly = true), Description("Where time goes per profile version and channel, and the slowest runs.")]
    public Task<string> SlowStages(int? days = null, string? profile = null, string? channel = null, CancellationToken ct = default) =>
        gateway.CallAsync("slow_stages", new { days, profile, channel }, ct);

    [McpServerTool(Name = "compare_profile_versions", ReadOnly = true), Description("Outcomes of each version of one profile side by side.")]
    public Task<string> CompareProfileVersions(string profile, int? days = null, CancellationToken ct = default) =>
        gateway.CallAsync("compare_profile_versions", new { profile, days }, ct);

    [McpServerTool(Name = "retrieval_misses", ReadOnly = true), Description("Knowledge searches that found nothing, with their runs.")]
    public Task<string> RetrievalMisses(int? days = null, CancellationToken ct = default) => gateway.CallAsync("retrieval_misses", new { days }, ct);

    [McpServerTool(Name = "search_logs", ReadOnly = true), Description("Shell log lines from Loki containing a text, newest first.")]
    public Task<string> SearchLogs(string? contains = null, string? level = null, int? minutes = null, CancellationToken ct = default) =>
        gateway.CallAsync("search_logs", new { contains, level, minutes }, ct);

    [McpServerTool(Name = "promql_query", ReadOnly = true), Description("A PromQL range query over Lots metrics (names starting with lots_).")]
    public Task<string> PromqlQuery(string query, int? minutes = null, CancellationToken ct = default) => gateway.CallAsync("promql_query", new { query, minutes }, ct);
}
