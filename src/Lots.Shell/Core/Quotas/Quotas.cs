using Lots.Shell.Core.Notifications;
using Lots.Shell.Core.Policy;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Core.Quotas;

/// <summary>Limits for one person. Null = unlimited. Tokens and speech count from midnight UTC.</summary>
public sealed class QuotaLimits
{
    public int? RunsPerMinute { get; set; }
    public int? ConcurrentRuns { get; set; }
    public long? TokensPerDay { get; set; }
    public int? ToolCallsPerRun { get; set; }
    public double? SpeechSecondsPerDay { get; set; }

    /// <summary>The more generous of two limits per field (a person with several roles gets the best of them).</summary>
    public static QuotaLimits Widest(QuotaLimits a, QuotaLimits b) => new()
    {
        RunsPerMinute = Max(a.RunsPerMinute, b.RunsPerMinute),
        ConcurrentRuns = Max(a.ConcurrentRuns, b.ConcurrentRuns),
        TokensPerDay = a.TokensPerDay is null || b.TokensPerDay is null ? null : Math.Max(a.TokensPerDay.Value, b.TokensPerDay.Value),
        ToolCallsPerRun = Max(a.ToolCallsPerRun, b.ToolCallsPerRun),
        SpeechSecondsPerDay = a.SpeechSecondsPerDay is null || b.SpeechSecondsPerDay is null ? null : Math.Max(a.SpeechSecondsPerDay.Value, b.SpeechSecondsPerDay.Value),
    };

    /// <summary>Fields set in <paramref name="over"/> replace those in <paramref name="baseline"/>.</summary>
    public static QuotaLimits Override(QuotaLimits baseline, QuotaLimits over) => new()
    {
        RunsPerMinute = over.RunsPerMinute ?? baseline.RunsPerMinute,
        ConcurrentRuns = over.ConcurrentRuns ?? baseline.ConcurrentRuns,
        TokensPerDay = over.TokensPerDay ?? baseline.TokensPerDay,
        ToolCallsPerRun = over.ToolCallsPerRun ?? baseline.ToolCallsPerRun,
        SpeechSecondsPerDay = over.SpeechSecondsPerDay ?? baseline.SpeechSecondsPerDay,
    };

    private static int? Max(int? a, int? b) => a is null || b is null ? null : Math.Max(a.Value, b.Value);
}

/// <summary>
/// Quotas (#78): <c>Quotas:Default</c>, per role <c>Quotas:Roles:&lt;role&gt;</c> (the widest of a user's roles applies), per profile
/// <c>Quotas:Profiles:&lt;profile&gt;</c> (caps every user of that profile), and per user overrides set by admins (database).
/// </summary>
public sealed class QuotaOptions
{
    public const string Section = "Quotas";
    public QuotaLimits Default { get; set; } = new() { RunsPerMinute = 20, ConcurrentRuns = 5, ToolCallsPerRun = 40 };
    public Dictionary<string, QuotaLimits> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, QuotaLimits> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Share of a daily budget at which the user and admins are notified once.</summary>
    public double WarnAt { get; set; } = 0.8;
}

public sealed record QuotaUsage(int RunsLastMinute, int ActiveRuns, long TokensToday, double SpeechSecondsToday);

public sealed record QuotaStatus(QuotaLimits Limits, QuotaUsage Usage);

public sealed class QuotaExceededException(string message) : Exception(message);

public sealed class QuotaService(LotsDbContext db, IOptions<QuotaOptions> options, TimeProvider clock)
{
    private readonly QuotaOptions _o = options.Value;

    public async Task<QuotaLimits> LimitsAsync(Principal who, string? profile, CancellationToken ct)
    {
        var limits = who.Roles.Select(r => _o.Roles.GetValueOrDefault(r)).Where(l => l is not null)
            .Aggregate((QuotaLimits?)null, (acc, l) => acc is null ? QuotaLimits.Override(_o.Default, l!) : QuotaLimits.Widest(acc, QuotaLimits.Override(_o.Default, l!)))
            ?? _o.Default;
        if (profile is not null && _o.Profiles.TryGetValue(profile, out var cap))
            limits = new QuotaLimits
            {
                RunsPerMinute = Min(limits.RunsPerMinute, cap.RunsPerMinute),
                ConcurrentRuns = Min(limits.ConcurrentRuns, cap.ConcurrentRuns),
                TokensPerDay = cap.TokensPerDay is null ? limits.TokensPerDay : limits.TokensPerDay is null ? cap.TokensPerDay : Math.Min(limits.TokensPerDay.Value, cap.TokensPerDay.Value),
                ToolCallsPerRun = Min(limits.ToolCallsPerRun, cap.ToolCallsPerRun),
                SpeechSecondsPerDay = limits.SpeechSecondsPerDay,
            };
        var user = await db.QuotaOverrides.AsNoTracking().SingleOrDefaultAsync(q => q.UserId == who.UserId, ct);
        return user is null ? limits : QuotaLimits.Override(limits, user.Limits());
    }

    public async Task<QuotaUsage> UsageAsync(string user, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var midnight = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var minuteAgo = now.AddMinutes(-1);
        var runsLastMinute = await db.Runs.CountAsync(r => r.UserId == user && r.CreatedAt >= minuteAgo, ct);
        var active = await db.Runs.CountAsync(r => r.UserId == user && (r.Status == RunStatus.Pending || r.Status == RunStatus.Running), ct);
        var tokens = await (from s in db.RunSteps
                            join r in db.Runs on s.RunId equals r.Id
                            where r.UserId == user && s.CreatedAt >= midnight && s.Kind == StepKind.ModelCall
                            select (long)((s.PromptTokens ?? 0) + (s.CompletionTokens ?? 0))).SumAsync(ct);
        var speech = await db.VoiceUsage.Where(v => v.UserId == user && v.At >= midnight && v.Outcome == "ok")
            .Select(v => v.AudioSeconds ?? (v.Characters ?? 0) / 15.0).SumAsync(ct); // speech out counted at ~15 characters a second
        return new QuotaUsage(runsLastMinute, active, tokens, Math.Round(speech, 1));
    }

    /// <summary>Before a run starts: throws with a message the user can act on when a limit is reached.</summary>
    public async Task CheckStartAsync(Principal who, string profile, CancellationToken ct)
    {
        var l = await LimitsAsync(who, profile, ct);
        var u = await UsageAsync(who.UserId, ct);
        if (l.RunsPerMinute is { } rpm && u.RunsLastMinute >= rpm) throw new QuotaExceededException($"You started {rpm} runs in the last minute; wait a moment.");
        if (l.ConcurrentRuns is { } c && u.ActiveRuns >= c) throw new QuotaExceededException($"You already have {c} runs in progress; wait for one to finish or cancel one.");
        if (l.TokensPerDay is { } t && u.TokensToday >= t) throw new QuotaExceededException($"Your daily budget of {t:N0} tokens is used up; it resets at midnight UTC.");
        await WarnIfNearAsync(who.UserId, "tokens", u.TokensToday, l.TokensPerDay, ct);
    }

    /// <summary>During a run, before each model call: the daily token budget.</summary>
    public async Task<string?> RunBudgetProblemAsync(Principal who, string profile, int toolCallsSoFar, CancellationToken ct)
    {
        var l = await LimitsAsync(who, profile, ct);
        if (l.ToolCallsPerRun is { } max && toolCallsSoFar > max) return $"Stopped after {max} tool calls in one run (quota).";
        if (l.TokensPerDay is { } t && (await UsageAsync(who.UserId, ct)).TokensToday >= t) return $"Stopped: the daily budget of {t:N0} tokens is used up.";
        return null;
    }

    public async Task CheckSpeechAsync(Principal who, CancellationToken ct)
    {
        var l = await LimitsAsync(who, null, ct);
        if (l.SpeechSecondsPerDay is not { } max) return;
        var used = (await UsageAsync(who.UserId, ct)).SpeechSecondsToday;
        if (used >= max) throw new QuotaExceededException($"Your daily voice budget ({max / 60:0} minutes) is used up; type instead.");
        await WarnIfNearAsync(who.UserId, "speech", used, max, ct);
    }

    /// <summary>One notification per user, budget and day when usage passes the warning share.</summary>
    private async Task WarnIfNearAsync(string user, string budget, double used, double? limit, CancellationToken ct)
    {
        if (limit is not { } max || max <= 0 || used < max * _o.WarnAt) return;
        var day = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd");
        var key = $"\"user\":\"{user}\",\"budget\":\"{budget}\",\"day\":\"{day}\"";
        if (await db.Notifications.AnyAsync(n => n.Event == NotificationEvents.QuotaWarning && n.PayloadJson.Contains(key), ct)) return;
        Outbox.Add(db, NotificationEvents.QuotaWarning, new { user, budget, day, used = Math.Round(used), limit = max, share = Math.Round(used / max, 2) }, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
    }

    private static int? Min(int? a, int? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
}
