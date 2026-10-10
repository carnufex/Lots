using Lots.Shell.Core.Outcomes;
using Lots.Shell.Features.Insights;
using Lots.Shell.Features.Usage;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Tests.Features;

/// <summary>#141: one outcome row per finished run, with the signals the self-improvement loop needs, and no content.</summary>
public class RunOutcomeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static LotsDbContext Db(string name) => new(new DbContextOptionsBuilder<LotsDbContext>().UseInMemoryDatabase(name).Options);

    private static PriceTable Prices() => new(new ConfigurationBuilder().Build());

    private static RunRecord Run(RunStatus status, string prompt = "Is postgres up?", Guid? conversation = null, string? error = null, string? answer = "It is.",
        int minute = 0)
    {
        var id = Guid.NewGuid();
        return new RunRecord
        {
            Id = id, Prompt = prompt, Profile = TestProfiles.Name, UserId = "alice", Roles = "operator", Status = status, Error = error,
            FinalAnswer = status == RunStatus.Completed ? answer : null, ConversationId = conversation,
            CreatedAt = T0.AddMinutes(minute), UpdatedAt = T0.AddMinutes(minute).AddSeconds(3),
            Steps =
            [
                new RunStepRecord { RunId = id, Seq = 1, Kind = StepKind.ModelCall, Name = "qwen", LatencyMs = 900, PromptTokens = 100, CompletionTokens = 20, CreatedAt = T0 },
                new RunStepRecord { RunId = id, Seq = 2, Kind = StepKind.ToolCall, Name = "list_containers", LatencyMs = 40, Result = "lots-postgres-1 healthy", CreatedAt = T0 },
                new RunStepRecord { RunId = id, Seq = 3, Kind = StepKind.ToolCall, Name = "get_logs", LatencyMs = 30, Result = "Error: no such container", CreatedAt = T0 },
            ],
        };
    }

    private static async Task<Dictionary<Guid, RunOutcomeRecord>> Compute(LotsDbContext db, params RunRecord[] runs)
    {
        db.Runs.AddRange(runs);
        await db.SaveChangesAsync();
        await RunOutcomes.ComputeAsync(db, runs.Select(r => r.Id).ToList(), TestProfiles.Registry(), Prices(), new OutcomeOptions(), TimeProvider.System, default);
        return await db.RunOutcomes.ToDictionaryAsync(o => o.RunId);
    }

    [Fact]
    public async Task Completed_failed_cancelled_and_refused_runs_each_get_an_outcome()
    {
        using var db = Db(nameof(Completed_failed_cancelled_and_refused_runs_each_get_an_outcome));
        var done = Run(RunStatus.Completed);
        var failed = Run(RunStatus.Failed, error: "Timed out after 300 s.");
        var cancelled = Run(RunStatus.Cancelled);
        var refused = Run(RunStatus.Completed, answer: "I can't do that: the approval was denied.");
        db.AuditLog.Add(new AuditRecord { Id = Guid.NewGuid(), RunId = refused.Id, UserId = "alice", Roles = "operator", Profile = TestProfiles.Name, Tool = "restart",
            Decision = AuditDecision.ApprovalDenied, Reason = "denied", At = T0 });

        var o = await Compute(db, done, failed, cancelled, refused);

        Assert.Equal(4, o.Count);
        Assert.Equal(("Completed", 1, 2, 1, 120), (o[done.Id].Status, o[done.Id].ModelCalls, o[done.Id].ToolCalls, o[done.Id].ToolErrors, o[done.Id].TokensIn + o[done.Id].TokensOut));
        Assert.Equal("tool_error", RunOutcomes.Problem(o[done.Id]));
        Assert.True(o[failed.Id].TimedOut);
        Assert.Equal("timeout", RunOutcomes.Problem(o[failed.Id]));
        Assert.Equal("cancelled", RunOutcomes.Problem(o[cancelled.Id]));
        Assert.Equal(1, o[refused.Id].ApprovalRefusals);
        Assert.True(o[refused.Id].Refused);
        Assert.Equal("approval_refused", RunOutcomes.Problem(o[refused.Id]));
        Assert.All(o.Values, x => Assert.NotEqual("alice", x.UserHash)); // never the user id in clear
        Assert.Contains("\"get_logs\":{\"calls\":1,\"errors\":1}", o[done.Id].ToolsJson);
    }

    [Fact]
    public async Task Retries_follow_ups_and_feedback_are_signals()
    {
        using var db = Db(nameof(Retries_follow_ups_and_feedback_are_signals));
        var conv = Guid.NewGuid();
        var first = Run(RunStatus.Completed, conversation: conv);
        var correction = Run(RunStatus.Completed, "No, the other database", conversation: conv, minute: 1); // within the follow-up window
        var again = Run(RunStatus.Completed, "  is postgres up? ", minute: 30);
        db.Feedback.Add(new FeedbackRecord { Id = Guid.NewGuid(), RunId = first.Id, UserId = "alice", Rating = -1, CreatedAt = T0, UpdatedAt = T0 });

        var o = await Compute(db, first, correction, again);

        Assert.True(o[first.Id].FollowUp);
        Assert.True(o[first.Id].UserRetried); // the same prompt again later
        Assert.Equal(-1, o[first.Id].FeedbackRating);
        Assert.False(o[again.Id].UserRetried);
        Assert.False(o[correction.Id].FollowUp);
    }

    [Fact]
    public void Aggregates_give_success_rate_and_p95()
    {
        var rows = Enumerable.Range(1, 20).Select(i => new RunOutcomeRecord
        {
            RunId = Guid.NewGuid(), UserHash = "h", Profile = "p", Channel = "web", Status = i <= 15 ? "Completed" : "Failed", WallMs = i * 100,
            FeedbackRating = i == 1 ? -1 : null,
        }).ToList();

        var g = OutcomesEndpoint.Aggregate("p v1", rows);

        Assert.Equal(0.7, g.SuccessRate); // 15 completed, one of them rated bad
        Assert.Equal(1000, g.P50WallMs);
        Assert.Equal(1900, g.P95WallMs);
        Assert.Equal(5, g.Problems["failed"]);
    }

    [Fact]
    public async Task The_worker_backfills_finished_runs_and_recomputes_dirty_ones()
    {
        var name = nameof(The_worker_backfills_finished_runs_and_recomputes_dirty_ones);
        var services = new ServiceCollection()
            .AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(name))
            .AddSingleton(TestProfiles.Registry())
            .AddSingleton(Prices())
            .BuildServiceProvider();
        var worker = new RunOutcomeWorker(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new OutcomeOptions()), TimeProvider.System,
            NullLogger<RunOutcomeWorker>.Instance);
        Guid done;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
            var r = Run(RunStatus.Completed);
            done = r.Id;
            db.Runs.AddRange(r, Run(RunStatus.Running));
            await db.SaveChangesAsync();
        }

        await worker.RunOnceAsync(default);
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
            var only = Assert.Single(await db.RunOutcomes.ToListAsync()); // the running one is not finished yet
            Assert.Equal(done, only.RunId);
            db.Feedback.Add(new FeedbackRecord { Id = Guid.NewGuid(), RunId = done, UserId = "alice", Rating = 1, CreatedAt = T0, UpdatedAt = T0 });
            only.Dirty = true;
            await db.SaveChangesAsync();
        }

        await worker.RunOnceAsync(default);
        using (var scope = services.CreateScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<LotsDbContext>().RunOutcomes.SingleAsync();
            Assert.Equal(1, outcome.FeedbackRating);
            Assert.False(outcome.Dirty);
        }
    }
}
