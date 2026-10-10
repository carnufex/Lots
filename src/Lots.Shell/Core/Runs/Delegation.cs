using System.Text.Json;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Core.Runs;

/// <summary>The run a tool call is being made for, for built-in tools that act on the run itself (sub-agents, #103).</summary>
public sealed record RunScope(Guid RunId, int Depth, DataClass Sensitivity, bool Tainted, string Profile)
{
    private static readonly AsyncLocal<RunScope?> Holder = new();
    public static RunScope? Current => Holder.Value;

    public static IDisposable Enter(RunScope scope)
    {
        var previous = Holder.Value;
        Holder.Value = scope;
        return new Restore(previous);
    }

    private sealed class Restore(RunScope? previous) : IDisposable
    {
        public void Dispose() => Holder.Value = previous;
    }
}

public sealed class DelegationOptions
{
    public const string Section = "Agent:Delegation";
    /// <summary>How deep sub-agents may nest: 1 = a run may delegate, its sub-run may not.</summary>
    public int MaxDepth { get; set; } = 1;
    public int MaxSteps { get; set; } = 6;
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// <c>delegate</c> (#103): hands a task to a specialised profile the run's profile lists under <c>delegates</c>. The sub-run is the
/// same user with the same roles (identity follows the user), inherits the data class and taint, may only use the target profile's
/// tools that need no approval (a sub-agent never asks a person for anything), and is limited in depth, steps and time. Its trace
/// and audit rows hang under the parent run.
/// </summary>
public sealed class DelegateToolSource(IServiceScopeFactory scopes, ProfileRegistry profiles, Microsoft.Extensions.Options.IOptions<DelegationOptions> options,
    TimeProvider clock) : IToolSource
{
    public const string ToolName = "delegate";
    public const string ServerName = "delegation";

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "profile": { "type": "string", "description": "The specialised assistant to ask (one of the profiles this assistant may delegate to)." },
            "task": { "type": "string", "description": "What it should find out or do, self-contained: it does not see this conversation." }
          },
          "required": ["profile", "task"]
        }
        """).RootElement.Clone();

    public Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ToolDescriptor>>(
        [new ToolDescriptor(ToolName,
            "Asks a specialised assistant (another profile, e.g. the CMDB) a self-contained question and returns its answer. It works as " +
            "the same user, with its own read-only tools. Use it when the question needs data only that assistant has.", Schema)]);

    public Task<string?> ServerOfAsync(string toolName, CancellationToken ct) => Task.FromResult<string?>(toolName == ToolName ? ServerName : null);

    public async Task<string> CallAsync(string name, string argumentsJson, CancellationToken ct)
    {
        var call = ToolCallContext.Current ?? throw new InvalidOperationException("delegate needs the caller's identity.");
        var parent = RunScope.Current ?? throw new InvalidOperationException("delegate can only be used inside a run.");
        using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var targetName = args.RootElement.TryGetProperty("profile", out var p) ? p.GetString() ?? "" : "";
        var task = args.RootElement.TryGetProperty("task", out var t) ? t.GetString()?.Trim() ?? "" : "";
        var o = options.Value;

        var source = profiles.Find(parent.Profile);
        if (source is null || !source.Delegates.Contains(targetName, StringComparer.OrdinalIgnoreCase))
            return $"Error: this assistant may not delegate to '{targetName}'. It may delegate to: {string.Join(", ", source?.Delegates ?? [])}.";
        if (profiles.Find(targetName) is not { } target) return $"Error: the profile '{targetName}' does not exist.";
        if (!target.Roles.Any(r => call.Principal.Roles.Contains(r.Name, StringComparer.OrdinalIgnoreCase)))
            return $"Error: the user has no role in '{targetName}'.";
        if (parent.Depth >= o.MaxDepth) return "Error: a sub-agent cannot delegate further.";
        if (task.Length == 0) return "Error: task is required.";

        var now = clock.GetUtcNow();
        var child = new RunRecord
        {
            Id = Guid.NewGuid(), Prompt = task, Profile = target.Name, UserId = call.Principal.UserId, Roles = string.Join(',', call.Principal.Roles),
            CreatedAt = now, UpdatedAt = now, ParentRunId = parent.RunId, Depth = parent.Depth + 1, StepLimit = o.MaxSteps,
            Sensitivity = parent.Sensitivity, Tainted = parent.Tainted, Trigger = "delegate:" + parent.Profile,
            // Claimed by this call, not by a worker: the lease keeps the run workers away while it executes here.
            LeaseOwner = "delegate:" + parent.RunId, LeaseUntilMs = now.AddSeconds(o.TimeoutSeconds + 30).ToUnixTimeMilliseconds(),
        };
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        // Same user, same login: delegated backends (ADR 0011) see the person in the sub-run too. The sub-run drops it when done.
        var token = await db.Runs.AsNoTracking().Where(r => r.Id == parent.RunId)
            .Select(r => new { r.SubjectTokenProtected, r.SubjectTokenExpiresAt }).SingleOrDefaultAsync(ct);
        child.SubjectTokenProtected = token?.SubjectTokenProtected;
        child.SubjectTokenExpiresAt = token?.SubjectTokenExpiresAt;
        db.Runs.Add(child);
        await db.SaveChangesAsync(ct);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
        try { await scope.ServiceProvider.GetRequiredService<AgentRunner>().ExecuteAsync(child.Id, limit.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

        // The runner worked on the tracked entity of this scope, so `child` holds the outcome.
        if (child.Status != RunStatus.Completed)
        {
            if (child.Status is not (RunStatus.Failed or RunStatus.Cancelled))
            {
                child.Status = RunStatus.Failed;
                child.Error ??= "Stopped: the sub-agent ran out of time.";
                await db.SaveChangesAsync(CancellationToken.None);
            }
            return $"The {target.Name} assistant could not answer ({child.Status}: {child.Error}). Sub-run {child.Id}.";
        }
        return $"Answer from the {target.Name} assistant (sub-run {child.Id}):\n{child.FinalAnswer}";
    }
}
