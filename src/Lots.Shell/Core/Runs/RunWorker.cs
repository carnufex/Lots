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

        using var lost = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var renewer = Task.Run(() => RenewLoopAsync(id.Value, lost), CancellationToken.None);
        try
        {
            await scope.ServiceProvider.GetRequiredService<AgentRunner>().ExecuteAsync(id.Value, lost.Token);
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
        try
        {
            while (!lost.IsCancellationRequested)
            {
                await Task.Delay(LeaseTtl / 3, lost.Token);
                if (!await leases.RenewAsync(runId, _owner, LeaseTtl, lost.Token))
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
