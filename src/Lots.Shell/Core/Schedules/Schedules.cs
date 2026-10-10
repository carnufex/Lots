using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cronos;
using Lots.Shell.Core.Config;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lots.Shell.Core.Schedules;

/// <summary>Where a scheduled run's result goes besides the UI (#101).</summary>
public sealed record ScheduleDelivery(IReadOnlyList<string> Email, IReadOnlyList<string> Webhooks);

/// <summary>
/// A run started without a person (#101): on a cron schedule, from a webhook (an alert fired), or by an admin ("run now"). It runs
/// as an explicit service identity with minimal roles; policy and approvals apply exactly as for a person, and approvals notify
/// the people who may decide them.
/// </summary>
public sealed record ScheduleSpec(
    string Name, string Profile, string Prompt, string User, IReadOnlyList<string> Roles, CronExpression? Cron, TimeZoneInfo TimeZone,
    string? WebhookSecretRef, int MinIntervalSeconds, IReadOnlyList<string> Viewers, ScheduleDelivery Deliver, bool Enabled, string CronText);

public static class ScheduleParser
{
    // Strict like profiles: a misspelled key (identity roles, delivery) is an error, not a silent default.
    private static IDeserializer Yaml => Config.StrictYaml.Deserializer;

    /// <summary>Service identities are recognisable everywhere (runs, audit): the user id must start with this.</summary>
    public const string ServicePrefix = "svc-";

    public static ScheduleSpec? Parse(string yaml, string name, ProfileRegistry profiles, List<string> errors)
    {
        Doc? d;
        try { d = Yaml.Deserialize<Doc>(yaml); }
        catch (Exception ex) { errors.Add(Config.StrictYaml.Explain(ex, typeof(Doc))); return null; }
        if (d is null) { errors.Add("empty"); return null; }

        var profile = profiles.Find(d.Profile ?? "");
        if (profile is null) errors.Add($"profile '{d.Profile}' does not exist");
        if (string.IsNullOrWhiteSpace(d.Prompt)) errors.Add("prompt is required");
        if (d.Identity?.User is not { Length: > 0 } user || !user.StartsWith(ServicePrefix, StringComparison.Ordinal) || user.Length > 128)
        {
            errors.Add($"identity.user is required and must start with '{ServicePrefix}' (a service identity, never a person)");
            user = "";
        }
        var roles = (d.Identity?.Roles ?? []).Select(r => r.Trim()).Where(r => r.Length > 0).Distinct().ToList();
        if (roles.Count == 0) errors.Add("identity.roles is required: name the minimal roles the run needs");
        if (profile is not null)
            foreach (var r in roles.Where(r => profile.Roles.All(pr => !pr.Name.Equals(r, StringComparison.OrdinalIgnoreCase))))
                errors.Add($"role '{r}' is not defined in profile '{profile.Name}'");

        CronExpression? cron = null;
        if (!string.IsNullOrWhiteSpace(d.Cron))
            try { cron = CronExpression.Parse(d.Cron.Trim()); }
            catch (CronFormatException ex) { errors.Add($"cron '{d.Cron}': {ex.Message}"); }
        if (cron is null && d.Webhook is null && errors.Count == 0) errors.Add("a schedule needs a cron expression, a webhook, or both");
        TimeZoneInfo zone = TimeZoneInfo.Utc;
        if (!string.IsNullOrWhiteSpace(d.TimeZone))
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(d.TimeZone); }
            catch (Exception) { errors.Add($"unknown timeZone '{d.TimeZone}'"); }
        if (d.Webhook is { } w && string.IsNullOrWhiteSpace(w.SecretRef)) errors.Add("webhook.secretRef is required (env:NAME or file:/path)");

        var viewers = Knowledge.KnowledgeAccess.Normalise(d.Deliver?.Viewers ?? [], errors.Add);
        foreach (var url in d.Deliver?.Webhooks ?? [])
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https") errors.Add($"deliver.webhooks: '{url}' is not an https URL");
        foreach (var mail in d.Deliver?.Email ?? [])
            if (!mail.Contains('@')) errors.Add($"deliver.email: '{mail}' is not an e-mail address");

        return errors.Count > 0 ? null : new ScheduleSpec(name, profile!.Name, d.Prompt!.Trim(), user, roles, cron, zone, d.Webhook?.SecretRef,
            Math.Clamp(d.Webhook?.MinIntervalSeconds ?? 30, 0, 86_400), viewers,
            new ScheduleDelivery(d.Deliver?.Email ?? [], d.Deliver?.Webhooks ?? []), d.Enabled ?? true, d.Cron?.Trim() ?? "");
    }

    private sealed class Doc
    {
        public string? Kind { get; set; }
        public string? Name { get; set; }
        public string? Profile { get; set; }
        public string? Prompt { get; set; }
        public IdentityDoc? Identity { get; set; }
        public string? Cron { get; set; }
        public string? TimeZone { get; set; }
        public WebhookDoc? Webhook { get; set; }
        public DeliverDoc? Deliver { get; set; }
        public bool? Enabled { get; set; }
    }

    private sealed class IdentityDoc { public string? User { get; set; } public List<string>? Roles { get; set; } }
    private sealed class WebhookDoc { public string? SecretRef { get; set; } public int? MinIntervalSeconds { get; set; } }
    private sealed class DeliverDoc { public List<string>? Viewers { get; set; } public List<string>? Email { get; set; } public List<string>? Webhooks { get; set; } }
}

public static class ScheduleRuns
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The schedules currently applied (config as code), parsed; invalid ones are skipped.</summary>
    public static async Task<List<ScheduleSpec>> LoadAsync(LotsDbContext db, ProfileRegistry profiles, CancellationToken ct)
    {
        var rows = await db.ConfigResources.AsNoTracking().Where(r => r.Kind == ResourceKinds.Schedule).ToListAsync(ct);
        var list = new List<ScheduleSpec>();
        foreach (var r in rows)
            if (ScheduleParser.Parse(r.Spec, r.Name, profiles, []) is { } spec) list.Add(spec);
        return list;
    }

    /// <summary>
    /// Creates the run. A webhook's payload is untrusted data: it is wrapped like a tool result (ADR 0017), and a suspicious payload
    /// taints the run from the start, so its write calls need an approval.
    /// </summary>
    public static RunRecord Create(ScheduleSpec s, string trigger, string? payload, DateTimeOffset now)
    {
        var prompt = s.Prompt;
        var tainted = false;
        if (payload is not null)
        {
            var guarded = InjectionGuard.Guard("webhook " + s.Name, payload.Length <= 16_000 ? payload : payload[..16_000] + "\n[truncated]");
            prompt += "\n\nThe event that triggered this run:\n" + guarded.Text;
            tainted = guarded.Suspicious;
        }
        return new RunRecord
        {
            Id = Guid.NewGuid(), Prompt = prompt, Profile = s.Profile, UserId = s.User, Roles = string.Join(',', s.Roles), CreatedAt = now,
            UpdatedAt = now, Trigger = $"{trigger}:{s.Name}", Viewers = s.Viewers.Count == 0 ? null : "," + string.Join(',', s.Viewers) + ",",
            DeliverJson = s.Deliver.Email.Count + s.Deliver.Webhooks.Count == 0 ? null : JsonSerializer.Serialize(s.Deliver, Json), Tainted = tainted,
        };
    }

    public static bool SecretMatches(string? supplied, string expected) =>
        supplied is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
}

/// <summary>
/// Fires cron schedules (#101). Every replica runs it; a firing is claimed by inserting (schedule, due time) into
/// <c>schedule_fires</c>, so exactly one replica starts the run. After downtime only the latest missed occurrence runs, and only
/// if it is less than an hour late.
/// </summary>
public sealed class ScheduleWorker(IServiceScopeFactory scopes, ProfileRegistry profiles, TimeProvider clock, ILogger<ScheduleWorker> logger) : BackgroundService
{
    public static readonly TimeSpan CatchUp = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await TickAsync(stop); }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested) { logger.LogWarning("Schedules: {Error}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(20), stop).ContinueWith(_ => { });
        }
    }

    public async Task<int> TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var now = clock.GetUtcNow();
        var fired = 0;
        foreach (var s in await ScheduleRuns.LoadAsync(db, profiles, ct))
        {
            if (!s.Enabled || s.Cron is null) continue;
            var applied = await db.ConfigResources.AsNoTracking().Where(r => r.Kind == ResourceKinds.Schedule && r.Name == s.Name).Select(r => r.AppliedAt).SingleAsync(ct);
            var last = await db.ScheduleFires.AsNoTracking().Where(f => f.Name == s.Name).MaxAsync(f => (DateTimeOffset?)f.DueAt, ct);
            var from = last ?? applied;
            // The latest occurrence that is due: walk forward from the last firing to now.
            DateTimeOffset? due = null;
            for (var next = s.Cron.GetNextOccurrence(from, s.TimeZone); next is { } n && n <= now; next = s.Cron.GetNextOccurrence(n, s.TimeZone))
                due = n;
            if (due is not { } at || now - at > CatchUp) continue;
            if (await ClaimAsync(db, s.Name, at, ct) is not { } claim) continue;
            var run = ScheduleRuns.Create(s, "schedule", null, now);
            claim.RunId = run.Id;
            db.Runs.Add(run);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Schedule {Schedule} fired for {Due} as run {Run}", s.Name, at, run.Id);
            fired++;
        }
        return fired;
    }

    /// <summary>Claims one occurrence; null when another replica already did.</summary>
    private static async Task<ScheduleFireRecord?> ClaimAsync(LotsDbContext db, string name, DateTimeOffset due, CancellationToken ct)
    {
        var claim = new ScheduleFireRecord { Name = name, DueAt = due, FiredAt = DateTimeOffset.UtcNow };
        db.ScheduleFires.Add(claim);
        try
        {
            await db.SaveChangesAsync(ct);
            return claim;
        }
        catch (DbUpdateException)
        {
            db.Entry(claim).State = EntityState.Detached;
            return null;
        }
        catch (ArgumentException) // in-memory provider: duplicate key
        {
            db.Entry(claim).State = EntityState.Detached;
            return null;
        }
    }
}
