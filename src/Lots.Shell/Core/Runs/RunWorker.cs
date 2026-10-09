using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Core.Runs;

/// <summary>
/// Background worker that executes runs. Picks up Pending runs and Running runs left over from a
/// previous process, so runs survive a restart. M1 assumes a single shell instance.
/// </summary>
public sealed class RunWorker(IServiceScopeFactory scopes, ILogger<RunWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Guid? next;
                using (var scope = scopes.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
                    next = await db.Runs
                        .Where(r => r.Status == RunStatus.Pending || r.Status == RunStatus.Running)
                        .OrderBy(r => r.CreatedAt)
                        .Select(r => (Guid?)r.Id)
                        .FirstOrDefaultAsync(stoppingToken);
                }

                if (next is { } id)
                {
                    using var scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<AgentRunner>().ExecuteAsync(id, stoppingToken);
                    continue;
                }
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
}
