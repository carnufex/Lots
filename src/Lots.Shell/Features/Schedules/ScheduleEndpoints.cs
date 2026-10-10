using FastEndpoints;
using Lots.Shell.Core.Mcp;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Schedules;
using Lots.Shell.Features.Admin;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Schedules;

public sealed record TriggerRequest(string Name);

public sealed record TriggeredDto(Guid RunId);

/// <summary>
/// Starts a schedule's run from outside (#101), e.g. an Alertmanager or Grafana webhook. Authenticated by the schedule's own secret
/// (<c>Authorization: Bearer &lt;secret&gt;</c>), not by a user: the run's identity is the schedule's service identity. The body
/// (up to 64 KB) is given to the run as untrusted data. At most one run per <c>webhook.minIntervalSeconds</c>.
/// </summary>
public sealed class TriggerEndpoint(LotsDbContext db, ProfileRegistry profiles, TimeProvider clock, ILogger<TriggerEndpoint> logger)
    : EndpointWithoutRequest<TriggeredDto> // the body is the event, read as text: no model binding may consume it
{
    public const int MaxBody = 64 * 1024;

    public override void Configure()
    {
        Post("/triggers/{Name}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var req = new TriggerRequest(Route<string>("Name") ?? "");
        var spec = (await ScheduleRuns.LoadAsync(db, profiles, ct)).FirstOrDefault(s => s.Name.Equals(req.Name, StringComparison.OrdinalIgnoreCase));
        string? secret = null;
        try { secret = spec?.WebhookSecretRef is { } r ? SecretReference.Resolve(r) : null; }
        catch (InvalidOperationException ex) { logger.LogWarning("Trigger {Name}: secret unavailable: {Error}", req.Name, ex.Message); }
        string? supplied = HttpContext.Request.Headers.Authorization;
        // Unknown schedule, no webhook, disabled or wrong secret: all look the same from outside.
        if (spec is not { Enabled: true } || secret is null || !ScheduleRuns.SecretMatches(supplied?.StartsWith("Bearer ") == true ? supplied[7..].Trim() : null, secret))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var now = clock.GetUtcNow();
        var trigger = "webhook:" + spec.Name;
        if (await db.Runs.AnyAsync(r => r.Trigger == trigger && r.CreatedAt > now.AddSeconds(-spec.MinIntervalSeconds), ct))
        {
            AddError($"This trigger fired less than {spec.MinIntervalSeconds} s ago.");
            await Send.ErrorsAsync(429, ct);
            return;
        }

        string payload;
        using (var reader = new StreamReader(HttpContext.Request.Body))
        {
            var buffer = new char[MaxBody + 1];
            var read = await reader.ReadBlockAsync(buffer, ct);
            if (read > MaxBody)
            {
                AddError($"The body is larger than {MaxBody / 1024} KB.");
                await Send.ErrorsAsync(413, ct);
                return;
            }
            payload = new string(buffer, 0, read);
        }

        var run = ScheduleRuns.Create(spec, "webhook", string.IsNullOrWhiteSpace(payload) ? null : payload, now);
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Schedule {Schedule} triggered by webhook as run {Run}", spec.Name, run.Id);
        await Send.ResponseAsync(new TriggeredDto(run.Id), 202, ct);
    }
}

public sealed record ScheduleDto(
    string Name, string Profile, string User, IReadOnlyList<string> Roles, string? Cron, string TimeZone, DateTimeOffset? Next, bool Webhook,
    bool Enabled, IReadOnlyList<string> Viewers, int DeliverTargets, Guid? LastRunId, string? LastStatus, DateTimeOffset? LastRunAt);

/// <summary>The applied schedules with their next occurrence and last run (#101). Admins.</summary>
public sealed class ListSchedulesEndpoint(LotsDbContext db, ProfileRegistry profiles, TimeProvider clock, IConfiguration config, ICurrentPrincipal who)
    : AdminEndpoint<EmptyRequest, List<ScheduleDto>>(config, who)
{
    public override void Configure() => Get("/admin/schedules");

    public override async Task HandleAsync(EmptyRequest req, CancellationToken ct)
    {
        if (!await AllowedAsync(ct)) return;
        var now = clock.GetUtcNow();
        var list = new List<ScheduleDto>();
        foreach (var s in await ScheduleRuns.LoadAsync(db, profiles, ct))
        {
            var last = await db.Runs.AsNoTracking().Where(r => r.Trigger != null && r.Trigger.EndsWith(":" + s.Name))
                .OrderByDescending(r => r.CreatedAt).Select(r => new { r.Id, r.Status, r.CreatedAt }).FirstOrDefaultAsync(ct);
            list.Add(new ScheduleDto(s.Name, s.Profile, s.User, s.Roles, s.Cron is null ? null : s.CronText, s.TimeZone.Id,
                s.Enabled ? s.Cron?.GetNextOccurrence(now, s.TimeZone) : null, s.WebhookSecretRef is not null, s.Enabled, s.Viewers,
                s.Deliver.Email.Count + s.Deliver.Webhooks.Count, last?.Id, last?.Status.ToString(), last?.CreatedAt));
        }
        await Send.OkAsync(list, ct);
    }
}

public sealed record RunScheduleRequest(string Name);

/// <summary>Runs a schedule now, as its service identity (#101). Admins; recorded as a <c>manual:</c> trigger.</summary>
public sealed class RunScheduleNowEndpoint(LotsDbContext db, ProfileRegistry profiles, TimeProvider clock, IConfiguration config, ICurrentPrincipal who,
    ILogger<RunScheduleNowEndpoint> logger) : AdminEndpoint<RunScheduleRequest, TriggeredDto>(config, who)
{
    public override void Configure() => Post("/admin/schedules/{Name}/run");

    public override async Task HandleAsync(RunScheduleRequest req, CancellationToken ct)
    {
        if (!await AllowedAsync(ct)) return;
        if ((await ScheduleRuns.LoadAsync(db, profiles, ct)).FirstOrDefault(s => s.Name.Equals(req.Name, StringComparison.OrdinalIgnoreCase)) is not { } spec)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var run = ScheduleRuns.Create(spec, "manual", null, clock.GetUtcNow());
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Schedule {Schedule} run now by {Admin} as run {Run}", spec.Name, Me.UserId, run.Id);
        await Send.ResponseAsync(new TriggeredDto(run.Id), 202, ct);
    }
}
