using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Core.Runs;

/// <summary>
/// Leases make a run belong to exactly one worker at a time. A lease is taken with a single atomic
/// conditional UPDATE, so concurrent workers (other replicas) cannot both win, and it expires on its own, so a
/// crashed worker's run is picked up by another instance.
/// </summary>
public sealed class RunLeases(LotsDbContext db, TimeProvider clock)
{
    private long NowMs => clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Claims the oldest runnable run (Pending/Running without a live lease). Returns its id, or null.</summary>
    public async Task<Guid?> ClaimNextAsync(string owner, TimeSpan ttl, CancellationToken ct)
    {
        // Other workers may win every candidate we saw while more runs are waiting: look again before giving up.
        for (var round = 0; round < 10; round++)
        {
            var now = NowMs;
            var candidates = await db.Runs.AsNoTracking()
                .Where(r => (r.Status == RunStatus.Pending || r.Status == RunStatus.Running)
                            && (r.LeaseUntilMs == null || r.LeaseUntilMs < now))
                .OrderBy(r => r.CreatedAt)
                .Select(r => r.Id)
                .Take(5)
                .ToListAsync(ct);

            if (candidates.Count == 0) return null;

            foreach (var id in candidates)
                if (await TryClaimAsync(id, owner, ttl, ct))
                    return id;
        }
        return null;
    }

    /// <summary>Extends a lease the owner still holds. False means the lease was lost and the work must stop.</summary>
    public async Task<bool> RenewAsync(Guid runId, string owner, TimeSpan ttl, CancellationToken ct)
    {
        var until = NowMs + (long)ttl.TotalMilliseconds;
        if (db.Database.IsRelational())
            return await db.Runs.Where(r => r.Id == runId && r.LeaseOwner == owner)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.LeaseUntilMs, until), ct) == 1;

        var run = await db.Runs.SingleOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null || run.LeaseOwner != owner) return false;
        run.LeaseUntilMs = until;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Gives the lease up so another worker (or this one later) can continue immediately.</summary>
    public async Task ReleaseAsync(Guid runId, string owner)
    {
        if (db.Database.IsRelational())
        {
            await db.Runs.Where(r => r.Id == runId && r.LeaseOwner == owner)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.LeaseOwner, (string?)null).SetProperty(r => r.LeaseUntilMs, (long?)null));
            return;
        }

        var run = await db.Runs.SingleOrDefaultAsync(r => r.Id == runId);
        if (run is null || run.LeaseOwner != owner) return;
        run.LeaseOwner = null;
        run.LeaseUntilMs = null;
        await db.SaveChangesAsync();
    }

    private async Task<bool> TryClaimAsync(Guid id, string owner, TimeSpan ttl, CancellationToken ct)
    {
        var now = NowMs;
        var until = now + (long)ttl.TotalMilliseconds;

        if (db.Database.IsRelational())
            return await db.Runs
                .Where(r => r.Id == id
                            && (r.Status == RunStatus.Pending || r.Status == RunStatus.Running)
                            && (r.LeaseUntilMs == null || r.LeaseUntilMs < now))
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.LeaseOwner, owner).SetProperty(r => r.LeaseUntilMs, until), ct) == 1;

        // Non-relational providers (in-memory tests) have no atomic conditional update: single process only.
        var run = await db.Runs.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (run is null || run.Status is not (RunStatus.Pending or RunStatus.Running) || (run.LeaseUntilMs ?? 0) >= now && run.LeaseUntilMs is not null)
            return false;
        run.LeaseOwner = owner;
        run.LeaseUntilMs = until;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
