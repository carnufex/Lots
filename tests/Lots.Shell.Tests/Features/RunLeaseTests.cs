using Lots.Shell.Core.Runs;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Time.Testing;

namespace Lots.Shell.Tests.Features;

/// <summary>Runs against a real relational database (SQLite file) so the atomic conditional UPDATE is exercised.</summary>
public sealed class RunLeaseTests : IDisposable
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"lots-leases-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _clock = new();

    /// <summary>SQLite cannot order DateTimeOffset columns, so tests store them as binary values.</summary>
    private sealed class SqliteLotsDbContext(DbContextOptions<LotsDbContext> options) : LotsDbContext(options)
    {
        protected override void ConfigureConventions(ModelConfigurationBuilder builder) =>
            builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    private LotsDbContext Db()
    {
        var db = new SqliteLotsDbContext(new DbContextOptionsBuilder<LotsDbContext>().UseSqlite($"Data Source={_file}").Options);
        db.Database.EnsureCreated();
        return db;
    }

    private async Task<List<Guid>> SeedAsync(int count, RunStatus status = RunStatus.Pending)
    {
        using var db = Db();
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var run = new RunRecord
            {
                Id = Guid.NewGuid(), Prompt = $"run {i}", Status = status, Profile = "p", UserId = "u",
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(i),
            };
            db.Runs.Add(run);
            ids.Add(run.Id);
        }
        await db.SaveChangesAsync();
        return ids;
    }

    private RunLeases Leases(LotsDbContext db) => new(db, _clock);

    [Fact]
    public async Task Many_workers_racing_never_claim_the_same_run_twice()
    {
        var ids = await SeedAsync(24);

        var claims = await Task.WhenAll(Enumerable.Range(0, 6).Select(w => Task.Run(async () =>
        {
            using var db = Db();
            var mine = new List<Guid>();
            // Each worker takes at most 4 of the 24 runs, so the work has to be shared by all six.
            while (mine.Count < 4 && await Leases(db).ClaimNextAsync($"worker-{w}", Ttl, default) is { } id) mine.Add(id);
            return mine;
        })));

        var all = claims.SelectMany(c => c).ToList();
        Assert.Equal(ids.Count, all.Count);              // every run claimed...
        Assert.Equal(ids.Count, all.Distinct().Count()); // ...exactly once
        Assert.All(claims, c => Assert.Equal(4, c.Count)); // and the work really was shared
    }

    [Fact]
    public async Task A_live_lease_blocks_others_and_an_expired_lease_is_taken_over()
    {
        var id = (await SeedAsync(1)).Single();
        using var a = Db();
        using var b = Db();

        Assert.Equal(id, await Leases(a).ClaimNextAsync("a", Ttl, default));
        Assert.Null(await Leases(b).ClaimNextAsync("b", Ttl, default));          // a's lease is live

        _clock.Advance(Ttl + TimeSpan.FromSeconds(1));                            // a "crashed": never renewed
        Assert.Equal(id, await Leases(b).ClaimNextAsync("b", Ttl, default));      // b resumes the run

        Assert.False(await Leases(a).RenewAsync(id, "a", Ttl, default));          // a has lost it and must stop
        Assert.True(await Leases(b).RenewAsync(id, "b", Ttl, default));
    }

    [Fact]
    public async Task Renewing_keeps_the_lease_alive_past_its_original_expiry()
    {
        var id = (await SeedAsync(1)).Single();
        using var a = Db();
        using var b = Db();
        await Leases(a).ClaimNextAsync("a", Ttl, default);

        _clock.Advance(TimeSpan.FromSeconds(20));
        Assert.True(await Leases(a).RenewAsync(id, "a", Ttl, default));
        _clock.Advance(TimeSpan.FromSeconds(20)); // 40 s after the claim, 20 s after the renewal

        Assert.Null(await Leases(b).ClaimNextAsync("b", Ttl, default));
    }

    [Fact]
    public async Task Release_lets_the_next_worker_continue_immediately_and_finished_runs_are_never_claimed()
    {
        var running = (await SeedAsync(1)).Single();
        await SeedAsync(2, RunStatus.Completed);
        await SeedAsync(1, RunStatus.WaitingForApproval);
        using var a = Db();
        using var b = Db();

        Assert.Equal(running, await Leases(a).ClaimNextAsync("a", Ttl, default));
        await Leases(a).ReleaseAsync(running, "a");

        Assert.Equal(running, await Leases(b).ClaimNextAsync("b", Ttl, default));
        Assert.Null(await Leases(a).ClaimNextAsync("a", Ttl, default)); // nothing else is runnable
    }

    [Fact]
    public async Task Only_the_owner_can_release()
    {
        var id = (await SeedAsync(1)).Single();
        using var a = Db();
        using var b = Db();
        await Leases(a).ClaimNextAsync("a", Ttl, default);

        await Leases(b).ReleaseAsync(id, "b"); // not the owner: no effect

        Assert.Null(await Leases(b).ClaimNextAsync("b", Ttl, default));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_file); } catch (IOException) { /* temp file, best effort */ }
    }
}
