using FastEndpoints;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Runs;

public sealed record StartRunRequest(string Prompt, string? Profile = null);

public sealed record StartRunResponse(Guid Id, string Status);

public sealed class StartRunEndpoint(LotsDbContext db, TimeProvider clock, ProfileRegistry profiles, IConfiguration config)
    : Endpoint<StartRunRequest, StartRunResponse>
{
    public override void Configure()
    {
        Post("/runs");
        AllowAnonymous(); // M1: local only, OIDC arrives in M2
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

        // Until OIDC (#16) the acting user comes from configuration. Roles are fixed on the run when it starts.
        var userId = config["Auth:Dev:UserId"] ?? "dev";
        var roles = config["Auth:Dev:Roles"] ?? "operator";

        var now = clock.GetUtcNow();
        var run = new RunRecord
        {
            Id = Guid.NewGuid(), Prompt = req.Prompt, Profile = profile.Name, UserId = userId, Roles = roles,
            CreatedAt = now, UpdatedAt = now,
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        await Send.ResponseAsync(new StartRunResponse(run.Id, run.Status.ToString()), 202, ct);
    }
}

public sealed record GetRunRequest(Guid Id);

public sealed record StepDto(
    int Seq, string Kind, string Name, string? ToolCallId, string? Arguments, string? Result,
    long LatencyMs, int? PromptTokens, int? CompletionTokens, DateTimeOffset At);

public sealed record RunDto(
    Guid Id, string Prompt, string Status, string? FinalAnswer, string? Error,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<StepDto> Steps);

public sealed class GetRunEndpoint(LotsDbContext db) : Endpoint<GetRunRequest, RunDto>
{
    public override void Configure()
    {
        Get("/runs/{Id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(GetRunRequest req, CancellationToken ct)
    {
        var run = await db.Runs.AsNoTracking().Include(r => r.Steps).SingleOrDefaultAsync(r => r.Id == req.Id, ct);
        if (run is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(new RunDto(
            run.Id, run.Prompt, run.Status.ToString(), run.FinalAnswer, run.Error, run.CreatedAt, run.UpdatedAt,
            run.Steps.OrderBy(s => s.Seq).Select(s => new StepDto(
                s.Seq, s.Kind.ToString(), s.Name, s.ToolCallId, s.ArgumentsJson, s.Result,
                s.LatencyMs, s.PromptTokens, s.CompletionTokens, s.CreatedAt)).ToList()), ct);
    }
}
