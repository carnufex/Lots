using System.Text.Json;
using FastEndpoints;
using Lots.Shell.Core.Mining;
using Lots.Shell.Core.Policy;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Insights;

public static class InsightsAccess
{
    /// <summary>Who sees insights and reviews mined candidates: <c>Insights:Roles</c> (default admin, auditor, self-improve).</summary>
    public static bool Allowed(Principal me, IConfiguration config)
    {
        var roles = config.GetSection("Insights:Roles").Get<string[]>() is { Length: > 0 } r ? r : ["admin", "auditor", "self-improve"];
        return me.Roles.Any(x => roles.Contains(x, StringComparer.OrdinalIgnoreCase));
    }
}

public sealed record CandidateDto(Guid Id, string Signature, string Profile, int ProfileVersion, string Problem, int Runs, int Impact,
    IReadOnlyList<Guid> RunIds, MinedCase Draft, MinedCase? Case, string State, string? ReviewedBy, DateTimeOffset? ReviewedAt, string? ReviewNote,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen);

public sealed record CandidateQuery(string? State = null, string? Profile = null, int Limit = 100);

public sealed record CandidateDecision(Guid Id, MinedCase? Case = null, string? Note = null);

public sealed record DatasetRequest(string? Profile = null);

public static class CandidateViews
{
    public static CandidateDto ToDto(EvalCandidateRecord c) => new(c.Id, c.Signature, c.Profile, c.ProfileVersion, c.Problem, c.Runs, c.Impact,
        JsonSerializer.Deserialize<List<Guid>>(c.RunIdsJson) ?? [], JsonSerializer.Deserialize<MinedCase>(c.DraftJson, FailureMiner.Json)!,
        c.CaseJson is null ? null : JsonSerializer.Deserialize<MinedCase>(c.CaseJson, FailureMiner.Json), c.State.ToString(), c.ReviewedBy, c.ReviewedAt,
        c.ReviewNote, c.FirstSeen, c.LastSeen);
}

/// <summary>Mined failure clusters with their drafted eval cases (#143), highest impact first.</summary>
public sealed class ListCandidatesEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config) : Endpoint<CandidateQuery, List<CandidateDto>>
{
    public override void Configure() => Get("/insights/candidates");

    public override async Task HandleAsync(CandidateQuery q, CancellationToken ct)
    {
        if (!InsightsAccess.Allowed(who.Get(HttpContext), config)) { await Send.ForbiddenAsync(ct); return; }
        var rows = db.EvalCandidates.AsNoTracking();
        if (Enum.TryParse<CandidateState>(q.State, true, out var state)) rows = rows.Where(c => c.State == state);
        if (!string.IsNullOrWhiteSpace(q.Profile)) rows = rows.Where(c => c.Profile == q.Profile);
        var list = await rows.OrderByDescending(c => c.Impact).ThenByDescending(c => c.LastSeen).Take(Math.Clamp(q.Limit, 1, 500)).ToListAsync(ct);
        await Send.OkAsync(list.Select(CandidateViews.ToDto).ToList(), ct);
    }
}

/// <summary>Runs the mining now instead of waiting for the schedule.</summary>
public sealed class MineEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config, Microsoft.Extensions.Options.IOptions<MiningOptions> options,
    TimeProvider clock) : EndpointWithoutRequest<MiningSummary>
{
    public override void Configure() => Post("/insights/mine");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!InsightsAccess.Allowed(who.Get(HttpContext), config)) { await Send.ForbiddenAsync(ct); return; }
        var embeddings = HttpContext.RequestServices.GetService<Core.Knowledge.IEmbeddingModel>();
        await Send.OkAsync(await FailureMiner.MineAsync(db, embeddings, options.Value, clock, ct), ct);
    }
}

/// <summary>Accepts a candidate, optionally edited: from now on its case is in the mined dataset and the regression gate.</summary>
public sealed class AcceptCandidateEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config, TimeProvider clock) : Endpoint<CandidateDecision, CandidateDto>
{
    public override void Configure() => Post("/insights/candidates/{Id}/accept");

    public override async Task HandleAsync(CandidateDecision req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        if (!InsightsAccess.Allowed(me, config)) { await Send.ForbiddenAsync(ct); return; }
        var row = await db.EvalCandidates.SingleOrDefaultAsync(c => c.Id == req.Id, ct);
        if (row is null) { await Send.NotFoundAsync(ct); return; }
        var draft = JsonSerializer.Deserialize<MinedCase>(row.DraftJson, FailureMiner.Json)!;
        var final = req.Case is null ? draft : req.Case with { Id = draft.Id, Profile = draft.Profile };
        if (string.IsNullOrWhiteSpace(final.Question)) AddError("The case needs a question.");
        if (Core.Security.SecretRedactor.LooksLikeSecret(final.Question, out _)) AddError("The question contains what looks like a secret; edit it out.");
        if (final.ExpectedTools is null && final.ForbiddenTools is null && !final.ExpectRefusal && string.IsNullOrWhiteSpace(final.Judge))
            AddError("Say what a good answer does: expected or forbidden tools, a refusal, or judge criteria.");
        ThrowIfAnyErrors();
        row.CaseJson = JsonSerializer.Serialize(final with { Question = FailureMiner.Clean(final.Question) }, FailureMiner.Json);
        Decide(row, CandidateState.Accepted, me.UserId, req.Note);
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(CandidateViews.ToDto(row), ct);
    }

    internal void Decide(EvalCandidateRecord row, CandidateState state, string user, string? note)
    {
        row.State = state;
        row.ReviewedBy = user;
        row.ReviewedAt = clock.GetUtcNow();
        row.ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 2000)];
    }
}

public sealed class RejectCandidateEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config, TimeProvider clock) : Endpoint<CandidateDecision, CandidateDto>
{
    public override void Configure() => Post("/insights/candidates/{Id}/reject");

    public override async Task HandleAsync(CandidateDecision req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        if (!InsightsAccess.Allowed(me, config)) { await Send.ForbiddenAsync(ct); return; }
        var row = await db.EvalCandidates.SingleOrDefaultAsync(c => c.Id == req.Id, ct);
        if (row is null) { await Send.NotFoundAsync(ct); return; }
        row.State = CandidateState.Rejected;
        row.ReviewedBy = me.UserId;
        row.ReviewedAt = clock.GetUtcNow();
        row.ReviewNote = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim()[..Math.Min(req.Note.Trim().Length, 2000)];
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(CandidateViews.ToDto(row), ct);
    }
}

/// <summary>The accepted cases as a Lots.Evals dataset file (<c>mined</c>, or <c>mined-&lt;profile&gt;</c>).</summary>
public sealed class MinedDatasetEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config) : Endpoint<DatasetRequest>
{
    public override void Configure() => Get("/insights/eval-cases");

    public override async Task HandleAsync(DatasetRequest req, CancellationToken ct)
    {
        if (!InsightsAccess.Allowed(who.Get(HttpContext), config)) { await Send.ForbiddenAsync(ct); return; }
        var cases = (await db.EvalCandidates.AsNoTracking().Where(c => c.State == CandidateState.Accepted && c.CaseJson != null)
                .OrderBy(c => c.ReviewedAt).ToListAsync(ct))
            .Select(c => JsonSerializer.Deserialize<MinedCase>(c.CaseJson!, FailureMiner.Json)!)
            .Where(c => string.IsNullOrWhiteSpace(req.Profile) || c.Profile == req.Profile).ToList();
        var name = "mined" + (string.IsNullOrWhiteSpace(req.Profile) ? "" : "-" + string.Concat(req.Profile.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')));
        var body = new { dataset = name, version = Math.Max(1, cases.Count), profile = string.IsNullOrWhiteSpace(req.Profile) ? null : req.Profile, cases };
        await Send.StringAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions(FailureMiner.Json) { WriteIndented = true }), contentType: "application/json", cancellation: ct);
    }
}
