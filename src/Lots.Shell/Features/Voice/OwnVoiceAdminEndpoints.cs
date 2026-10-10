using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Speech;
using Lots.Shell.Features.Admin;
using Lots.Shell.Features.Settings;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Voice;

public sealed record OwnVoiceDto(string UserId, double Seconds, DateTimeOffset ConsentAt, int RegistrationsLast30Days);

public sealed record ConsentEventDto(string UserId, string? VoiceId, string Event, string Actor, DateTimeOffset At, string? Statement);

public sealed record OwnVoicesDto(IReadOnlyList<OwnVoiceDto> Voices, IReadOnlyList<ConsentEventDto> RecentEvents);

/// <summary>Who has registered their own voice, with the consent trail (#93). Admins; no audio is ever exposed.</summary>
public sealed class ListOwnVoicesEndpoint(LotsDbContext db, IConfiguration config, ICurrentPrincipal who, TimeProvider clock)
    : AdminEndpoint<EmptyRequest, OwnVoicesDto>(config, who)
{
    public override void Configure() => Get("/admin/voices");

    public override async Task HandleAsync(EmptyRequest req, CancellationToken ct)
    {
        if (!await AllowedAsync(ct)) return;
        var since = clock.GetUtcNow().AddDays(-30);
        var voices = await db.UserSettings.AsNoTracking().Where(s => s.VoiceId != null && s.VoiceConsentAt != null).ToListAsync(ct);
        var counts = await db.VoiceConsents.AsNoTracking().Where(c => c.Event == VoiceConsentEvent.Given && c.At > since)
            .GroupBy(c => c.UserId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var events = await db.VoiceConsents.AsNoTracking().OrderByDescending(c => c.At).Take(100).ToListAsync(ct);
        await Send.OkAsync(new OwnVoicesDto(
            voices.Select(s => new OwnVoiceDto(s.UserId, s.VoiceSeconds ?? 0, s.VoiceConsentAt!.Value, counts.GetValueOrDefault(s.UserId))).ToList(),
            events.Select(e => new ConsentEventDto(e.UserId, e.VoiceId, e.Event.ToString(), e.Actor, e.At, e.Statement)).ToList()), ct);
    }
}

public sealed record RevokeVoiceRequest(string UserId);

/// <summary>Revokes a user's own voice (#93): the clip is deleted in the voice service (verified), the reference removed, the revocation logged.</summary>
public sealed class RevokeOwnVoiceEndpoint(LotsDbContext db, IVoiceRegistry registry, IConfiguration config, ICurrentPrincipal who, TimeProvider clock,
    ILogger<RevokeOwnVoiceEndpoint> logger) : AdminEndpoint<RevokeVoiceRequest, EmptyResponse>(config, who)
{
    public override void Configure() => Delete("/admin/voices/{UserId}");

    public override async Task HandleAsync(RevokeVoiceRequest req, CancellationToken ct)
    {
        if (!await AllowedAsync(ct)) return;
        var row = await db.UserSettings.SingleOrDefaultAsync(s => s.UserId == req.UserId, ct);
        if (row?.VoiceId is not { } id)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        try
        {
            await registry.DeleteAsync(id, ct);
            if (await registry.ExistsAsync(id, ct) == true)
            {
                AddError("The voice service still has the recording after deleting it. Try again.");
                await Send.ErrorsAsync(502, ct);
                return;
            }
        }
        catch (SpeechUnavailableException)
        {
            AddError("The voice service is unavailable, so the recording could not be deleted. Try again.");
            await Send.ErrorsAsync(503, ct);
            return;
        }
        var now = clock.GetUtcNow();
        row.VoiceId = null;
        row.VoiceSeconds = null;
        row.VoiceConsentAt = null;
        row.UpdatedAt = now;
        VoiceConsent.Log(db, req.UserId, id, VoiceConsentEvent.Revoked, Me.UserId, now);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Own voice of {User} revoked by admin {Admin}", req.UserId, Me.UserId);
        await Send.NoContentAsync(ct);
    }
}
