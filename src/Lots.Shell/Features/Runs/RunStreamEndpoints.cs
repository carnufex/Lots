using System.Text.Json;
using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Runs;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Runs;

/// <summary>
/// Live updates of a run as server-sent events (#95): <c>partial</c> with the answer written so far while the model streams,
/// <c>changed</c> when the run's status or steps change (the client then reads <c>GET /runs/{id}</c>), <c>done</c> when it
/// has finished. Works across replicas: the partial text comes from memory on the worker's replica and from the database elsewhere.
/// Read with <c>fetch</c> (EventSource cannot send the Authorization header).
/// </summary>
public sealed class RunEventsEndpoint(LotsDbContext db, RunStreams streams, ICurrentPrincipal who, IConfiguration config, TimeProvider clock)
    : Endpoint<GetRunRequest>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override void Configure() => Get("/runs/{Id}/events");

    public override async Task HandleAsync(GetRunRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == req.Id, ct);
        if (run is null || !RunAccess.CanRead(run, me, config))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var res = HttpContext.Response;
        res.ContentType = "text/event-stream";
        res.Headers["X-Accel-Buffering"] = "no"; // proxies must pass events through at once
        await res.Body.FlushAsync(ct);

        string? lastPartial = null;
        (RunStatus, DateTimeOffset, int)? lastState = null;
        var until = clock.GetUtcNow().AddMinutes(10); // clients reconnect after this
        while (!ct.IsCancellationRequested && clock.GetUtcNow() < until)
        {
            var state = await db.Runs.AsNoTracking().Where(r => r.Id == req.Id)
                .Select(r => new { r.Status, r.UpdatedAt, Steps = r.Steps.Count, r.Partial }).SingleOrDefaultAsync(ct);
            if (state is null) break;

            var key = (state.Status, state.UpdatedAt, state.Steps);
            if (lastState != key)
            {
                lastState = key;
                await SendAsync("changed", new { status = state.Status.ToString(), steps = state.Steps }, ct);
            }
            var partial = streams.Get(req.Id)?.Text ?? state.Partial;
            if (partial is not null && partial != lastPartial)
            {
                lastPartial = partial;
                await SendAsync("partial", new { text = partial }, ct);
            }
            else if (partial is null) lastPartial = null;

            if (state.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled)
            {
                await SendAsync("done", new { status = state.Status.ToString() }, ct);
                break;
            }
            await Task.Delay(streams.Get(req.Id) is null ? 400 : 120, ct).ContinueWith(_ => { });
        }
    }

    private async Task SendAsync(string name, object data, CancellationToken ct)
    {
        await HttpContext.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, Json)}\n\n", ct);
        await HttpContext.Response.Body.FlushAsync(ct);
    }
}

public sealed record RegenerateRequest(Guid Id, string? Prompt = null);

/// <summary>
/// Regenerate or edit the last turn of a conversation (#95): a new run with the same (or the edited) prompt replaces the old one
/// in the conversation, which is then no longer given to the model as context. Only by the user who asked, only the latest turn.
/// </summary>
public sealed class RegenerateRunEndpoint(LotsDbContext db, TimeProvider clock, ProfileRegistry profiles, IConfiguration config, ICurrentPrincipal who,
    Core.Mcp.SubjectTokenVault vault) : Endpoint<RegenerateRequest, StartRunResponse>
{
    public override void Configure() => Post("/runs/{Id}/regenerate");

    public override async Task HandleAsync(RegenerateRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var old = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == req.Id, ct);
        if (old is null || !RunAccess.CanRead(old, me, config))
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (old.UserId != me.UserId) AddError("Only the user who asked can regenerate or edit a turn.");
        if (old.Status is not (RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled)) AddError("The run has not finished yet; stop it first.");
        if (req.Prompt is { } p && string.IsNullOrWhiteSpace(p)) AddError(x => x.Prompt!, "The edited prompt is empty.");
        if (old.ConversationId is { } conv && await db.Runs.AnyAsync(r => r.ConversationId == conv && r.CreatedAt > old.CreatedAt && r.RetryOf != old.Id, ct))
            AddError("Only the latest turn of a conversation can be regenerated or edited.");
        if (await db.Runs.AnyAsync(r => r.RetryOf == old.Id, ct)) AddError("This turn was already regenerated.");
        if (profiles.Find(old.Profile) is not { } profile)
        {
            AddError($"The run's profile '{old.Profile}' no longer exists.");
            await Send.ErrorsAsync(409, ct);
            return;
        }
        if (ValidationFailed)
        {
            await Send.ErrorsAsync(old.UserId != me.UserId ? 403 : 409, ct);
            return;
        }

        var run = RunFactory.Create(HttpContext, me, clock.GetUtcNow(), profile, config, vault, req.Prompt?.Trim() ?? old.Prompt, old.Voice,
            old.ConversationId, retryOf: old.Id);
        run.AttachmentsJson = old.AttachmentsJson; // the same files go with the redone question
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        await Send.ResponseAsync(new StartRunResponse(run.Id, run.Status.ToString()), 202, ct);
    }
}
