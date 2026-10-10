using System.Text.Json;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;

namespace Lots.Shell.Core.Tools;

public enum ToolRisk { Read, Write, Destructive }

public sealed record ToolDescriptor(string Name, string Description, JsonElement Parameters);

/// <summary>
/// Health of one tool server as last seen. Health: ok, unavailable, or per-user (delegated servers are reached with each
/// user's own token, so there is no shared view of them).
/// </summary>
public sealed record ServerStatus(
    string Name, string Url, string Auth, string Health, IReadOnlyList<ToolDescriptor> Tools, string? Error, DateTimeOffset? CheckedAt);

/// <summary>A provider of tools (an MCP server, in-process tools, ...).</summary>
public interface IToolSource
{
    Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct);
    Task<string> CallAsync(string name, string argumentsJson, CancellationToken ct);

    /// <summary>Name of the server (as in the profile) that provides the tool, if the source knows it.</summary>
    Task<string?> ServerOfAsync(string toolName, CancellationToken ct) => Task.FromResult<string?>(null);

    /// <summary>Health and discovered tools per server, for operators. Sources without servers return nothing.</summary>
    Task<IReadOnlyList<ServerStatus>> StatusAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ServerStatus>>([]);
}

/// <summary>
/// The single choke point for every tool call. Policy is evaluated here, per call, outside the model:
/// a tool is visible and callable only if the profile declares it and one of the principal's roles grants its
/// risk class. Calls that need approval are executed only when the caller passes <c>approved: true</c>, which the
/// agent runner does after a recorded approval.
/// </summary>
public sealed class ToolInvoker(IEnumerable<IToolSource> sources, ProfileRegistry profiles)
{
    public const int MaxOutputChars = 8_000;

    private readonly IReadOnlyList<IToolSource> _sources = sources.ToList();

    /// <summary>The backend auth strategy (ADR 0004) the profile declares for the server providing this tool.</summary>
    public async Task<string?> AuthStrategyAsync(string toolName, string profileName, CancellationToken ct)
    {
        var profile = RequireProfile(profileName);
        foreach (var source in _sources)
            if (await source.ServerOfAsync(toolName, ct) is { } server)
                return profile.Servers.FirstOrDefault(s => s.Name == server)?.Auth;
        return null;
    }

    /// <summary>Health and discovered tools of every server behind this invoker.</summary>
    public async Task<IReadOnlyList<ServerStatus>> ServerStatusAsync(CancellationToken ct)
    {
        var all = new List<ServerStatus>();
        foreach (var source in _sources) all.AddRange(await source.StatusAsync(ct));
        return all;
    }

    /// <summary>The policy decision for a call, without executing it.</summary>
    public PolicyResult Evaluate(ToolCall call, Principal principal, string profileName) =>
        PolicyEngine.Decide(principal, RequireProfile(profileName), call.Name);

    /// <summary>Tools the model may see for this principal and profile. Everything else is not shown at all.</summary>
    public async Task<IReadOnlyList<ToolDefinition>> DefinitionsAsync(Principal principal, string profileName, CancellationToken ct)
    {
        var profile = RequireProfile(profileName);
        var visible = PolicyEngine.VisibleTools(principal, profile).ToHashSet();
        var result = new List<ToolDefinition>();
        foreach (var source in _sources)
            foreach (var tool in await source.ListAsync(ct))
                if (visible.Contains(tool.Name) && result.All(r => r.Name != tool.Name))
                    result.Add(new ToolDefinition(tool.Name, tool.Description, tool.Parameters));
        return result;
    }

    /// <summary>
    /// Executes a tool call if policy allows it. Never throws for denied or failing tools: the model gets an
    /// error string as the tool result. The result is untrusted data and is size-capped.
    /// </summary>
    public async Task<string> InvokeAsync(ToolCall call, Principal principal, string profileName, CancellationToken ct, bool approved = false)
    {
        var decision = PolicyEngine.Decide(principal, RequireProfile(profileName), call.Name);
        if (decision.Decision == Decision.Deny)
            return $"Error: tool '{call.Name}' is not available or not permitted.";
        if (decision.Decision == Decision.RequireApproval && !approved)
            return $"Error: tool '{call.Name}' requires approval.";

        IToolSource? source = null;
        foreach (var s in _sources)
            if ((await s.ListAsync(ct)).Any(t => t.Name == call.Name)) { source = s; break; }
        if (source is null)
            return $"Error: tool '{call.Name}' is not available or not permitted.";

        try
        {
            return Truncate(await source.CallAsync(call.Name, call.ArgumentsJson, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: tool '{call.Name}' failed: {ex.Message}";
        }
    }

    private Profile RequireProfile(string name) =>
        profiles.Find(name) ?? throw new InvalidOperationException($"Unknown profile '{name}'.");

    private static string Truncate(string s) =>
        s.Length <= MaxOutputChars ? s : s[..MaxOutputChars] + $"\n[truncated: output exceeded {MaxOutputChars} characters]";
}
