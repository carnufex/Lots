using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Quotas;
using Lots.Shell.Features.Admin;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Usage;

/// <summary>The caller's limits and how much of them is used (#78).</summary>
public sealed class MyQuotaEndpoint(QuotaService quotas, ICurrentPrincipal who) : EndpointWithoutRequest<QuotaStatus>
{
    public override void Configure() => Get("/me/quota");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        await Send.OkAsync(new QuotaStatus(await quotas.LimitsAsync(me, null, ct), await quotas.UsageAsync(me.UserId, ct)), ct);
    }
}

public sealed record QuotaOverrideRequest(string User, int? RunsPerMinute = null, int? ConcurrentRuns = null, long? TokensPerDay = null,
    int? ToolCallsPerRun = null, double? SpeechSecondsPerDay = null);

/// <summary>An admin sets a person's own limits (empty fields keep the role/profile limits); every change is recorded with who set it.</summary>
public sealed class SetQuotaOverrideEndpoint(LotsDbContext db, TimeProvider clock, IConfiguration config, ICurrentPrincipal who)
    : AdminEndpoint<QuotaOverrideRequest, QuotaOverrideRecord>(config, who)
{
    public override void Configure() => Put("/admin/v1/quotas/{User}");

    public override async Task HandleAsync(QuotaOverrideRequest req, CancellationToken ct)
    {
        if (!await AllowedAsync(ct)) return;
        var row = await db.QuotaOverrides.SingleOrDefaultAsync(q => q.UserId == req.User, ct);
        if (row is null) db.QuotaOverrides.Add(row = new QuotaOverrideRecord { UserId = req.User });
        row.RunsPerMinute = req.RunsPerMinute;
        row.ConcurrentRuns = req.ConcurrentRuns;
        row.TokensPerDay = req.TokensPerDay;
        row.ToolCallsPerRun = req.ToolCallsPerRun;
        row.SpeechSecondsPerDay = req.SpeechSecondsPerDay;
        row.SetBy = Me.UserId;
        row.SetAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(row, ct);
    }
}

public sealed record UserRequest(string User);

public sealed class ListQuotaOverridesEndpoint(LotsDbContext db, IConfiguration config, ICurrentPrincipal who) : AdminEndpoint<EmptyRequest, List<QuotaOverrideRecord>>(config, who)
{
    public override void Configure() => Get("/admin/v1/quotas");

    public override async Task HandleAsync(EmptyRequest _, CancellationToken ct)
    {
        if (!await AllowedAsync(ct)) return;
        await Send.OkAsync(await db.QuotaOverrides.AsNoTracking().OrderBy(q => q.UserId).ToListAsync(ct), ct);
    }
}

public sealed class DeleteQuotaOverrideEndpoint(LotsDbContext db, IConfiguration config, ICurrentPrincipal who) : AdminEndpoint<UserRequest, EmptyResponse>(config, who)
{
    public override void Configure() => Delete("/admin/v1/quotas/{User}");

    public override async Task HandleAsync(UserRequest req, CancellationToken ct)
    {
        if (!await AllowedAsync(ct)) return;
        var row = await db.QuotaOverrides.SingleOrDefaultAsync(q => q.UserId == req.User, ct);
        if (row is null) { await Send.NotFoundAsync(ct); return; }
        db.QuotaOverrides.Remove(row);
        await db.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
