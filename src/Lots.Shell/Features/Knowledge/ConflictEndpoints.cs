using System.Text.Json;
using FastEndpoints;
using Lots.Shell.Core.Knowledge;
using Lots.Shell.Core.Policy;
using Lots.Shell.Features.Conversations;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Knowledge;

public sealed record ConflictOptionDto(string ChunkId, string Title, string SourceName, string Heading, string Text, DateTimeOffset UpdatedAt, bool Changed);

public sealed record ConflictDto(
    Guid Id, string Question, string Summary, string Status, DateTimeOffset DetectedAt, IReadOnlyList<ConflictOptionDto> Options,
    string? MyVote, ConflictTally Tally, string? ResolvedOption, string? ResolvedBy);

internal static class ConflictViews
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static List<ConflictOption> Options(KnowledgeConflictRecord c) => JsonSerializer.Deserialize<List<ConflictOption>>(c.OptionsJson, Json) ?? [];

    public static Dictionary<string, double> Weights(IConfiguration config) =>
        config.GetSection("Knowledge:VoteWeights").GetChildren()
            .ToDictionary(x => x.Key, x => double.TryParse(x.Value, System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 1.0, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The conflict as this caller may see it: only options they may read, and tallies only for those (votes on restricted
    /// sources are not visible to others). Null when fewer than two options are readable.
    /// </summary>
    public static async Task<ConflictDto?> ViewAsync(KnowledgeConflictRecord c, Principal me, IKnowledgeStore store, LotsDbContext db,
        IConfiguration config, TimeProvider clock, bool all, CancellationToken ct)
    {
        var readers = KnowledgeAccess.TokensOf(me);
        var options = new List<ConflictOptionDto>();
        var hashes = new Dictionary<string, string?>();
        foreach (var o in Options(c))
        {
            // Admins (source owners) see every option so they can fix the stale one; everyone else only what they may read.
            var hit = all ? await AnyReaderAsync(store, o, ct) : await store.ChunkAsync(o.ChunkId, readers, ct);
            hashes[o.ChunkId] = hit?.ContentHash;
            if (hit is null) continue;
            options.Add(new ConflictOptionDto(o.ChunkId, hit.Title, hit.SourceName, hit.Heading, hit.Text, hit.UpdatedAt, hit.ContentHash != o.ContentHash));
        }
        if (options.Count < 2 && !all) return null;

        var votes = await db.ConflictVotes.AsNoTracking().Where(v => v.ConflictId == c.Id).ToListAsync(ct);
        var visible = votes.Where(v => v.Option == ConflictVoting.Neither || options.Any(o => o.ChunkId == v.Option));
        var tally = ConflictVoting.Tally(visible, hashes, Weights(config), clock.GetUtcNow());
        return new ConflictDto(c.Id, c.Question, c.Summary, c.Status, c.DetectedAt, options, votes.FirstOrDefault(v => v.UserId == me.UserId)?.Option,
            tally, c.ResolvedOption, c.ResolvedBy);
    }

    private static async Task<KnowledgeHit?> AnyReaderAsync(IKnowledgeStore store, ConflictOption o, CancellationToken ct)
    {
        var source = await store.GetSourceAsync(o.SourceId, ct);
        return source is null ? null : await store.ChunkAsync(o.ChunkId, source.Readers.ToArray(), ct);
    }

    public static bool IsOwnerOrAdmin(Principal me, IConfiguration config) => ConversationViews.IsAdmin(me, config);
}

public sealed record ConflictRequest(Guid Id);

public sealed class GetConflictEndpoint(LotsDbContext db, IKnowledgeStore store, ICurrentPrincipal who, IConfiguration config, TimeProvider clock)
    : Endpoint<ConflictRequest, ConflictDto>
{
    public override void Configure() => Get("/knowledge/conflicts/{Id}");

    public override async Task HandleAsync(ConflictRequest req, CancellationToken ct)
    {
        var c = await db.KnowledgeConflicts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == req.Id, ct);
        var view = c is null ? null : await ConflictViews.ViewAsync(c, who.Get(HttpContext), store, db, config, clock, all: false, ct);
        if (view is null) await Send.NotFoundAsync(ct);
        else await Send.OkAsync(view, ct);
    }
}

public sealed record VoteRequest(Guid Id, string Option);

/// <summary>
/// One vote per user per conflict (a new vote replaces the old one), only for options the voter may read, rate-limited.
/// Votes are a signal for source owners: they never change policy, instructions, permissions or approvals.
/// </summary>
public sealed class VoteConflictEndpoint(LotsDbContext db, IKnowledgeStore store, ICurrentPrincipal who, IConfiguration config, TimeProvider clock)
    : Endpoint<VoteRequest, ConflictDto>
{
    public override void Configure() => Post("/knowledge/conflicts/{Id}/vote");

    public override async Task HandleAsync(VoteRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var c = await db.KnowledgeConflicts.SingleOrDefaultAsync(x => x.Id == req.Id, ct);
        var view = c is null ? null : await ConflictViews.ViewAsync(c, me, store, db, config, clock, all: false, ct);
        if (c is null || view is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (req.Option != ConflictVoting.Neither && view.Options.All(o => o.ChunkId != req.Option))
        {
            AddError(x => x.Option, "Vote for one of the options shown, or 'neither'.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        var now = clock.GetUtcNow();
        var lastHour = await db.ConflictVotes.CountAsync(v => v.UserId == me.UserId && v.At > now.AddHours(-1), ct);
        if (lastHour >= ConflictVoting.MaxVotesPerHour)
        {
            AddError("Too many votes in the last hour; try again later.");
            await Send.ErrorsAsync(429, ct);
            return;
        }

        var hash = req.Option == ConflictVoting.Neither ? null : (await store.ChunkAsync(req.Option, KnowledgeAccess.TokensOf(me), ct))?.ContentHash;
        var vote = await db.ConflictVotes.SingleOrDefaultAsync(v => v.ConflictId == c.Id && v.UserId == me.UserId, ct);
        if (vote is null)
            db.ConflictVotes.Add(vote = new ConflictVoteRecord { Id = Guid.NewGuid(), ConflictId = c.Id, UserId = me.UserId, Option = req.Option });
        vote.Option = req.Option;
        vote.ContentHash = hash;
        vote.Roles = string.Join(',', me.Roles);
        vote.At = now;
        await db.SaveChangesAsync(ct);
        await Send.OkAsync((await ConflictViews.ViewAsync(c, me, store, db, config, clock, all: false, ct))!, ct);
    }
}

/// <summary>Conflicts with their tallies and suggestions, for admins (source owners): what to fix where.</summary>
public sealed class ListConflictsEndpoint(LotsDbContext db, IKnowledgeStore store, ICurrentPrincipal who, IConfiguration config, TimeProvider clock)
    : EndpointWithoutRequest<List<ConflictDto>>
{
    public override void Configure() => Get("/knowledge/conflicts");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        if (!ConflictViews.IsOwnerOrAdmin(me, config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var list = new List<ConflictDto>();
        foreach (var c in await db.KnowledgeConflicts.AsNoTracking().OrderByDescending(c => c.DetectedAt).Take(200).ToListAsync(ct))
            if (await ConflictViews.ViewAsync(c, me, store, db, config, clock, all: true, ct) is { } v) list.Add(v);
        await Send.OkAsync(list, ct);
    }
}

public sealed record ResolveRequest(Guid Id, string Option);

/// <summary>Only an admin marks an option as authoritative; votes alone never do.</summary>
public sealed class ResolveConflictEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config, TimeProvider clock) : Endpoint<ResolveRequest>
{
    public override void Configure() => Post("/knowledge/conflicts/{Id}/resolve");

    public override async Task HandleAsync(ResolveRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        if (!ConflictViews.IsOwnerOrAdmin(me, config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var c = await db.KnowledgeConflicts.SingleOrDefaultAsync(x => x.Id == req.Id, ct);
        if (c is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (ConflictViews.Options(c).All(o => o.ChunkId != req.Option))
        {
            AddError(x => x.Option, "Not an option of this conflict.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        c.Status = "resolved";
        c.ResolvedOption = req.Option;
        c.ResolvedBy = me.UserId;
        c.ResolvedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
