using FastEndpoints;
using Lots.Shell.Core.Mcp;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Runs;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Runs;

public sealed record StartRunRequest(string Prompt, string? Profile = null, bool Voice = false, Guid? ConversationId = null);

public sealed record StartRunResponse(Guid Id, string Status);

public sealed class StartRunEndpoint(LotsDbContext db, TimeProvider clock, ProfileRegistry profiles, IConfiguration config, ICurrentPrincipal who, SubjectTokenVault vault)
    : Endpoint<StartRunRequest, StartRunResponse>
{
    public override void Configure()
    {
        Post("/runs");
    }

    public override async Task HandleAsync(StartRunRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt))
        {
            AddError(x => x.Prompt, "Prompt is required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var profileName = req.Profile ?? config["Agent:DefaultProfile"] ?? profiles.All.First().Name;
        var profile = profiles.Find(profileName);
        if (profile is null)
        {
            AddError(x => x.Profile!, $"Unknown profile '{profileName}'.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        if (req.ConversationId is { } conversation
            && await db.Runs.AnyAsync(r => r.ConversationId == conversation && r.UserId != who.Get(HttpContext).UserId, ct))
        {
            AddError("That conversation belongs to someone else.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var run = RunFactory.Create(HttpContext, who.Get(HttpContext), clock.GetUtcNow(), profile, config, vault,
            req.Prompt, req.Voice, req.ConversationId);
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        Lots.Shell.Core.Telemetry.LotsMetrics.RunsStarted.Add(1, new("profile", run.Profile), new("voice", run.Voice));
        await Send.ResponseAsync(new StartRunResponse(run.Id, run.Status.ToString()), 202, ct);
    }
}

/// <summary>Creates runs for the calling user (new prompts and retries alike).</summary>
public static class RunFactory
{
    public static RunRecord Create(HttpContext http, Principal me, DateTimeOffset now, Profile profile, IConfiguration config, SubjectTokenVault vault,
        string prompt, bool voice, Guid? conversationId, Guid? retryOf = null)
    {
        // Roles are fixed on the run when it starts: the run acts with the permissions its user had then.
        var run = new RunRecord
        {
            Id = Guid.NewGuid(), Prompt = prompt, Profile = profile.Name, Voice = voice, ConversationId = conversationId, RetryOf = retryOf,
            UserId = me.UserId, Roles = string.Join(',', me.Roles), CreatedAt = now, UpdatedAt = now,
        };
        // Only runs whose profile uses delegated servers keep the user's login token (encrypted), and only until the
        // run ends. It is exchanged per call for a backend-scoped token and never sent to a backend itself.
        if (profile.Servers.Any(s => s.Auth == AuthStrategies.Delegated)
            && AuthSetup.IsOidc(config)
            && http.Request.Headers.Authorization.ToString() is { } auth
            && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            run.SubjectTokenProtected = vault.Protect(auth["Bearer ".Length..].Trim());
            run.SubjectTokenExpiresAt = long.TryParse(http.User.FindFirst("exp")?.Value, out var exp)
                ? DateTimeOffset.FromUnixTimeSeconds(exp)
                : now.AddMinutes(5);
        }
        return run;
    }
}

public sealed record RunSummaryDto(Guid Id, string Prompt, string Profile, string User, string Status, DateTimeOffset CreatedAt);

/// <summary>The caller's runs, newest first. Admins (<c>Auth:AdminRoles</c>) see everyone's.</summary>
public sealed class ListRunsEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config) : EndpointWithoutRequest<List<RunSummaryDto>>
{
    public override void Configure() => Get("/runs");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var admins = (config["Auth:AdminRoles"] ?? "admin").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var isAdmin = me.Roles.Any(r => admins.Contains(r, StringComparer.OrdinalIgnoreCase));

        var q = db.Runs.AsNoTracking().AsQueryable();
        if (!isAdmin) q = q.Where(r => r.UserId == me.UserId);

        var runs = await q.OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync(ct);
        await Send.OkAsync(runs.Select(r => new RunSummaryDto(r.Id, r.Prompt, r.Profile, r.UserId, r.Status.ToString(), r.CreatedAt)).ToList(), ct);
    }
}

/// <summary>Who may read a run: its owner and admins (<c>Auth:AdminRoles</c>, default admin).</summary>
public static class RunAccess
{
    public static bool CanRead(RunRecord run, Principal me, IConfiguration config)
    {
        var admins = (config["Auth:AdminRoles"] ?? "admin").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return run.UserId == me.UserId || me.Roles.Any(r => admins.Contains(r, StringComparer.OrdinalIgnoreCase));
    }
}

public sealed record GetRunRequest(Guid Id);

public sealed record StepDto(
    int Seq, string Kind, string Name, string? ToolCallId, string? Arguments, string? Result,
    long LatencyMs, int? PromptTokens, int? CompletionTokens, DateTimeOffset At, string? Endpoint = null,
    string? Decision = null, string? Reason = null);

/// <param name="Waiting">What an unfinished run is waiting for: queued, model, tool, approval or cancelling; null when finished.</param>
public sealed record RunDto(
    Guid Id, string Prompt, string Status, string? FinalAnswer, string? Error,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<StepDto> Steps,
    string? Waiting = null, Guid? RetryOf = null, string? TraceId = null, double Cost = 0, string? Currency = null);

/// <summary>A run can be read by its owner and by admins (<c>Auth:AdminRoles</c>, default admin). Others get 404.</summary>
public sealed class GetRunEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config, Lots.Shell.Features.Usage.PriceTable prices) : Endpoint<GetRunRequest, RunDto>
{
    public override void Configure()
    {
        Get("/runs/{Id}");
    }

    public override async Task HandleAsync(GetRunRequest req, CancellationToken ct)
    {
        var run = await db.Runs.AsNoTracking().Include(r => r.Steps).Include(r => r.Messages).SingleOrDefaultAsync(r => r.Id == req.Id, ct);
        var me = who.Get(HttpContext);
        if (run is null || !RunAccess.CanRead(run, me, config))
        {
            await Send.NotFoundAsync(ct); // do not reveal that someone else's run exists
            return;
        }

        // Each tool step with the policy decision that let it run or stopped it ("denied: no role grants Write").
        var audit = await db.AuditLog.AsNoTracking().Where(a => a.RunId == run.Id && a.Decision != AuditDecision.ApprovalRequested).ToListAsync(ct);
        await Send.OkAsync(new RunDto(
            run.Id, run.Prompt, run.Status.ToString(), run.FinalAnswer, run.Error, run.CreatedAt, run.UpdatedAt,
            run.Steps.OrderBy(s => s.Seq).Select(s =>
            {
                var decision = s.Kind == StepKind.ToolCall ? Lots.Shell.Features.ToolCalls.ListToolCallsEndpoint.Match(audit, s) : null;
                return new StepDto(s.Seq, s.Kind.ToString(), s.Name, s.ToolCallId, s.ArgumentsJson, s.Result,
                    s.LatencyMs, s.PromptTokens, s.CompletionTokens, s.CreatedAt, s.Endpoint, decision?.Decision.ToString(), decision?.Reason);
            }).ToList(),
            WaitingFor(run), run.RetryOf, run.TraceId,
            Math.Round(run.Steps.Where(s => s.Kind == StepKind.ModelCall).Sum(s => prices.Cost(s.Name, s.PromptTokens ?? 0, s.CompletionTokens ?? 0)), 4),
            prices.Currency), ct);
    }

    internal static string? WaitingFor(RunRecord run)
    {
        if (run.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled) return null;
        if (run.CancelRequestedAt is not null) return "cancelling";
        if (run.Status == RunStatus.WaitingForApproval) return "approval";
        if (run.Status == RunStatus.Pending) return "queued";
        var last = run.Messages.OrderBy(m => m.Seq).LastOrDefault();
        return last is { Role: "assistant", ToolCallsJson: not null } ? "tool" : "model";
    }
}

public sealed record RunActionRequest(Guid Id);

/// <summary>
/// Stops a run: its owner or an admin. 200 + Cancelled when no worker held it; 202 + Running when a worker does (it stops
/// within about a second, <c>waiting</c> = cancelling until then); 409 when the run already finished.
/// </summary>
public sealed class CancelRunEndpoint(LotsDbContext db, RunControl control, ICurrentPrincipal who, IConfiguration config)
    : Endpoint<RunActionRequest, StartRunResponse>
{
    public override void Configure()
    {
        Post("/runs/{Id}/cancel");
    }

    public override async Task HandleAsync(RunActionRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == req.Id, ct);
        if (run is null || !RunAccess.CanRead(run, me, config))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        switch (await control.RequestCancelAsync(run.Id, me.UserId, ct))
        {
            case CancelOutcome.Cancelled:
                await Send.OkAsync(new StartRunResponse(run.Id, nameof(RunStatus.Cancelled)), ct);
                break;
            case CancelOutcome.Requested:
                await Send.ResponseAsync(new StartRunResponse(run.Id, nameof(RunStatus.Running)), 202, ct);
                break;
            case CancelOutcome.NotFound:
                await Send.NotFoundAsync(ct);
                break;
            default:
                AddError($"The run already finished ({run.Status}).");
                await Send.ErrorsAsync(409, ct);
                break;
        }
    }
}

/// <summary>
/// Starts a failed or cancelled run again as a new run (same prompt, profile, voice mode and conversation). Only the owner may:
/// the new run acts with the caller's current roles, never with someone else's.
/// </summary>
public sealed class RetryRunEndpoint(LotsDbContext db, TimeProvider clock, ProfileRegistry profiles, IConfiguration config, ICurrentPrincipal who, SubjectTokenVault vault)
    : Endpoint<RunActionRequest, StartRunResponse>
{
    public override void Configure()
    {
        Post("/runs/{Id}/retry");
    }

    public override async Task HandleAsync(RunActionRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var old = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == req.Id, ct);
        if (old is null || !RunAccess.CanRead(old, me, config))
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (old.UserId != me.UserId)
        {
            AddError("Only the user who started a run can retry it.");
            await Send.ErrorsAsync(403, ct);
            return;
        }
        if (old.Status is not (RunStatus.Failed or RunStatus.Cancelled))
        {
            AddError($"Only failed or cancelled runs can be retried; this one is {old.Status}.");
            await Send.ErrorsAsync(409, ct);
            return;
        }
        if (profiles.Find(old.Profile) is not { } profile)
        {
            AddError($"The run's profile '{old.Profile}' no longer exists.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var run = RunFactory.Create(HttpContext, me, clock.GetUtcNow(), profile, config, vault, old.Prompt, old.Voice, old.ConversationId, retryOf: old.Id);
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        await Send.ResponseAsync(new StartRunResponse(run.Id, run.Status.ToString()), 202, ct);
    }
}
