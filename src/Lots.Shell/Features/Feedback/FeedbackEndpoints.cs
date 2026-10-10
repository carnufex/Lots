using System.Text.Json;
using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Features.Runs;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Feedback;

public sealed record FeedbackRequest(Guid Id, int Rating, string? Comment = null);

public sealed record RunFeedbackRequest(Guid Id);

public sealed record FeedbackDto(Guid Id, Guid RunId, int Rating, string? Comment, DateTimeOffset UpdatedAt, string State);

/// <summary>An eval case in the dataset format of <c>Lots.Evals</c> (docs/evals.md).</summary>
public sealed record FeedbackCase(string Id, string Question, string? Profile = null, List<string>? ExpectedTools = null,
    List<string>? ExpectedFacts = null, List<string>? ForbiddenTools = null, bool ExpectRefusal = false, string? Judge = null);

public sealed record FeedbackItem(Guid Id, Guid RunId, string User, string Profile, int Rating, string? Comment, string Prompt, string? Answer,
    IReadOnlyList<string> ToolsCalled, DateTimeOffset CreatedAt, string State, string? ReviewedBy, DateTimeOffset? ReviewedAt, string? ReviewNote,
    FeedbackCase? Case);

public sealed record FeedbackQueue(IReadOnlyList<FeedbackItem> Items, int Open, int Down, int Up);

public sealed record FeedbackQueueRequest(string? State = null, string? Rating = null, string? Profile = null, int Limit = 100);

public sealed record ResolveFeedbackRequest(Guid Id, string? Note = null);

/// <param name="Question">The question as it goes into the dataset; edit it to remove personal data. Default: the run's prompt.</param>
public sealed record EvalCaseFromFeedbackRequest(Guid Id, string? Question = null, List<string>? ExpectedTools = null, List<string>? ExpectedFacts = null,
    List<string>? ForbiddenTools = null, bool ExpectRefusal = false, string? Judge = null);

public sealed record FeedbackExportRequest(string? Profile = null);

public sealed record FeedbackDataset(string Dataset, int Version, string? Profile, IReadOnlyList<FeedbackCase> Cases);

/// <summary>A human verdict on an answer, in the label format of judge calibration (<c>--mode calibrate</c>).</summary>
public sealed record FeedbackLabel(string Id, string Question, string Criteria, string Answer, string Label, string? Note);

public static class FeedbackViews
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static FeedbackDto ToDto(FeedbackRecord f) => new(f.Id, f.RunId, f.Rating, f.Comment, f.UpdatedAt, f.State.ToString());

    /// <summary>
    /// Who reviews feedback: roles in <c>Feedback:ReviewRoles</c> (default admin). Reviewers read other users' prompts and answers,
    /// so give the role only to people who may read every run anyway.
    /// </summary>
    public static bool IsReviewer(Principal me, IConfiguration config)
    {
        var roles = config.GetSection("Feedback:ReviewRoles").Get<string[]>() is { Length: > 0 } r ? r : ["admin"];
        return me.Roles.Any(x => roles.Contains(x, StringComparer.OrdinalIgnoreCase));
    }

    public static FeedbackCase? CaseOf(FeedbackRecord f) => f.CaseJson is null ? null : JsonSerializer.Deserialize<FeedbackCase>(f.CaseJson, Json);

    public static string CaseId(Guid feedback) => "fb-" + feedback.ToString("N")[..8];
}

/// <summary>Rate an answer you can read (#121): thumbs up (1) or down (-1) and an optional comment. Rating again replaces it.</summary>
public sealed class PutFeedbackEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config, TimeProvider clock) : Endpoint<FeedbackRequest, FeedbackDto>
{
    /// <summary>The run's outcome carries the rating (#141): recompute it.</summary>
    internal static async Task MarkOutcomeDirtyAsync(LotsDbContext db, Guid runId, CancellationToken ct)
    {
        if (await db.RunOutcomes.SingleOrDefaultAsync(o => o.RunId == runId, ct) is { } outcome) outcome.Dirty = true;
    }

    public override void Configure() => Put("/runs/{Id}/feedback");

    public override async Task HandleAsync(FeedbackRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == req.Id, ct);
        if (run is null || !RunAccess.CanRead(run, me, config))
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (req.Rating is not (1 or -1)) AddError(x => x.Rating, "Rating is 1 (good) or -1 (bad).");
        var comment = string.IsNullOrWhiteSpace(req.Comment) ? null : req.Comment.Trim();
        if (comment is { Length: > 2000 }) AddError(x => x.Comment!, "Keep the comment under 2000 characters.");
        if (comment is not null && Core.Security.SecretRedactor.LooksLikeSecret(comment, out _))
            AddError(x => x.Comment!, "That looks like a secret; leave it out of the comment.");
        ThrowIfAnyErrors();

        var now = clock.GetUtcNow();
        var row = await db.Feedback.SingleOrDefaultAsync(f => f.RunId == run.Id && f.UserId == me.UserId, ct);
        if (row is null)
        {
            row = new FeedbackRecord { Id = Guid.NewGuid(), RunId = run.Id, UserId = me.UserId, CreatedAt = now };
            db.Feedback.Add(row);
        }
        // A changed rating is news for the reviewers again, unless it already became an eval case.
        if (row.State == FeedbackState.Resolved && (row.Rating != req.Rating || row.Comment != comment)) row.State = FeedbackState.Open;
        row.Rating = req.Rating;
        row.Comment = comment;
        row.UpdatedAt = now;
        await MarkOutcomeDirtyAsync(db, run.Id, ct);
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(FeedbackViews.ToDto(row), ct);
    }
}

/// <summary>Your own rating of a run; 404 when you have not rated it.</summary>
public sealed class GetFeedbackEndpoint(LotsDbContext db, ICurrentPrincipal who) : Endpoint<RunFeedbackRequest, FeedbackDto>
{
    public override void Configure() => Get("/runs/{Id}/feedback");

    public override async Task HandleAsync(RunFeedbackRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext).UserId;
        var row = await db.Feedback.AsNoTracking().SingleOrDefaultAsync(f => f.RunId == req.Id && f.UserId == me, ct);
        if (row is null) await Send.NotFoundAsync(ct);
        else await Send.OkAsync(FeedbackViews.ToDto(row), ct);
    }
}

public sealed class DeleteFeedbackEndpoint(LotsDbContext db, ICurrentPrincipal who) : Endpoint<RunFeedbackRequest>
{
    public override void Configure() => Delete("/runs/{Id}/feedback");

    public override async Task HandleAsync(RunFeedbackRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext).UserId;
        var row = await db.Feedback.SingleOrDefaultAsync(f => f.RunId == req.Id && f.UserId == me, ct);
        if (row is not null)
        {
            db.Feedback.Remove(row);
            await PutFeedbackEndpoint.MarkOutcomeDirtyAsync(db, req.Id, ct);
            await db.SaveChangesAsync(ct);
        }
        await Send.NoContentAsync(ct);
    }
}

/// <summary>The review queue (#121): ratings with the prompt, answer and tools of their run. Reviewers only.</summary>
public sealed class FeedbackQueueEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config) : Endpoint<FeedbackQueueRequest, FeedbackQueue>
{
    public override void Configure() => Get("/feedback");

    public override async Task HandleAsync(FeedbackQueueRequest req, CancellationToken ct)
    {
        if (!FeedbackViews.IsReviewer(who.Get(HttpContext), config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var q = db.Feedback.AsNoTracking().Join(db.Runs.AsNoTracking(), f => f.RunId, r => r.Id, (f, r) => new { f, r });
        if (Enum.TryParse<FeedbackState>(req.State, true, out var state)) q = q.Where(x => x.f.State == state);
        if (req.Rating?.ToLowerInvariant() is "down" or "-1") q = q.Where(x => x.f.Rating < 0);
        else if (req.Rating?.ToLowerInvariant() is "up" or "1") q = q.Where(x => x.f.Rating > 0);
        if (!string.IsNullOrWhiteSpace(req.Profile)) q = q.Where(x => x.r.Profile == req.Profile);

        var rows = await q.OrderByDescending(x => x.f.UpdatedAt).Take(Math.Clamp(req.Limit, 1, 500)).ToListAsync(ct);
        var runIds = rows.Select(x => x.r.Id).ToList();
        var tools = (await db.RunSteps.AsNoTracking().Where(s => runIds.Contains(s.RunId) && s.Kind == StepKind.ToolCall)
                .Select(s => new { s.RunId, s.Name, s.Seq }).ToListAsync(ct))
            .GroupBy(s => s.RunId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.OrderBy(s => s.Seq).Select(s => s.Name).ToList());

        var items = rows.Select(x => new FeedbackItem(x.f.Id, x.r.Id, x.f.UserId, x.r.Profile, x.f.Rating, x.f.Comment, x.r.Prompt, x.r.FinalAnswer,
            tools.GetValueOrDefault(x.r.Id) ?? [], x.f.CreatedAt, x.f.State.ToString(), x.f.ReviewedBy, x.f.ReviewedAt, x.f.ReviewNote,
            FeedbackViews.CaseOf(x.f))).ToList();
        await Send.OkAsync(new FeedbackQueue(items,
            await db.Feedback.CountAsync(f => f.State == FeedbackState.Open, ct),
            await db.Feedback.CountAsync(f => f.State == FeedbackState.Open && f.Rating < 0, ct),
            await db.Feedback.CountAsync(f => f.State == FeedbackState.Open && f.Rating > 0, ct)), ct);
    }
}

public sealed class ResolveFeedbackEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config, TimeProvider clock) : Endpoint<ResolveFeedbackRequest, FeedbackDto>
{
    public override void Configure() => Post("/feedback/{Id}/resolve");

    public override async Task HandleAsync(ResolveFeedbackRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        if (!FeedbackViews.IsReviewer(me, config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var row = await db.Feedback.SingleOrDefaultAsync(f => f.Id == req.Id, ct);
        if (row is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        row.State = row.State == FeedbackState.Converted ? FeedbackState.Converted : FeedbackState.Resolved;
        row.ReviewedBy = me.UserId;
        row.ReviewedAt = clock.GetUtcNow();
        row.ReviewNote = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim()[..Math.Min(req.Note.Trim().Length, 2000)];
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(FeedbackViews.ToDto(row), ct);
    }
}

/// <summary>
/// Turns a rated answer into an eval case (#121): the run's question (editable, to strip personal data) and the behaviour the reviewer
/// expects. At least one expectation is required, otherwise the case would pass on any answer.
/// </summary>
public sealed class EvalCaseFromFeedbackEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config, TimeProvider clock)
    : Endpoint<EvalCaseFromFeedbackRequest, FeedbackCase>
{
    public override void Configure() => Post("/feedback/{Id}/eval-case");

    public override async Task HandleAsync(EvalCaseFromFeedbackRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        if (!FeedbackViews.IsReviewer(me, config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var row = await db.Feedback.SingleOrDefaultAsync(f => f.Id == req.Id, ct);
        var run = row is null ? null : await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == row.RunId, ct);
        if (row is null || run is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        static List<string>? Clean(List<string>? xs) => xs?.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList() is { Count: > 0 } l ? l : null;
        var question = string.IsNullOrWhiteSpace(req.Question) ? run.Prompt : req.Question.Trim();
        var judge = string.IsNullOrWhiteSpace(req.Judge) ? null : req.Judge.Trim();
        var c = new FeedbackCase(FeedbackViews.CaseId(row.Id), question, run.Profile, Clean(req.ExpectedTools), Clean(req.ExpectedFacts),
            Clean(req.ForbiddenTools), req.ExpectRefusal, judge);
        if (c.ExpectedTools is null && c.ExpectedFacts is null && c.ForbiddenTools is null && !c.ExpectRefusal && c.Judge is null)
            AddError("Say what a good answer does: expected tools or facts, forbidden tools, a refusal, or judge criteria.");
        if (question.Length > 4000) AddError(x => x.Question!, "Keep the question under 4000 characters.");
        if (Core.Security.SecretRedactor.LooksLikeSecret(question, out _)) AddError(x => x.Question!, "The question contains what looks like a secret; edit it out.");
        ThrowIfAnyErrors();

        row.CaseJson = JsonSerializer.Serialize(c, FeedbackViews.Json);
        row.State = FeedbackState.Converted;
        row.ReviewedBy = me.UserId;
        row.ReviewedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(c, ct);
    }
}

/// <summary>The eval cases made from feedback, as a dataset file for <c>Lots.Evals --file</c>. Reviewers only.</summary>
public sealed class FeedbackDatasetEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config) : Endpoint<FeedbackExportRequest, FeedbackDataset>
{
    public override void Configure() => Get("/feedback/eval-cases");

    public override async Task HandleAsync(FeedbackExportRequest req, CancellationToken ct)
    {
        if (!FeedbackViews.IsReviewer(who.Get(HttpContext), config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var cases = (await db.Feedback.AsNoTracking().Where(f => f.CaseJson != null).OrderBy(f => f.ReviewedAt).ToListAsync(ct))
            .Select(FeedbackViews.CaseOf).OfType<FeedbackCase>()
            .Where(c => string.IsNullOrWhiteSpace(req.Profile) || c.Profile == req.Profile).ToList();
        var name = "feedback" + (string.IsNullOrWhiteSpace(req.Profile) ? "" : "-" + string.Concat(req.Profile.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')));
        // The version follows the cases: a new case is a new version, so its first run is not read as a regression.
        var dataset = new FeedbackDataset(name, Math.Max(1, cases.Count), string.IsNullOrWhiteSpace(req.Profile) ? null : req.Profile, cases);
        // Written without nulls, indented: the file is meant to be saved under evals/ and read like a hand-written one.
        await Send.StringAsync(JsonSerializer.Serialize(dataset, new JsonSerializerOptions(FeedbackViews.Json) { WriteIndented = true }),
            contentType: "application/json", cancellation: ct);
    }
}

/// <summary>Human verdicts from feedback that became cases with judge criteria: labels for judge calibration (#117). Reviewers only.</summary>
public sealed class FeedbackLabelsEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config) : Endpoint<FeedbackExportRequest, List<FeedbackLabel>>
{
    public override void Configure() => Get("/feedback/labels");

    public override async Task HandleAsync(FeedbackExportRequest req, CancellationToken ct)
    {
        if (!FeedbackViews.IsReviewer(who.Get(HttpContext), config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var rows = await db.Feedback.AsNoTracking().Where(f => f.CaseJson != null)
            .Join(db.Runs.AsNoTracking(), f => f.RunId, r => r.Id, (f, r) => new { f, r.FinalAnswer, r.Profile }).ToListAsync(ct);
        var labels = rows
            .Where(x => x.FinalAnswer is not null && (string.IsNullOrWhiteSpace(req.Profile) || x.Profile == req.Profile))
            .Select(x => (x.f, Case: FeedbackViews.CaseOf(x.f), x.FinalAnswer))
            .Where(x => x.Case?.Judge is not null)
            .Select(x => new FeedbackLabel(x.Case!.Id, x.Case.Question, x.Case.Judge!, x.FinalAnswer!, x.f.Rating > 0 ? "pass" : "fail", x.f.Comment))
            .ToList();
        await Send.OkAsync(labels, ct);
    }
}
