using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Groups;

public sealed record GroupDto(Guid Id, string Name, string? Color, string? Icon, int SortOrder, bool Pinned, bool Archived, string? Instructions,
    string? DefaultContext, bool ShareContext, int Chats);

/// <param name="ShareContext">Chats in the group may use each other's context (default on).</param>
public sealed record GroupRequest(Guid Id, string? Name = null, string? Color = null, string? Icon = null, int? SortOrder = null, bool? Pinned = null,
    bool? Archived = null, string? Instructions = null, string? DefaultContext = null, bool? ShareContext = null);

/// <summary>Chat groups (#153): one user's folders of conversations. Everything here is the caller's own; nobody else's groups are visible.</summary>
public static class GroupViews
{
    public static GroupDto ToDto(ConversationGroupRecord g, int chats) =>
        new(g.Id, g.Name, g.Color, g.Icon, g.SortOrder, g.Pinned, g.Archived, g.Instructions, g.DefaultContext, g.ShareContext, chats);

    /// <summary>Applies the fields that were given; returns an error for a field that is not acceptable.</summary>
    public static string? Apply(ConversationGroupRecord g, GroupRequest r, Principal me, ProfileRegistry profiles)
    {
        if (r.Name is { } name)
        {
            name = name.Trim();
            if (name.Length is 0 or > 100) return "A group needs a name of at most 100 characters.";
            g.Name = name;
        }
        if (r.Color is { } c) g.Color = c.Length == 0 ? null : c.Length <= 20 ? c : g.Color;
        if (r.Icon is { } i) g.Icon = i.Length == 0 ? null : i.Length <= 40 ? i : g.Icon;
        if (r.SortOrder is { } o) g.SortOrder = o;
        if (r.Pinned is { } p) g.Pinned = p;
        if (r.Archived is { } a) g.Archived = a;
        if (r.Instructions is { } ins)
        {
            if (ins.Length > 2_000) return "Group instructions are limited to 2000 characters.";
            if (Core.Security.SecretRedactor.LooksLikeSecret(ins, out _)) return "That looks like a secret; group instructions are sent to the model.";
            g.Instructions = ins.Trim().Length == 0 ? null : ins.Trim();
        }
        if (r.DefaultContext is { } dc)
        {
            if (dc.Length > 0 && !Core.Routing.ContextRouter.Usable(me, profiles).Any(x => x.Name == dc)) return $"'{dc}' is not a context you can use.";
            g.DefaultContext = dc.Length == 0 ? null : dc;
        }
        if (r.ShareContext is { } share) g.ShareContext = share;
        return null;
    }
}

public sealed class ListGroupsEndpoint(LotsDbContext db, ICurrentPrincipal who) : EndpointWithoutRequest<List<GroupDto>>
{
    public override void Configure() => Get("/groups");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var me = who.Get(HttpContext).UserId;
        var groups = await db.ConversationGroups.AsNoTracking().Where(g => g.UserId == me).ToListAsync(ct);
        var counts = await db.Conversations.AsNoTracking().Where(c => c.UserId == me && c.GroupId != null)
            .GroupBy(c => c.GroupId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        await Send.OkAsync(groups.OrderByDescending(g => g.Pinned).ThenBy(g => g.SortOrder).ThenBy(g => g.Name)
            .Select(g => GroupViews.ToDto(g, counts.GetValueOrDefault(g.Id))).ToList(), ct);
    }
}

public sealed class CreateGroupEndpoint(LotsDbContext db, ICurrentPrincipal who, ProfileRegistry profiles, TimeProvider clock) : Endpoint<GroupRequest, GroupDto>
{
    public override void Configure() => Post("/groups");

    public override async Task HandleAsync(GroupRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        if (await db.ConversationGroups.CountAsync(g => g.UserId == me.UserId, ct) >= 200) AddError("At most 200 groups per user.");
        var now = clock.GetUtcNow();
        var g = new ConversationGroupRecord { Id = Guid.NewGuid(), UserId = me.UserId, Name = "", CreatedAt = now, UpdatedAt = now };
        if (GroupViews.Apply(g, req with { Name = req.Name ?? "" }, me, profiles) is { } error) AddError(error);
        ThrowIfAnyErrors();
        g.SortOrder = req.SortOrder ?? await db.ConversationGroups.Where(x => x.UserId == me.UserId).Select(x => (int?)x.SortOrder).MaxAsync(ct) + 1 ?? 0;
        db.ConversationGroups.Add(g);
        await db.SaveChangesAsync(ct);
        await Send.ResponseAsync(GroupViews.ToDto(g, 0), 201, ct);
    }
}

public sealed class UpdateGroupEndpoint(LotsDbContext db, ICurrentPrincipal who, ProfileRegistry profiles, TimeProvider clock) : Endpoint<GroupRequest, GroupDto>
{
    public override void Configure() => Put("/groups/{Id}");

    public override async Task HandleAsync(GroupRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var g = await db.ConversationGroups.SingleOrDefaultAsync(x => x.Id == req.Id && x.UserId == me.UserId, ct);
        if (g is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (GroupViews.Apply(g, req, me, profiles) is { } error) AddError(error);
        ThrowIfAnyErrors();
        g.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(GroupViews.ToDto(g, await db.Conversations.CountAsync(c => c.GroupId == g.Id && c.UserId == me.UserId, ct)), ct);
    }
}

public sealed record GroupIdRequest(Guid Id);

/// <summary>Deletes the group; its chats stay, ungrouped.</summary>
public sealed class DeleteGroupEndpoint(LotsDbContext db, ICurrentPrincipal who) : Endpoint<GroupIdRequest>
{
    public override void Configure() => Delete("/groups/{Id}");

    public override async Task HandleAsync(GroupIdRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext).UserId;
        var g = await db.ConversationGroups.SingleOrDefaultAsync(x => x.Id == req.Id && x.UserId == me, ct);
        if (g is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        foreach (var c in await db.Conversations.Where(c => c.GroupId == g.Id && c.UserId == me).ToListAsync(ct)) c.GroupId = null;
        db.ConversationGroups.Remove(g);
        await db.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}

/// <param name="GroupId">Move into this group; <paramref name="Ungroup"/> takes it out. Fields left out stay as they are.</param>
public sealed record OrganiseRequest(Guid Id, Guid? GroupId = null, bool? Ungroup = null, bool? Isolated = null, bool? Pinned = null, bool? Archived = null);

public sealed record OrganisedDto(Guid Id, Guid? GroupId, bool Isolated, bool Pinned, bool Archived);

/// <summary>Moves a chat into or out of a group, isolates, pins or archives it. Only the chat's own user.</summary>
public sealed class OrganiseConversationEndpoint(LotsDbContext db, ICurrentPrincipal who) : Endpoint<OrganiseRequest, OrganisedDto>
{
    public override void Configure() => Put("/conversations/{Id}/organise");

    public override async Task HandleAsync(OrganiseRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext).UserId;
        var owner = await db.Runs.AsNoTracking().Where(r => r.ConversationId == req.Id).Select(r => r.UserId).FirstOrDefaultAsync(ct);
        if (owner != me)
        {
            await Send.NotFoundAsync(ct); // not revealing whether someone else's chat exists
            return;
        }
        if (req.GroupId is { } gid && !await db.ConversationGroups.AnyAsync(g => g.Id == gid && g.UserId == me, ct))
        {
            AddError(x => x.GroupId!, "No such group.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        var c = await db.Conversations.SingleOrDefaultAsync(x => x.Id == req.Id, ct);
        if (c is null) db.Conversations.Add(c = new ConversationRecord { Id = req.Id, UserId = me });
        if (req.Ungroup == true) c.GroupId = null;
        else if (req.GroupId is { } g) c.GroupId = g;
        if (req.Isolated is { } i) c.Isolated = i;
        if (req.Pinned is { } p) c.Pinned = p;
        if (req.Archived is { } a) c.Archived = a;
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(new OrganisedDto(c.Id, c.GroupId, c.Isolated, c.Pinned, c.Archived), ct);
    }
}
