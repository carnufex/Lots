using FastEndpoints;
using Lots.Shell.Core.Mcp;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Runs;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Runs;

/// <param name="Model">A configured model alias to answer with instead of the profile's (comparisons, #119). Roles in <c>Models:ChooseRoles</c> only (default admin, evaluator).</param>
/// <param name="ReasoningEffort">none, minimal, low, medium or high; same roles as <paramref name="Model"/>.</param>
/// <param name="Routing">"chosen" when the user picked the context after the router asked (#150); recorded on the run.</param>
/// <param name="Contexts">Ask these contexts together (#151): 2-3 contexts where the user may only read; one sub-run each, one combined answer.</param>
public sealed record StartRunRequest(string Prompt, string? Profile = null, bool Voice = false, Guid? ConversationId = null,
    List<Guid>? Attachments = null, string? Model = null, string? ReasoningEffort = null, string? Routing = null, List<string>? Contexts = null);

/// <param name="Profile">The context to answer in; null or "auto" lets the router choose (#150). Context is the UI name of a profile.</param>
public sealed record StartRunResponse(Guid Id, string Status, string? Profile = null, string? Routing = null);

/// <summary>Returned with 409 when the router cannot choose with confidence: the user picks one of these and sends again with it.</summary>
public sealed record RouteChoiceDto(string Reason, IReadOnlyList<Core.Routing.RouteCandidate> Candidates);

public sealed class StartRunEndpoint(LotsDbContext db, TimeProvider clock, ProfileRegistry profiles, IConfiguration config, ICurrentPrincipal who, SubjectTokenVault vault,
    Lots.Shell.Core.Quotas.QuotaService quotas)
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

        Core.Routing.RouteDecision? route = null;
        string? routing = "manual";
        List<string>? supervise = null;
        if (req.Contexts is { Count: > 0 } asked)
        {
            // Fan-out is for reading only (#151): every context must be one the user can use, and only read in.
            var me = who.Get(HttpContext);
            var usable = Core.Routing.ContextRouter.Usable(me, profiles);
            var max = HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<Core.Routing.RoutingOptions>>().Value.MaxContexts;
            var picked = asked.Select(c => usable.FirstOrDefault(p => string.Equals(p.Name, c, StringComparison.OrdinalIgnoreCase))).ToList();
            if (asked.Count < 2 || asked.Count > max || picked.Any(p => p is null) || picked.Any(p => !Core.Routing.ContextRouter.ReadOnlyFor(me, p!)))
            {
                AddError(x => x.Contexts!, $"Give 2-{max} contexts you can use and can only read in.");
                await Send.ErrorsAsync(cancellation: ct);
                return;
            }
            supervise = picked.Select(p => p!.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            route = new Core.Routing.RouteDecision(supervise[0], "multi", [], 0, "chosen by the user", "none", 0, supervise);
            routing = req.Routing is "chosen" ? "chosen" : "manual";
        }
        else if (string.IsNullOrWhiteSpace(req.Profile) || req.Profile == "auto")
        {
            var router = HttpContext.RequestServices.GetRequiredService<Core.Routing.ContextRouter>();
            var current = req.ConversationId is { } conv
                ? await db.Runs.AsNoTracking().Where(r => r.ConversationId == conv).OrderByDescending(r => r.CreatedAt).Select(r => r.Profile).FirstOrDefaultAsync(ct)
                : null;
            route = await router.RouteAsync(who.Get(HttpContext), req.Prompt, current, ct);
            if (route.Mode == "ask")
            {
                // Never a silent guess: the user picks, with one click, and sends again with that context.
                await Send.ResultAsync(TypedResults.Json(new RouteChoiceDto(route.Reason, route.Candidates), statusCode: 409));
                return;
            }
            routing = route.Mode == "none" ? "default" : route.Mode;
            if (route.Mode == "multi") supervise = route.Contexts?.ToList();
        }
        var profileName = route?.Profile ?? (string.IsNullOrWhiteSpace(req.Profile) || req.Profile == "auto" ? null : req.Profile)
            ?? config["Agent:DefaultProfile"] ?? profiles.All.First().Name;
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

        if (ModelChoice.Check(req.Model, req.ReasoningEffort, who.Get(HttpContext), config,
                HttpContext.RequestServices.GetService<Lots.Shell.Core.Models.ModelCatalog>()) is { } choiceError)
        {
            AddError(choiceError.Message);
            await Send.ErrorsAsync(choiceError.Status, ct);
            return;
        }

        try { await quotas.CheckStartAsync(who.Get(HttpContext), profile.Name, ct); }
        catch (Lots.Shell.Core.Quotas.QuotaExceededException ex)
        {
            AddError(ex.Message);
            await Send.ErrorsAsync(429, ct);
            return;
        }

        var (attachments, attachmentError) = await Features.Attachments.RunAttachments.ResolveAsync(db, who.Get(HttpContext).UserId, req.Attachments,
            HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<Core.Attachments.AttachmentOptions>>().Value, ct);
        if (attachmentError is not null)
        {
            AddError(x => x.Attachments!, attachmentError);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var run = RunFactory.Create(HttpContext, who.Get(HttpContext), clock.GetUtcNow(), profile, config, vault,
            req.Prompt, req.Voice, req.ConversationId);
        run.AttachmentsJson = attachments;
        run.ModelAlias = string.IsNullOrWhiteSpace(req.Model) ? null : req.Model.Trim();
        run.ReasoningEffort = string.IsNullOrWhiteSpace(req.ReasoningEffort) ? null : req.ReasoningEffort.Trim().ToLowerInvariant();
        run.RoutingMode = req.Routing is "chosen" && routing == "manual" ? "chosen" : routing;
        run.SuperviseJson = supervise is { Count: >= 2 } ? System.Text.Json.JsonSerializer.Serialize(supervise) : null;
        run.RoutingJson = route is null || route.Mode == "only" ? null : System.Text.Json.JsonSerializer.Serialize(
            new { route.Method, route.Margin, route.LatencyMs, candidates = route.Candidates.Select(c => new { c.Profile, c.Score }) });
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        Lots.Shell.Core.Telemetry.LotsMetrics.RunsStarted.Add(1, new("profile", run.Profile), new("voice", run.Voice));
        await Send.ResponseAsync(new StartRunResponse(run.Id, run.Status.ToString(), run.Profile, run.RoutingMode), 202, ct);
    }
}

public sealed record RouteRequest(string Prompt, Guid? ConversationId = null);

/// <summary>
/// The router's decision without starting anything (#150): for the routing evals and for clients that want to show where a question
/// would go. Same candidates (policy first), scores and thresholds as a real run.
/// </summary>
public sealed class RouteEndpoint(LotsDbContext db, ICurrentPrincipal who, Core.Routing.ContextRouter router) : Endpoint<RouteRequest, Core.Routing.RouteDecision>
{
    public override void Configure() => Post("/route");

    public override async Task HandleAsync(RouteRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt))
        {
            AddError(x => x.Prompt, "Prompt is required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        var me = who.Get(HttpContext);
        var current = req.ConversationId is { } conv
            ? await db.Runs.AsNoTracking().Where(r => r.ConversationId == conv && r.UserId == me.UserId).OrderByDescending(r => r.CreatedAt)
                .Select(r => r.Profile).FirstOrDefaultAsync(ct)
            : null;
        await Send.OkAsync(await router.RouteAsync(me, req.Prompt, current, ct), ct);
    }
}

/// <summary>
/// Who may pick the model for a run (#119): roles in <c>Models:ChooseRoles</c> (default admin, evaluator). Only configured aliases can be picked,
/// never a raw endpoint or model name, and data-class clearance still routes the call, so a choice cannot send data where it may not go.
/// </summary>
public static class ModelChoice
{
    public static readonly string[] Efforts = ["none", "minimal", "low", "medium", "high"];

    /// <summary>admin, and evaluator: an identity for model comparisons that profiles give no tools.</summary>
    public static readonly string[] DefaultRoles = ["admin", "evaluator"];

    public sealed record Problem(int Status, string Message);

    public static Problem? Check(string? model, string? effort, Principal me, IConfiguration config, Lots.Shell.Core.Models.ModelCatalog? catalog)
    {
        var wantsModel = !string.IsNullOrWhiteSpace(model);
        var wantsEffort = !string.IsNullOrWhiteSpace(effort);
        if (!wantsModel && !wantsEffort) return null;
        var roles = config.GetSection("Models:ChooseRoles").Get<string[]>() is { Length: > 0 } r ? r : DefaultRoles;
        if (!me.Roles.Any(x => roles.Contains(x, StringComparer.OrdinalIgnoreCase)))
            return new(403, $"Choosing the model or reasoning effort needs one of the roles {string.Join(", ", roles)}.");
        if (wantsModel && (catalog is null || !catalog.Aliases.ContainsKey(model!.Trim())))
            return new(400, $"Unknown model alias '{model}'" + (catalog is null ? "." : $"; configured: {string.Join(", ", catalog.Aliases.Keys.Order())}."));
        if (wantsEffort && !Efforts.Contains(effort!.Trim().ToLowerInvariant()))
            return new(400, $"Unknown reasoning effort '{effort}'; use one of {string.Join(", ", Efforts)}.");
        return null;
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
            PreviewRealRoles = me.Preview is { } p ? string.Join(',', p.RealRoles) : null, PreviewAllowWrites = me.Preview?.AllowWrites ?? false,
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

        var q = db.Runs.AsNoTracking().Where(r => r.ParentRunId == null); // sub-runs are listed on their parent (#103)
        if (!isAdmin) q = q.Where(r => r.UserId == me.UserId);

        var runs = await q.OrderByDescending(r => r.CreatedAt).Take(100).ToListAsync(ct);
        if (!isAdmin)
        {
            // Scheduled runs shared with the caller's roles (#101).
            var shared = await db.Runs.AsNoTracking().Where(r => r.Viewers != null && r.UserId != me.UserId)
                .OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync(ct);
            runs = runs.Concat(shared.Where(r => RunAccess.IsViewer(r, me))).OrderByDescending(r => r.CreatedAt).Take(100).ToList();
        }
        await Send.OkAsync(runs.Select(r => new RunSummaryDto(r.Id, r.Prompt, r.Profile, r.UserId, r.Status.ToString(), r.CreatedAt)).ToList(), ct);
    }
}

/// <summary>Who may read a run: its owner and admins (<c>Auth:AdminRoles</c>, default admin).</summary>
public static class RunAccess
{
    public static bool CanRead(RunRecord run, Principal me, IConfiguration config)
    {
        var admins = (config["Auth:AdminRoles"] ?? "admin").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return run.UserId == me.UserId || me.Roles.Any(r => admins.Contains(r, StringComparer.OrdinalIgnoreCase)) || IsViewer(run, me);
    }

    /// <summary>A scheduled run names who may read it besides the owner and admins (#101).</summary>
    public static bool IsViewer(RunRecord run, Principal me) =>
        run.Viewers is { Length: > 0 } v && Core.Knowledge.KnowledgeAccess.TokensOf(me).Any(t => v.Contains("," + t + ",", StringComparison.OrdinalIgnoreCase));
}

public sealed record GetRunRequest(Guid Id);

public sealed record StepDto(
    int Seq, string Kind, string Name, string? ToolCallId, string? Arguments, string? Result,
    long LatencyMs, int? PromptTokens, int? CompletionTokens, DateTimeOffset At, string? Endpoint = null,
    string? Decision = null, string? Reason = null, bool Flagged = false);

/// <param name="Waiting">What an unfinished run is waiting for: queued, model, tool, approval or cancelling; null when finished.</param>
public sealed record RunDto(
    Guid Id, string Prompt, string Status, string? FinalAnswer, string? Error,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<StepDto> Steps,
    string? Waiting = null, Guid? RetryOf = null, string? TraceId = null, double Cost = 0, string? Currency = null,
    string Sensitivity = "public", Guid? ParentRunId = null, IReadOnlyList<Guid>? SubRuns = null, string? ModelAlias = null, string? ReasoningEffort = null,
    Guid? ConversationId = null);

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
                // Model steps carry their routing decision (#89) where tool steps carry their policy decision.
                return new StepDto(s.Seq, s.Kind.ToString(), s.Name, s.ToolCallId, s.ArgumentsJson, s.Result,
                    s.LatencyMs, s.PromptTokens, s.CompletionTokens, s.CreatedAt, s.Endpoint,
                    decision?.Decision.ToString() ?? (s.Routing is null ? null : "Rerouted"), decision?.Reason ?? s.Routing, s.Flagged);
            }).ToList(),
            WaitingFor(run), run.RetryOf, run.TraceId,
            Math.Round(run.Steps.Where(s => s.Kind == StepKind.ModelCall).Sum(s => prices.Cost(s.Name, s.PromptTokens ?? 0, s.CompletionTokens ?? 0)), 4),
            prices.Currency, run.Sensitivity.ToString().ToLowerInvariant(), run.ParentRunId,
            await db.Runs.AsNoTracking().Where(r => r.ParentRunId == run.Id).OrderBy(r => r.CreatedAt).Select(r => r.Id).ToListAsync(ct),
            run.ModelAlias, run.ReasoningEffort, run.ConversationId), ct);
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
        run.ModelAlias = old.ModelAlias;
        run.ReasoningEffort = old.ReasoningEffort;
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        await Send.ResponseAsync(new StartRunResponse(run.Id, run.Status.ToString()), 202, ct);
    }
}
