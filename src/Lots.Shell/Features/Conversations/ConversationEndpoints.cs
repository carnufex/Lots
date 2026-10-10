using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Conversations;

/// <summary>One thing that took time in a turn: speech in, a model call, a tool call or speech out. Times are relative to the conversation start.</summary>
public sealed record TimelineEvent(string Kind, string Name, long StartMs, long DurationMs, int? PromptTokens = null, int? CompletionTokens = null);

public sealed record TurnDto(
    Guid RunId, string Prompt, string? Answer, string Status, string? Error, DateTimeOffset StartedAt, long DurationMs,
    IReadOnlyList<TimelineEvent> Events, bool Voice = false, Guid? RetryOf = null, bool Superseded = false,
    IReadOnlyList<Core.Attachments.AttachmentRef>? Attachments = null, int? Rating = null, string? FeedbackComment = null);

public sealed record StageTotals(long SttMs, long LlmMs, long ToolMs, long TtsMs, long OtherMs);

public sealed record ConversationSummary(
    Guid Id, string UserId, string Profile, string Title, string? Summary, bool Voice, DateTimeOffset StartedAt, DateTimeOffset EndedAt,
    long DurationMs, int Turns, int Messages, string Status, int PromptTokens, int CompletionTokens, StageTotals Stages);

public sealed record ConversationDetail(ConversationSummary Conversation, IReadOnlyList<TurnDto> Turns);

public sealed record ConversationList(IReadOnlyList<ConversationSummary> Conversations, StageTotals Stages, int Count);

/// <summary>Builds conversation views (history, timing) from runs, steps and speech usage. Read-only.</summary>
public static class ConversationViews
{
    public static bool IsAdmin(Principal me, IConfiguration config)
    {
        var admins = (config["Auth:AdminRoles"] ?? "admin").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return me.Roles.Any(r => admins.Contains(r, StringComparer.OrdinalIgnoreCase));
    }

    public static async Task<List<ConversationDetail>> LoadAsync(LotsDbContext db, IQueryable<RunRecord> runs, CancellationToken ct)
    {
        var loaded = await runs.Include(r => r.Steps).AsNoTracking().OrderBy(r => r.CreatedAt).ToListAsync(ct);
        var ids = loaded.Select(r => r.ConversationId).Where(c => c.HasValue).Select(c => c!.Value).Distinct().ToList();
        var speech = await db.VoiceUsage.AsNoTracking()
            .Where(u => u.ConversationId != null && ids.Contains(u.ConversationId.Value) && u.Outcome == "ok").ToListAsync(ct);
        var summaries = await db.Conversations.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        // The run owner's own rating of each answer (#121), shown in the chat and in History.
        var runIds = loaded.Select(r => r.Id).ToList();
        var feedback = (await db.Feedback.AsNoTracking().Where(f => runIds.Contains(f.RunId)).ToListAsync(ct))
            .Where(f => loaded.Any(r => r.Id == f.RunId && r.UserId == f.UserId)).ToDictionary(f => f.RunId);

        return loaded.Where(r => r.ConversationId != null).GroupBy(r => r.ConversationId!.Value)
            .Select(g =>
            {
                var detail = Build(g.Key, g.OrderBy(r => r.CreatedAt).ToList(), speech.Where(u => u.ConversationId == g.Key).ToList(), feedback);
                // A generated title and summary (#80) replace the first prompt as the title once they exist.
                return summaries.TryGetValue(g.Key, out var s) && !string.IsNullOrEmpty(s.Title)
                    ? detail with { Conversation = detail.Conversation with { Title = s.Title, Summary = s.Summary } }
                    : detail;
            })
            .OrderByDescending(c => c.Conversation.StartedAt).ToList();
    }

    private static ConversationDetail Build(Guid id, List<RunRecord> runs, List<VoiceUsageRecord> speech, IReadOnlyDictionary<Guid, FeedbackRecord> feedback)
    {
        // Dictation happens before its run exists, so each Stt call is attached to the first run that starts after it.
        var stt = speech.Where(u => u.Direction == "Stt").OrderBy(u => u.At).ToList();
        var first = runs[0];
        var started = runs.Min(r => r.CreatedAt);
        var firstStt = stt.Count > 0 ? stt.Min(u => u.At - TimeSpan.FromMilliseconds(u.LatencyMs)) : started;
        if (firstStt < started) started = firstStt;

        var sttByRun = stt.GroupBy(u => (runs.FirstOrDefault(r => r.CreatedAt >= u.At.AddSeconds(-1)) ?? runs[^1]).Id).ToDictionary(g => g.Key, g => g.ToList());
        var turns = new List<TurnDto>();
        foreach (var run in runs)
        {
            var events = new List<TimelineEvent>();
            long Rel(DateTimeOffset t) => Math.Max(0, (long)(t - started).TotalMilliseconds);

            foreach (var u in sttByRun.GetValueOrDefault(run.Id) ?? [])
                events.Add(new("stt", "speech to text", Rel(u.At - TimeSpan.FromMilliseconds(u.LatencyMs)), u.LatencyMs));

            foreach (var s in run.Steps.OrderBy(s => s.Seq))
                events.Add(new(s.Kind == StepKind.ModelCall ? "llm" : "tool", s.Name, Rel(s.CreatedAt - TimeSpan.FromMilliseconds(s.LatencyMs)), s.LatencyMs,
                    s.PromptTokens, s.CompletionTokens));

            foreach (var u in speech.Where(u => u.Direction == "Tts" && u.RunId == run.Id))
                events.Add(new("tts", $"first audio after {u.LatencyMs} ms", Rel(u.At), u.DurationMs ?? u.LatencyMs));

            var end = events.Count == 0 ? Rel(run.UpdatedAt) : Math.Max(Rel(run.UpdatedAt), events.Max(e => e.StartMs + e.DurationMs));
            var begin = events.Count == 0 ? Rel(run.CreatedAt) : Math.Min(Rel(run.CreatedAt), events.Min(e => e.StartMs));
            // Superseded: the turn was regenerated or edited (#95); the chat shows only its successor.
            turns.Add(new TurnDto(run.Id, run.Prompt, run.FinalAnswer, run.Status.ToString(), run.Error, run.CreatedAt, end - begin,
                events.OrderBy(e => e.StartMs).ToList(), run.Voice, run.RetryOf, runs.Any(r => r.RetryOf == run.Id),
                run.AttachmentsJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<List<Core.Attachments.AttachmentRef>>(run.AttachmentsJson,
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
                feedback.GetValueOrDefault(run.Id)?.Rating, feedback.GetValueOrDefault(run.Id)?.Comment));
        }

        var all = turns.SelectMany(t => t.Events).ToList();
        long Sum(string kind) => all.Where(e => e.Kind == kind).Sum(e => e.DurationMs);
        var ended = runs.Max(r => r.UpdatedAt);
        var duration = Math.Max(0, (long)(ended - started).TotalMilliseconds);
        var stages = new StageTotals(Sum("stt"), Sum("llm"), Sum("tool"), Sum("tts"), 0);
        stages = stages with { OtherMs = Math.Max(0, duration - (stages.SttMs + stages.LlmMs + stages.ToolMs + stages.TtsMs)) };

        var status = runs.Any(r => r.Status == RunStatus.Failed) ? "Failed"
            : runs.Any(r => r.Status is RunStatus.Running or RunStatus.Pending) ? "Running"
            : runs.Any(r => r.Status == RunStatus.WaitingForApproval) ? "WaitingForApproval"
            : runs.All(r => r.Status == RunStatus.Cancelled) ? "Cancelled" : "Completed";
        var title = first.Prompt.Length <= 80 ? first.Prompt : first.Prompt[..80] + "…";
        var steps = runs.SelectMany(r => r.Steps).ToList();
        var summary = new ConversationSummary(
            id, first.UserId, first.Profile, title, null, runs.Any(r => r.Voice), started, ended, duration, runs.Count, runs.Count * 2, status,
            steps.Sum(s => s.PromptTokens ?? 0), steps.Sum(s => s.CompletionTokens ?? 0), stages);
        return new ConversationDetail(summary, turns);
    }

    public static StageTotals Sum(IEnumerable<ConversationSummary> list)
    {
        var l = list.ToList();
        return new StageTotals(l.Sum(c => c.Stages.SttMs), l.Sum(c => c.Stages.LlmMs), l.Sum(c => c.Stages.ToolMs), l.Sum(c => c.Stages.TtsMs), l.Sum(c => c.Stages.OtherMs));
    }
}

public sealed class ListConversationsRequest
{
    [QueryParam] public string? Q { get; set; }
    [QueryParam] public string? Status { get; set; }
    [QueryParam] public string? User { get; set; }
    [QueryParam] public DateTimeOffset? From { get; set; }
    [QueryParam] public DateTimeOffset? To { get; set; }
    [QueryParam] public string? Profile { get; set; }
    [QueryParam] public int? Limit { get; set; }
}

/// <summary>Conversation history. Everyone sees their own conversations; admins see everyone's.</summary>
public sealed class ListConversationsEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config)
    : Endpoint<ListConversationsRequest, ConversationList>
{
    public override void Configure() => Get("/conversations");

    public override async Task HandleAsync(ListConversationsRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var runs = db.Runs.Where(r => r.ConversationId != null);
        if (!ConversationViews.IsAdmin(me, config)) runs = runs.Where(r => r.UserId == me.UserId);
        else if (!string.IsNullOrWhiteSpace(req.User)) runs = runs.Where(r => r.UserId == req.User);
        if (req.From is { } from) runs = runs.Where(r => r.CreatedAt >= from);
        if (req.To is { } to) runs = runs.Where(r => r.CreatedAt <= to);
        if (!string.IsNullOrWhiteSpace(req.Profile)) runs = runs.Where(r => r.Profile == req.Profile);

        var all = (await ConversationViews.LoadAsync(db, runs, ct)).Select(c => c).ToList();
        IEnumerable<ConversationDetail> view = all;
        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var q = req.Q.Trim();
            view = view.Where(c => c.Turns.Any(t => t.Prompt.Contains(q, StringComparison.OrdinalIgnoreCase)
                                                   || (t.Answer?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)));
        }
        if (!string.IsNullOrWhiteSpace(req.Status)) view = view.Where(c => string.Equals(c.Conversation.Status, req.Status, StringComparison.OrdinalIgnoreCase));

        var list = view.Select(c => c.Conversation).Take(Math.Clamp(req.Limit ?? 100, 1, 500)).ToList();
        await Send.OkAsync(new ConversationList(list, ConversationViews.Sum(list), list.Count), ct);
    }
}

public sealed record GetConversationRequest(Guid Id);

public sealed class GetConversationEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config)
    : Endpoint<GetConversationRequest, ConversationDetail>
{
    public override void Configure() => Get("/conversations/{Id}");

    public override async Task HandleAsync(GetConversationRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var runs = db.Runs.Where(r => r.ConversationId == req.Id);
        var detail = (await ConversationViews.LoadAsync(db, runs, ct)).SingleOrDefault();
        if (detail is null || (detail.Conversation.UserId != me.UserId && !ConversationViews.IsAdmin(me, config)))
        {
            await Send.NotFoundAsync(ct); // not revealing whether it exists
            return;
        }
        await Send.OkAsync(detail, ct);
    }
}
