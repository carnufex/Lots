using FastEndpoints;
using Lots.Shell.Persistence;

namespace Lots.Shell.Features.Health;

public sealed record HealthResponse(string Status);

/// <summary>Liveness: the process is up. Does not touch the database.</summary>
public sealed class LiveEndpoint : EndpointWithoutRequest<HealthResponse>
{
    public override void Configure()
    {
        Get("/health");
        AllowAnonymous();
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Send.OkAsync(new HealthResponse("ok"), ct);
}

/// <summary>Readiness: the database is reachable.</summary>
public sealed class ReadyEndpoint(LotsDbContext db) : EndpointWithoutRequest<HealthResponse>
{
    public override void Configure()
    {
        Get("/health/ready");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (await db.Database.CanConnectAsync(ct))
            await Send.OkAsync(new HealthResponse("ready"), ct);
        else
            await Send.ResponseAsync(new HealthResponse("database unavailable"), 503, ct);
    }
}

/// <summary>Every dependency with latency and the last error (#82). Admins: it names internal endpoints.</summary>
public sealed class DependenciesEndpoint(Lots.Shell.Core.Telemetry.DependencyMonitor monitor, Lots.Shell.Core.Policy.ICurrentPrincipal who, IConfiguration config)
    : EndpointWithoutRequest<IReadOnlyList<Lots.Shell.Core.Telemetry.DependencyStatus>>
{
    public override void Configure() => Get("/health/dependencies");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Lots.Shell.Features.Conversations.ConversationViews.IsAdmin(who.Get(HttpContext), config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var latest = monitor.Latest.Count > 0 ? monitor.Latest : await monitor.CheckAllAsync(ct);
        await Send.OkAsync(latest, ct);
    }
}

public sealed record StatusComponent(string Name, string State);

public sealed record PublicStatus(string Overall, IReadOnlyList<StatusComponent> Components, DateTimeOffset? CheckedAt);

/// <summary>
/// A public status page's data (#83): overall state and coarse components (assistant, models, voice, tools, sign-in), no addresses,
/// errors or names of internal systems.
/// </summary>
public sealed class StatusEndpoint(Lots.Shell.Core.Telemetry.DependencyMonitor monitor) : EndpointWithoutRequest<PublicStatus>
{
    public override void Configure()
    {
        Get("/status");
        AllowAnonymous();
    }

    public override Task HandleAsync(CancellationToken ct)
    {
        var deps = monitor.Latest;
        string State(IEnumerable<Lots.Shell.Core.Telemetry.DependencyStatus> group)
        {
            var g = group.ToList();
            return g.Count == 0 ? "not used" : g.All(d => d.Up) ? "operational" : g.Any(d => d.Up) ? "degraded" : "down";
        }
        var components = new List<StatusComponent>
        {
            new("Assistant", State(deps.Where(d => d.Kind == "postgres"))),
            new("Language models", State(deps.Where(d => d.Kind == "model"))),
            new("Tools and knowledge", State(deps.Where(d => d.Kind is "mcp" or "builtin"))),
            new("Voice", State(deps.Where(d => d.Kind == "voice"))),
            new("Sign-in", State(deps.Where(d => d.Kind == "oidc"))),
        }.Where(c => c.State != "not used").ToList();
        var overall = components.Count == 0 ? "unknown"
            : components.Any(c => c.State == "down" && c.Name is "Assistant" or "Language models") ? "down"
            : components.All(c => c.State == "operational") ? "operational" : "degraded";
        return Send.OkAsync(new PublicStatus(overall, components, deps.Count == 0 ? null : deps.Max(d => d.CheckedAt)), ct);
    }
}
