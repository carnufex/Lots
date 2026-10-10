using System.Diagnostics.Metrics;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Core.Telemetry;

/// <summary>
/// The shell's metrics (#75), exported at <c>/metrics</c> (Prometheus) and via OTLP when configured. Labels are low-cardinality and
/// never identify a person: profile, model, endpoint, tool, status. Durations are in seconds.
/// </summary>
public static class LotsMetrics
{
    public const string MeterName = "Lots.Shell";
    private static readonly Meter Meter = new(MeterName, "1.0");

    private static readonly double[] LatencyBuckets = [0.05, 0.1, 0.25, 0.5, 1, 2, 4, 8, 15, 30, 60, 120];

    public static readonly Counter<long> RunsStarted = Meter.CreateCounter<long>("lots.runs.started", description: "Runs started");
    public static readonly Counter<long> RunsFinished = Meter.CreateCounter<long>("lots.runs.finished", description: "Runs that reached a final status");
    public static readonly Histogram<double> RunDuration = Meter.CreateHistogram("lots.run.duration", "s", "Wall time from start to final status",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = LatencyBuckets });

    public static readonly Counter<long> ModelCalls = Meter.CreateCounter<long>("lots.model.calls", description: "Model calls by model, endpoint and outcome");
    public static readonly Histogram<double> ModelLatency = Meter.CreateHistogram("lots.model.duration", "s", "Model call latency",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = LatencyBuckets });
    public static readonly Counter<long> ModelTokens = Meter.CreateCounter<long>("lots.model.tokens", description: "Tokens by model and kind (prompt, completion)");

    public static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>("lots.tool.calls", description: "Tool calls by tool, policy decision and result");
    public static readonly Histogram<double> ToolLatency = Meter.CreateHistogram("lots.tool.duration", "s", "Tool call latency",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = LatencyBuckets });

    public static readonly Counter<long> Approvals = Meter.CreateCounter<long>("lots.approvals", description: "Approval events: requested, approved, denied, expired");

    public static readonly Counter<long> SpeechRequests = Meter.CreateCounter<long>("lots.speech.requests", description: "Speech calls by direction (stt, tts) and outcome");
    public static readonly Histogram<double> SpeechLatency = Meter.CreateHistogram("lots.speech.duration", "s", "Speech latency (stt: transcription, tts: first audio)",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = LatencyBuckets });

    public static readonly Counter<long> KnowledgeSearches = Meter.CreateCounter<long>("lots.knowledge.searches", description: "Knowledge searches");
    public static readonly Histogram<double> KnowledgeLatency = Meter.CreateHistogram("lots.knowledge.duration", "s", "Knowledge search latency incl. embedding",
        advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = LatencyBuckets });

    /// <summary>Gauges read from the database by <see cref="QueueGauges"/> (refreshed every 15 s, not per scrape).</summary>
    public static void RegisterGauges(QueueGauges gauges)
    {
        Meter.CreateObservableGauge("lots.runs.active", () => gauges.Runs.Select(kv => new Measurement<long>(kv.Value, new KeyValuePair<string, object?>("status", kv.Key))),
            description: "Runs that are not finished, by status");
        Meter.CreateObservableGauge("lots.approvals.pending", () => gauges.PendingApprovals, description: "Approvals waiting for a decision");
        Meter.CreateObservableGauge("lots.approvals.oldest_pending_age", () => gauges.OldestPendingSeconds, "s", "Age of the oldest pending approval");
        Meter.CreateObservableGauge("lots.notifications.undelivered", () => gauges.UndeliveredNotifications, description: "Notifications not yet delivered");
        Meter.CreateObservableGauge("lots.runs.stuck", () => gauges.StuckRuns, description: "Running runs whose lease expired (no worker is executing them)");
    }
}

/// <summary>Database-backed gauge values, refreshed in the background so scrapes never hit the database.</summary>
public sealed class QueueGauges
{
    public Dictionary<string, long> Runs { get; set; } = [];
    public long PendingApprovals { get; set; }
    public double OldestPendingSeconds { get; set; }
    public long UndeliveredNotifications { get; set; }
    public long StuckRuns { get; set; }
}

public sealed class QueueGaugeWorker(IServiceScopeFactory scopes, QueueGauges gauges, TimeProvider clock, ILogger<QueueGaugeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
                var now = clock.GetUtcNow();
                var nowMs = now.ToUnixTimeMilliseconds();
                gauges.Runs = (await (db.Runs.Where(r => r.Status == RunStatus.Pending || r.Status == RunStatus.Running || r.Status == RunStatus.WaitingForApproval)
                            .GroupBy(r => r.Status).Select(g => new { g.Key, Count = g.LongCount() })).ToListAsync(stop))
                    .ToDictionary(x => x.Key.ToString(), x => x.Count);
                var pending = await (db.Approvals.Where(a => a.Status == ApprovalStatus.Pending).Select(a => a.RequestedAt)).ToListAsync(stop);
                gauges.PendingApprovals = pending.Count;
                gauges.OldestPendingSeconds = pending.Count == 0 ? 0 : (now - pending.Min()).TotalSeconds;
                gauges.UndeliveredNotifications = await (db.Notifications.Where(n => n.SentAt == null)).LongCountAsync(stop);
                gauges.StuckRuns = await (db.Runs.Where(r => r.Status == RunStatus.Running && (r.LeaseUntilMs == null || r.LeaseUntilMs < nowMs - 60_000))).LongCountAsync(stop);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                logger.LogDebug("Gauge refresh failed: {Error}", ex.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(15), stop).ContinueWith(_ => { });
        }
    }
}
