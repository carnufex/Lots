using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Core.Runs;

public enum CancelOutcome { NotFound, AlreadyFinished, Cancelled, Requested }

/// <summary>
/// Stopping runs. A run nobody is working on is cancelled at once. A run a worker holds (live lease) only gets the request
/// recorded: the worker watches for it, stops the work in flight and marks the run Cancelled. The decision between the two
/// is a single conditional update, so it cannot race with a worker claiming the run.
/// </summary>
public sealed class RunControl(LotsDbContext db, TimeProvider clock)
{
    public static void MarkCancelled(RunRecord run)
    {
        run.Status = RunStatus.Cancelled;
        run.Error = run.CancelRequestedBy is { } by ? $"Cancelled by {by}." : "Cancelled.";
        run.SubjectTokenProtected = null;
        run.SubjectTokenExpiresAt = null;
    }

    public async Task<CancelOutcome> RequestCancelAsync(Guid runId, string by, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var nowMs = now.ToUnixTimeMilliseconds();
        var error = $"Cancelled by {by}.";

        if (db.Database.IsRelational())
        {
            // Not held by a worker: cancel now. Pending/WaitingForApproval/a crashed worker's Running run all land here.
            var stopped = await db.Runs
                .Where(r => r.Id == runId && Active.Contains(r.Status) && (r.LeaseUntilMs == null || r.LeaseUntilMs < nowMs))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, RunStatus.Cancelled)
                    .SetProperty(r => r.CancelRequestedAt, now)
                    .SetProperty(r => r.CancelRequestedBy, by)
                    .SetProperty(r => r.Error, error)
                    .SetProperty(r => r.SubjectTokenProtected, (string?)null)
                    .SetProperty(r => r.SubjectTokenExpiresAt, (DateTimeOffset?)null)
                    .SetProperty(r => r.UpdatedAt, now), ct);
            if (stopped == 1) return CancelOutcome.Cancelled;

            var requested = await db.Runs
                .Where(r => r.Id == runId && Active.Contains(r.Status))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.CancelRequestedAt, now)
                    .SetProperty(r => r.CancelRequestedBy, by), ct);
            if (requested == 1) return CancelOutcome.Requested;

            return await db.Runs.AnyAsync(r => r.Id == runId, ct) ? CancelOutcome.AlreadyFinished : CancelOutcome.NotFound;
        }

        // Non-relational providers (in-memory tests): single process, no atomic conditional update.
        var run = await db.Runs.SingleOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return CancelOutcome.NotFound;
        if (!Active.Contains(run.Status)) return CancelOutcome.AlreadyFinished;
        run.CancelRequestedAt = now;
        run.CancelRequestedBy = by;
        var held = run.LeaseUntilMs is { } until && until >= nowMs;
        if (!held)
        {
            MarkCancelled(run);
            run.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return held ? CancelOutcome.Requested : CancelOutcome.Cancelled;
    }

    public Task<bool> IsCancelRequestedAsync(Guid runId, CancellationToken ct) =>
        db.Runs.AsNoTracking().AnyAsync(r => r.Id == runId && r.CancelRequestedAt != null, ct);

    /// <summary>After the worker stopped a run on request: record it as Cancelled (no-op if it finished first).</summary>
    public async Task FinishCancelledAsync(Guid runId)
    {
        var run = await db.Runs.SingleOrDefaultAsync(r => r.Id == runId);
        if (run is null || run.CancelRequestedAt is null || !Active.Contains(run.Status)) return;
        MarkCancelled(run);
        run.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync();
    }

    private static readonly RunStatus[] Active = [RunStatus.Pending, RunStatus.Running, RunStatus.WaitingForApproval];
}
