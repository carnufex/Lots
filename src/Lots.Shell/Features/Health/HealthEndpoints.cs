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
