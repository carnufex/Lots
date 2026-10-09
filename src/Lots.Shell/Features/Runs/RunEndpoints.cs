using FastEndpoints;
using Lots.Shell.Core.Mcp;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Runs;

public sealed record StartRunRequest(string Prompt, string? Profile = null);

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

        // Roles are fixed on the run when it starts: the run acts with the permissions its user had then.
        var me = who.Get(HttpContext);

        var now = clock.GetUtcNow();
        var run = new RunRecord
        {
            Id = Guid.NewGuid(), Prompt = req.Prompt, Profile = profile.Name, UserId = me.UserId, Roles = string.Join(',', me.Roles),
            CreatedAt = now, UpdatedAt = now,
        };
        // Only runs whose profile uses delegated servers keep the user's login token (encrypted), and only until the
        // run ends. It is exchanged per call for a backend-scoped token and never sent to a backend itself.
        if (profile.Servers.Any(s => s.Auth == AuthStrategies.Delegated)
            && AuthSetup.IsOidc(config)
            && HttpContext.Request.Headers.Authorization.ToString() is { } auth
            && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            run.SubjectTokenProtected = vault.Protect(auth["Bearer ".Length..].Trim());
            run.SubjectTokenExpiresAt = long.TryParse(HttpContext.User.FindFirst("exp")?.Value, out var exp)
                ? DateTimeOffset.FromUnixTimeSeconds(exp)
                : now.AddMinutes(5);
        }

        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        await Send.ResponseAsync(new StartRunResponse(run.Id, run.Status.ToString()), 202, ct);
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

public sealed record GetRunRequest(Guid Id);

public sealed record StepDto(
    int Seq, string Kind, string Name, string? ToolCallId, string? Arguments, string? Result,
    long LatencyMs, int? PromptTokens, int? CompletionTokens, DateTimeOffset At);

public sealed record RunDto(
    Guid Id, string Prompt, string Status, string? FinalAnswer, string? Error,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<StepDto> Steps);

/// <summary>A run can be read by its owner and by admins (<c>Auth:AdminRoles</c>, default admin). Others get 404.</summary>
public sealed class GetRunEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config) : Endpoint<GetRunRequest, RunDto>
{
    public override void Configure()
    {
        Get("/runs/{Id}");
    }

    public override async Task HandleAsync(GetRunRequest req, CancellationToken ct)
    {
        var run = await db.Runs.AsNoTracking().Include(r => r.Steps).SingleOrDefaultAsync(r => r.Id == req.Id, ct);
        var me = who.Get(HttpContext);
        var admins = (config["Auth:AdminRoles"] ?? "admin").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (run is null || (run.UserId != me.UserId && !me.Roles.Any(r => admins.Contains(r, StringComparer.OrdinalIgnoreCase))))
        {
            await Send.NotFoundAsync(ct); // do not reveal that someone else's run exists
            return;
        }

        await Send.OkAsync(new RunDto(
            run.Id, run.Prompt, run.Status.ToString(), run.FinalAnswer, run.Error, run.CreatedAt, run.UpdatedAt,
            run.Steps.OrderBy(s => s.Seq).Select(s => new StepDto(
                s.Seq, s.Kind.ToString(), s.Name, s.ToolCallId, s.ArgumentsJson, s.Result,
                s.LatencyMs, s.PromptTokens, s.CompletionTokens, s.CreatedAt)).ToList()), ct);
    }
}
