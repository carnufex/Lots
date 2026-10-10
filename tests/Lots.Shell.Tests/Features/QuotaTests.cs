using Lots.Shell.Core.Notifications;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Quotas;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Lots.Shell.Tests.Features;

public class QuotaTests
{
    private static (LotsDbContext Db, QuotaService Quotas, FakeTimeProvider Clock) Setup(QuotaOptions options)
    {
        var db = new LotsDbContext(new DbContextOptionsBuilder<LotsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        return (db, new QuotaService(db, Options.Create(options), clock), clock);
    }

    private static void Tokens(LotsDbContext db, string user, int tokens, DateTimeOffset at)
    {
        var run = new RunRecord { Id = Guid.NewGuid(), Prompt = "p", Profile = "p", UserId = user, Status = RunStatus.Completed, CreatedAt = at.AddHours(-2), UpdatedAt = at };
        db.Runs.Add(run);
        db.RunSteps.Add(new RunStepRecord { RunId = run.Id, Seq = 0, Kind = StepKind.ModelCall, Name = "m", PromptTokens = tokens, CompletionTokens = 0, CreatedAt = at });
        db.SaveChanges();
    }

    [Fact]
    public async Task The_widest_role_applies_profiles_cap_it_and_user_overrides_win()
    {
        var (db, quotas, _) = Setup(new QuotaOptions
        {
            Default = new QuotaLimits { RunsPerMinute = 5, TokensPerDay = 1000 },
            Roles = { ["power"] = new QuotaLimits { TokensPerDay = 100_000 }, ["ops"] = new QuotaLimits { TokensPerDay = 5000 } },
            Profiles = { ["cheap"] = new QuotaLimits { TokensPerDay = 2000 } },
        });
        var p = new Principal("alice", ["ops", "power"]);

        Assert.Equal(100_000, (await quotas.LimitsAsync(p, null, default)).TokensPerDay);
        Assert.Equal(2000, (await quotas.LimitsAsync(p, "cheap", default)).TokensPerDay);
        Assert.Equal(5, (await quotas.LimitsAsync(p, null, default)).RunsPerMinute);

        db.QuotaOverrides.Add(new QuotaOverrideRecord { UserId = "alice", TokensPerDay = 7 });
        await db.SaveChangesAsync();
        Assert.Equal(7, (await quotas.LimitsAsync(p, null, default)).TokensPerDay);
    }

    [Fact]
    public async Task A_used_up_daily_budget_blocks_new_runs_and_stops_running_ones_and_warns_once_at_80_percent()
    {
        var (db, quotas, clock) = Setup(new QuotaOptions { Default = new QuotaLimits { TokensPerDay = 1000, ToolCallsPerRun = 3 } });
        var p = new Principal("bob", []);
        Tokens(db, "bob", 500, clock.GetUtcNow().AddDays(-1)); // yesterday does not count
        Tokens(db, "bob", 850, clock.GetUtcNow());

        await quotas.CheckStartAsync(p, "p", default);
        await quotas.CheckStartAsync(p, "p", default);
        Assert.Single(db.Notifications.Where(n => n.Event == NotificationEvents.QuotaWarning));

        Tokens(db, "bob", 200, clock.GetUtcNow());
        var ex = await Assert.ThrowsAsync<QuotaExceededException>(() => quotas.CheckStartAsync(p, "p", default));
        Assert.Contains("daily budget", ex.Message);
        Assert.Contains("tokens is used up", await quotas.RunBudgetProblemAsync(p, "p", 0, default));
        Assert.Contains("tool calls", (await Setup(new QuotaOptions { Default = new QuotaLimits { ToolCallsPerRun = 3 } }).Quotas.RunBudgetProblemAsync(p, "p", 4, default))!);

        clock.Advance(TimeSpan.FromDays(1)); // a new day
        await quotas.CheckStartAsync(p, "p", default);
    }

    [Fact]
    public async Task Too_many_runs_at_once_or_per_minute_are_refused()
    {
        var (db, quotas, clock) = Setup(new QuotaOptions { Default = new QuotaLimits { RunsPerMinute = 2, ConcurrentRuns = 10 } });
        var p = new Principal("carol", []);
        for (var i = 0; i < 2; i++)
            db.Runs.Add(new RunRecord { Id = Guid.NewGuid(), Prompt = "p", UserId = "carol", Status = RunStatus.Completed, CreatedAt = clock.GetUtcNow().AddSeconds(-10) });
        await db.SaveChangesAsync();

        Assert.Contains("last minute", (await Assert.ThrowsAsync<QuotaExceededException>(() => quotas.CheckStartAsync(p, "p", default))).Message);
        clock.Advance(TimeSpan.FromMinutes(1));
        await quotas.CheckStartAsync(p, "p", default);
    }
}
