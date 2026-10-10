using FastEndpoints;
using Lots.Shell.Core.Memory;
using Lots.Shell.Core.Policy;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Features.Memory;

public sealed record MemoryDto(Guid Id, string Text, string Source, bool Confirmed, DateTimeOffset CreatedAt, DateTimeOffset? ConfirmedAt, string? Profile);

public sealed record AddMemoryRequest(string Text);

public sealed record MemoryRequest(Guid Id);

public sealed record EditMemoryRequest(Guid Id, string Text);

public static class MemoryViews
{
    public static MemoryDto ToDto(MemoryRecord m) => new(m.Id, m.Text, m.Source, m.ConfirmedAt is not null, m.CreatedAt, m.ConfirmedAt, m.Profile);

    public static string? Problem(string? text, MemoryOptions o) =>
        string.IsNullOrWhiteSpace(text) ? "Write what Lots should remember."
        : text.Length > o.MaxChars ? $"Keep it under {o.MaxChars} characters."
        : Lots.Shell.Core.Security.SecretRedactor.LooksLikeSecret(text, out _) ? "That looks like a secret; secrets are never remembered."
        : null;
}

/// <summary>Everything Lots remembers about the caller, suggestions first (#99).</summary>
public sealed class ListMemoriesEndpoint(LotsDbContext db, ICurrentPrincipal who) : EndpointWithoutRequest<List<MemoryDto>>
{
    public override void Configure() => Get("/me/memories");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var me = who.Get(HttpContext).UserId;
        var rows = await db.Memories.AsNoTracking().Where(m => m.UserId == me).OrderBy(m => m.ConfirmedAt != null).ThenByDescending(m => m.CreatedAt).ToListAsync(ct);
        await Send.OkAsync(rows.Select(MemoryViews.ToDto).ToList(), ct);
    }
}

/// <summary>The user writes a memory themselves: confirmed at once.</summary>
public sealed class AddMemoryEndpoint(LotsDbContext db, ICurrentPrincipal who, IOptions<MemoryOptions> options, TimeProvider clock) : Endpoint<AddMemoryRequest, MemoryDto>
{
    public override void Configure() => Post("/me/memories");

    public override async Task HandleAsync(AddMemoryRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext).UserId;
        if (MemoryViews.Problem(req.Text, options.Value) is { } p) AddError(x => x.Text, p);
        if (await db.Memories.CountAsync(m => m.UserId == me, ct) >= options.Value.MaxPerUser) AddError($"At most {options.Value.MaxPerUser} memories; delete some first.");
        ThrowIfAnyErrors();
        var now = clock.GetUtcNow();
        var row = new MemoryRecord { Id = Guid.NewGuid(), UserId = me, Text = req.Text.Trim(), Source = MemorySources.User, CreatedAt = now, ConfirmedAt = now };
        db.Memories.Add(row);
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(MemoryViews.ToDto(row), ct);
    }
}

/// <summary>Keeps a suggestion the agent made: from now on it is part of the context.</summary>
public sealed class ConfirmMemoryEndpoint(LotsDbContext db, ICurrentPrincipal who, TimeProvider clock) : Endpoint<MemoryRequest, MemoryDto>
{
    public override void Configure() => Post("/me/memories/{Id}/confirm");

    public override async Task HandleAsync(MemoryRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext).UserId;
        var row = await db.Memories.SingleOrDefaultAsync(m => m.Id == req.Id && m.UserId == me, ct);
        if (row is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        row.ConfirmedAt ??= clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(MemoryViews.ToDto(row), ct);
    }
}

public sealed class EditMemoryEndpoint(LotsDbContext db, ICurrentPrincipal who, IOptions<MemoryOptions> options, TimeProvider clock) : Endpoint<EditMemoryRequest, MemoryDto>
{
    public override void Configure() => Put("/me/memories/{Id}");

    public override async Task HandleAsync(EditMemoryRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext).UserId;
        var row = await db.Memories.SingleOrDefaultAsync(m => m.Id == req.Id && m.UserId == me, ct);
        if (row is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (MemoryViews.Problem(req.Text, options.Value) is { } p) AddError(x => x.Text, p);
        ThrowIfAnyErrors();
        row.Text = req.Text.Trim();
        row.ConfirmedAt = clock.GetUtcNow(); // editing it is confirming it
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(MemoryViews.ToDto(row), ct);
    }
}

public sealed class DeleteMemoryEndpoint(LotsDbContext db, ICurrentPrincipal who) : Endpoint<MemoryRequest>
{
    public override void Configure() => Delete("/me/memories/{Id}");

    public override async Task HandleAsync(MemoryRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext).UserId;
        var row = await db.Memories.SingleOrDefaultAsync(m => m.Id == req.Id && m.UserId == me, ct);
        if (row is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        db.Memories.Remove(row);
        await db.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}

/// <summary>Forget everything (#99).</summary>
public sealed class ClearMemoriesEndpoint(LotsDbContext db, ICurrentPrincipal who) : EndpointWithoutRequest
{
    public override void Configure() => Delete("/me/memories");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var me = who.Get(HttpContext).UserId;
        db.Memories.RemoveRange(await db.Memories.Where(m => m.UserId == me).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
