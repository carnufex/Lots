using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Core.Runs;

/// <summary>
/// Background worker that executes runs. Every replica runs one. A run is executed only while this worker holds
/// its lease (renewed in the background); if the lease is lost the work is cancelled, and if the process dies the
/// lease expires and another replica resumes the run from its persisted state.
/// </summary>
public sealed class RunWorker(IServiceScopeFactory scopes, ILogger<RunWorker> logger) : BackgroundService
{
    public static readonly TimeSpan LeaseTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    /// <summary>How often a worker looks for a cancel request on the run it is executing.</summary>
    private static readonly TimeSpan CancelCheckInterval = TimeSpan.FromSeconds(1);

    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await RunOneAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Run worker iteration failed");
            }

            await Task.Delay(PollInterval, stoppingToken).ContinueWith(_ => { });
        }
    }

    /// <summary>Claims and executes one run. Returns false when nothing was available.</summary>
    private async Task<bool> RunOneAsync(CancellationToken stoppingToken)
    {
        using var scope = scopes.CreateScope();
        var leases = scope.ServiceProvider.GetRequiredService<RunLeases>();

        var id = await leases.ClaimNextAsync(_owner, LeaseTtl, stoppingToken);
        if (id is null) return false;

        // Every log line written while this run executes carries its run, profile and user hash (#138).
        var head = await scope.ServiceProvider.GetRequiredService<Lots.Shell.Persistence.LotsDbContext>().Runs.AsNoTracking()
            .Where(r => r.Id == id.Value).Select(r => new { r.Profile, r.UserId, r.ConversationId }).SingleAsync(stoppingToken);
        using var logScope = Lots.Shell.Core.Telemetry.RunLogScope.Begin(logger, id.Value, head.Profile,
            scope.ServiceProvider.GetRequiredService<Lots.Shell.Core.Profiles.ProfileRegistry>().Find(head.Profile)?.Version, head.UserId, head.ConversationId);
        logger.LogInformation("Claimed run {Run} (lease {LeaseSeconds} s)", id.Value, (int)LeaseTtl.TotalSeconds);
        using var lost = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renewer = Task.Run(() => RenewLoopAsync(id.Value, lost), CancellationToken.None);
        try
        {
            await scope.ServiceProvider.GetRequiredService<AgentRunner>().ExecuteAsync(id.Value, lost.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Stopped on request (or the lease was lost: then the request check is a no-op and another worker continues).
            using var fresh = scopes.CreateScope();
            await fresh.ServiceProvider.GetRequiredService<RunControl>().FinishCancelledAsync(id.Value);
        }
        finally
        {
            await lost.CancelAsync();
            await renewer;
            await leases.ReleaseAsync(id.Value, _owner);
        }
        return true;
    }

    private async Task RenewLoopAsync(Guid runId, CancellationTokenSource lost)
    {
        // Own scope: the renewal runs concurrently with the runner, which owns the other DbContext.
        using var scope = scopes.CreateScope();
        var leases = scope.ServiceProvider.GetRequiredService<RunLeases>();
        var control = scope.ServiceProvider.GetRequiredService<RunControl>();
        var renewEvery = (int)((LeaseTtl / 3) / CancelCheckInterval);
        try
        {
            for (var tick = 1; !lost.IsCancellationRequested; tick++)
            {
                await Task.Delay(CancelCheckInterval, lost.Token);
                if (await control.IsCancelRequestedAsync(runId, lost.Token))
                {
                    logger.LogInformation("Run {Run} was cancelled; stopping work on it", runId);
                    await lost.CancelAsync();
                    break;
                }
                if (tick % renewEvery == 0 && !await leases.RenewAsync(runId, _owner, LeaseTtl, lost.Token))
                {
                    logger.LogWarning("Lost the lease on run {Run}; stopping work on it", runId);
                    await lost.CancelAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal: the run finished or the host is stopping
        }
    }
}
