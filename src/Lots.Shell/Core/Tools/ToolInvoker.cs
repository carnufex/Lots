using System.Text.Json;
using Lots.Shell.Core.Models;

namespace Lots.Shell.Core.Tools;

public enum ToolRisk { Read, Write, Destructive }

public sealed record ToolDescriptor(string Name, string Description, JsonElement Parameters, ToolRisk Risk);

/// <summary>A provider of tools (an MCP server, in-process tools, ...).</summary>
public interface IToolSource
{
    Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct);
    Task<string> CallAsync(string name, string argumentsJson, CancellationToken ct);
}

/// <summary>
/// The single choke point for every tool call. Policy is evaluated here, per call, outside the model.
/// M1 policy: only read-only tools are visible and callable.
/// </summary>
public sealed class ToolInvoker(IEnumerable<IToolSource> sources)
{
    public const int MaxOutputChars = 16_000;

    private readonly IReadOnlyList<IToolSource> _sources = sources.ToList();

    /// <summary>Tools the model may see. Tools that are not allowed are not shown at all.</summary>
    public async Task<IReadOnlyList<(IToolSource Source, ToolDescriptor Tool)>> ListAllowedAsync(CancellationToken ct)
    {
        var result = new List<(IToolSource, ToolDescriptor)>();
        foreach (var source in _sources)
            foreach (var tool in await source.ListAsync(ct))
                if (IsAllowed(tool))
                    result.Add((source, tool));
        return result;
    }

    public async Task<IReadOnlyList<ToolDefinition>> DefinitionsAsync(CancellationToken ct) =>
        (await ListAllowedAsync(ct)).Select(x => new ToolDefinition(x.Tool.Name, x.Tool.Description, x.Tool.Parameters)).ToList();

    /// <summary>
    /// Executes a tool call. Never throws for denied or failing tools: the model gets an error string
    /// as the tool result. The result is untrusted data and is size-capped.
    /// </summary>
    public async Task<string> InvokeAsync(ToolCall call, CancellationToken ct)
    {
        var match = (await ListAllowedAsync(ct)).FirstOrDefault(x => x.Tool.Name == call.Name);
        if (match.Source is null)
            return $"Error: tool '{call.Name}' is not available or not permitted.";

        try
        {
            return Truncate(await match.Source.CallAsync(call.Name, call.ArgumentsJson, ct));
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

    private static bool IsAllowed(ToolDescriptor tool) => tool.Risk == ToolRisk.Read;

    private static string Truncate(string s) =>
        s.Length <= MaxOutputChars ? s : s[..MaxOutputChars] + $"\n[truncated: output exceeded {MaxOutputChars} characters]";
}
