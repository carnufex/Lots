using System.Diagnostics;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Speech;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Core.Telemetry;

/// <summary>One dependency's state. Required ones decide readiness; the rest only degrade features.</summary>
public sealed record DependencyStatus(string Name, string Kind, bool Up, bool Required, long LatencyMs, string? Error, DateTimeOffset CheckedAt);

/// <summary>
/// Checks every dependency every 30 s (#82): database, model endpoints, identity provider, tool servers, voice, knowledge.
/// Results feed the admin view, the public status page, and the <c>lots_dependency_up</c> metric that the alert rules use (#83).
/// </summary>
public sealed class DependencyMonitor(IServiceScopeFactory scopes, IHttpClientFactory http, IConfiguration config, IOptions<SpeechOptions> speech,
    TimeProvider clock, ILogger<DependencyMonitor> logger) : BackgroundService
{
    private volatile IReadOnlyList<DependencyStatus> _latest = [];

    public IReadOnlyList<DependencyStatus> Latest => _latest;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { _latest = await CheckAllAsync(stop); }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested) { logger.LogWarning("Dependency check failed: {Error}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(30), stop).ContinueWith(_ => { });
        }
    }

    public async Task<IReadOnlyList<DependencyStatus>> CheckAllAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var list = new List<Task<DependencyStatus>>
        {
            Check("database", "postgres", true, async () =>
            {
                if (!await scope.ServiceProvider.GetRequiredService<LotsDbContext>().Database.CanConnectAsync(ct)) throw new InvalidOperationException("cannot connect");
            }),
        };

        foreach (var e in await scope.ServiceProvider.GetRequiredService<RoutingModelClient>().HealthAsync(ct))
            list.Add(Task.FromResult(new DependencyStatus($"model:{e.Name}", "model", e.Up, false, e.LatencyMs, e.Error, clock.GetUtcNow())));

        if (AuthSetup.IsOidc(config) && config["Auth:Oidc:Authority"] is { Length: > 0 } authority)
            list.Add(Check("identity provider", "oidc", false, async () =>
            {
                using var res = await http.CreateClient(nameof(DependencyMonitor)).GetAsync(authority.TrimEnd('/') + "/.well-known/openid-configuration", ct);
                res.EnsureSuccessStatusCode();
            }));

        if (speech.Value.Enabled)
            list.Add(Check("voice service", "voice", false, async () =>
            {
                var root = new Uri(speech.Value.BaseUrl.TrimEnd('/') + "/");
                using var res = await http.CreateClient(nameof(DependencyMonitor)).GetAsync(new Uri(root, "../health"), ct);
                res.EnsureSuccessStatusCode();
            }));

        foreach (var s in await scope.ServiceProvider.GetRequiredService<ToolInvoker>().ServerStatusAsync(ct))
            if (s.Health != "per-user")
                list.Add(Task.FromResult(new DependencyStatus($"tools:{s.Name}", s.Auth == ServerStatus.BuiltIn ? "builtin" : "mcp", s.Health == "ok", false, 0, s.Error, clock.GetUtcNow())));

        var results = await Task.WhenAll(list);
        foreach (var r in results)
            DependencyGauge.Set(r.Name, r.Kind, r.Up);
        return results;
    }

    private async Task<DependencyStatus> Check(string name, string kind, bool required, Func<Task> probe)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await probe().WaitAsync(timeout.Token);
            return new DependencyStatus(name, kind, true, required, sw.ElapsedMilliseconds, null, clock.GetUtcNow());
        }
        catch (Exception ex)
        {
            return new DependencyStatus(name, kind, false, required, sw.ElapsedMilliseconds, ex is TimeoutException or OperationCanceledException ? "timed out" : ex.Message, clock.GetUtcNow());
        }
    }
}

/// <summary>lots_dependency_up{name,kind} = 1 or 0, for alerting (#83).</summary>
public static class DependencyGauge
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Name, string Kind), int> Values = new();

    static DependencyGauge()
    {
        var meter = new System.Diagnostics.Metrics.Meter(LotsMetrics.MeterName + ".Dependencies");
        meter.CreateObservableGauge("lots.dependency.up", () => Values.Select(kv =>
            new System.Diagnostics.Metrics.Measurement<int>(kv.Value, new("name", kv.Key.Name), new("kind", kv.Key.Kind))), description: "1 when the dependency answers");
    }

    public static void Set(string name, string kind, bool up) => Values[(name, kind)] = up ? 1 : 0;
}
