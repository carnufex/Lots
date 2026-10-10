using System.Text.Json;
using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Proposals;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Insights;

public sealed record CreateProposalRequest(string Profile, string Title, string Rationale, ProposedChanges Changes, ProposalEvidence? Evidence = null);

public sealed record ProposalDto(Guid Id, string Profile, int BaseVersion, string Title, string Rationale, ProposalEvidence? Evidence, string Diff,
    string State, JsonElement? Evaluation, bool? Regresses, string? PrUrl, string CreatedBy, DateTimeOffset CreatedAt, string? DecidedBy, string? Note);

public sealed record ProposalIdRequest(Guid Id, string? Note = null);

public sealed record EvaluationRequest(Guid Id, JsonElement Evaluation, bool Regresses);

public sealed record ProposalQuery(string? State = null, string? Profile = null);

public static class ProposalViews
{
    public static ProposalDto ToDto(ProposalRecord p) => new(p.Id, p.Profile, p.BaseVersion, p.Title, p.Rationale,
        p.EvidenceJson is null ? null : JsonSerializer.Deserialize<ProposalEvidence>(p.EvidenceJson), p.Diff, p.State.ToString(),
        p.EvaluationJson is null ? null : JsonDocument.Parse(p.EvaluationJson).RootElement.Clone(), p.Regresses, p.PrUrl, p.CreatedBy, p.CreatedAt, p.DecidedBy, p.Note);
}

/// <summary>A proposal from a person or the self-improve agent (#144). Only instructions, description, model and conflict detection may change.</summary>
public sealed class CreateProposalEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config, ProfileRegistry profiles, TimeProvider clock)
    : Endpoint<CreateProposalRequest, ProposalDto>
{
    public override void Configure() => Post("/insights/proposals");

    public override async Task HandleAsync(CreateProposalRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        if (!InsightsAccess.Allowed(me, config)) { await Send.ForbiddenAsync(ct); return; }
        if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Rationale)) AddError("A proposal needs a title and a rationale.");
        ThrowIfAnyErrors();
        try
        {
            var p = ProposalBuilder.Create(req.Profile, req.Changes, req.Title, req.Rationale, req.Evidence, me.UserId, profiles, db,
                new ModelCatalogLike(HttpContext.RequestServices.GetService<Core.Models.ModelCatalog>()), clock.GetUtcNow());
            db.Proposals.Add(p);
            await db.SaveChangesAsync(ct);
            await Send.OkAsync(ProposalViews.ToDto(p), ct);
        }
        catch (ProposalException ex)
        {
            AddError(ex.Message);
            await Send.ErrorsAsync(400, ct);
        }
    }
}

public sealed class ListProposalsEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config) : Endpoint<ProposalQuery, List<ProposalDto>>
{
    public override void Configure() => Get("/insights/proposals");

    public override async Task HandleAsync(ProposalQuery q, CancellationToken ct)
    {
        if (!InsightsAccess.Allowed(who.Get(HttpContext), config)) { await Send.ForbiddenAsync(ct); return; }
        var rows = db.Proposals.AsNoTracking();
        if (Enum.TryParse<ProposalState>(q.State, true, out var s)) rows = rows.Where(p => p.State == s);
        if (!string.IsNullOrWhiteSpace(q.Profile)) rows = rows.Where(p => p.Profile == q.Profile);
        await Send.OkAsync((await rows.OrderByDescending(p => p.CreatedAt).Take(200).ToListAsync(ct)).Select(ProposalViews.ToDto).ToList(), ct);
    }
}

/// <summary>One proposal with the full proposed profile, for the evaluator (<c>Lots.Evals --mode proposal</c>).</summary>
public sealed class GetProposalEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config) : Endpoint<ProposalIdRequest>
{
    public override void Configure() => Get("/insights/proposals/{Id}");

    public override async Task HandleAsync(ProposalIdRequest req, CancellationToken ct)
    {
        if (!InsightsAccess.Allowed(who.Get(HttpContext), config)) { await Send.ForbiddenAsync(ct); return; }
        var p = await db.Proposals.AsNoTracking().SingleOrDefaultAsync(x => x.Id == req.Id, ct);
        if (p is null) { await Send.NotFoundAsync(ct); return; }
        await Send.OkAsync(new { proposal = ProposalViews.ToDto(p), baseYaml = p.BaseYaml, proposedYaml = p.ProposedYaml }, ct);
    }
}

/// <summary>Records the eval delta (current vs proposed) that <c>Lots.Evals --mode proposal</c> measured.</summary>
public sealed class ProposalEvaluationEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config) : Endpoint<EvaluationRequest, ProposalDto>
{
    public override void Configure() => Post("/insights/proposals/{Id}/evaluation");

    public override async Task HandleAsync(EvaluationRequest req, CancellationToken ct)
    {
        if (!InsightsAccess.Allowed(who.Get(HttpContext), config)) { await Send.ForbiddenAsync(ct); return; }
        var p = await db.Proposals.SingleOrDefaultAsync(x => x.Id == req.Id, ct);
        if (p is null) { await Send.NotFoundAsync(ct); return; }
        p.EvaluationJson = req.Evaluation.GetRawText();
        p.Regresses = req.Regresses;
        if (p.State == ProposalState.Draft) p.State = ProposalState.Evaluated;
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(ProposalViews.ToDto(p), ct);
    }
}

/// <summary>Opens the pull request in the config repository (Proposals:Git). A person still reviews and merges it.</summary>
public sealed class ProposalPullRequestEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config, ProposalGit git, TimeProvider clock)
    : Endpoint<ProposalIdRequest, ProposalDto>
{
    public override void Configure() => Post("/insights/proposals/{Id}/pr");

    public override async Task HandleAsync(ProposalIdRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        if (!InsightsAccess.Allowed(me, config)) { await Send.ForbiddenAsync(ct); return; }
        var p = await db.Proposals.SingleOrDefaultAsync(x => x.Id == req.Id, ct);
        if (p is null) { await Send.NotFoundAsync(ct); return; }
        if (!git.Configured) AddError("No configuration repository is set up (Proposals:Git); apply the diff by hand.");
        if (p.State is ProposalState.Rejected or ProposalState.Merged or ProposalState.PrOpened) AddError($"The proposal is already {p.State}.");
        if (p.EvaluationJson is null) AddError("Evaluate the proposal first (Lots.Evals --mode proposal).");
        ThrowIfAnyErrors();
        try
        {
            p.PrUrl = await git.OpenPullRequestAsync(p, ct);
            p.State = ProposalState.PrOpened;
            p.DecidedBy = me.UserId;
            p.DecidedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            await Send.OkAsync(ProposalViews.ToDto(p), ct);
        }
        catch (Exception ex) when (ex is ProposalException or HttpRequestException)
        {
            AddError(Core.Security.SecretRedactor.Redact(ex.Message));
            await Send.ErrorsAsync(502, ct);
        }
    }
}

public sealed class RejectProposalEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config, TimeProvider clock) : Endpoint<ProposalIdRequest, ProposalDto>
{
    public override void Configure() => Post("/insights/proposals/{Id}/reject");

    public override async Task HandleAsync(ProposalIdRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        if (!InsightsAccess.Allowed(me, config)) { await Send.ForbiddenAsync(ct); return; }
        var p = await db.Proposals.SingleOrDefaultAsync(x => x.Id == req.Id, ct);
        if (p is null) { await Send.NotFoundAsync(ct); return; }
        p.State = ProposalState.Rejected;
        p.DecidedBy = me.UserId;
        p.DecidedAt = clock.GetUtcNow();
        p.Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(ProposalViews.ToDto(p), ct);
    }
}

/// <summary>Did the merged version help? Compares outcomes of the proposed version with the one it replaced, with a revert diff when it regressed.</summary>
public sealed class ProposalFollowUpEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config, ProfileRegistry profiles) : Endpoint<ProposalIdRequest, FollowUp>
{
    public override void Configure() => Get("/insights/proposals/{Id}/follow-up");

    public override async Task HandleAsync(ProposalIdRequest req, CancellationToken ct)
    {
        if (!InsightsAccess.Allowed(who.Get(HttpContext), config)) { await Send.ForbiddenAsync(ct); return; }
        var p = await db.Proposals.SingleOrDefaultAsync(x => x.Id == req.Id, ct);
        if (p is null) { await Send.NotFoundAsync(ct); return; }
        var result = await ProposalFollowUp.CheckAsync(db, p, profiles, config.GetValue("Proposals:FollowUpMinRuns", 20), ct);
        if (result.Verdict is not ("not-applied" or "too-early") && p.State != ProposalState.Merged)
        {
            p.State = ProposalState.Merged; // live with the proposed instructions: it was merged and applied
            await db.SaveChangesAsync(ct);
        }
        await Send.OkAsync(result, ct);
    }
}
