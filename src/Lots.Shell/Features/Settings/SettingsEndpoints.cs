using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Speech;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Features.Settings;

public sealed record VoiceInfo(double Seconds, DateTimeOffset ConsentAt);

public sealed record SettingsDto(
    int Talkativeness, int Warmth, int Formality, int Expressiveness, int Pace, VoiceInfo? OwnVoice, bool VoiceEnabled);

public sealed record SetSettingsRequest(int Talkativeness, int Warmth, int Formality, int Expressiveness, int Pace);

/// <summary>How the agent speaks to a user: instructions added to the system prompt. Never touches policy, tools or approvals.</summary>
public static class Persona
{
    public static string? Instructions(UserSettingsRecord? s)
    {
        if (s is null) return null;
        var lines = new List<string>();
        if (s.Talkativeness <= 25) lines.Add("Be very brief: answer in one or two short sentences unless asked for detail.");
        else if (s.Talkativeness <= 40) lines.Add("Keep answers short and to the point.");
        else if (s.Talkativeness >= 75) lines.Add("Be talkative and thorough: give context, explain your reasoning briefly and suggest relevant follow-ups.");
        else if (s.Talkativeness >= 60) lines.Add("Give a little more context than strictly needed.");

        if (s.Warmth <= 25) lines.Add("Be neutral and matter-of-fact; no small talk or enthusiasm.");
        else if (s.Warmth >= 75) lines.Add("Be warm, friendly and encouraging; show some personality and natural emotion.");
        else if (s.Warmth >= 60) lines.Add("Be friendly.");

        if (s.Formality <= 25) lines.Add("Use a casual, relaxed tone.");
        else if (s.Formality >= 75) lines.Add("Use a formal, professional tone.");

        return lines.Count == 0 ? null : "Style preferences from the user (they never override your rules or tool policy): " + string.Join(" ", lines);
    }
}

public static class UserSettings
{
    public static Task<UserSettingsRecord?> OfAsync(LotsDbContext db, string userId, CancellationToken ct) =>
        db.UserSettings.AsNoTracking().SingleOrDefaultAsync(s => s.UserId == userId, ct);

    public static SpeechVoice VoiceOf(UserSettingsRecord? s) =>
        s is null ? new SpeechVoice() : new SpeechVoice(s.VoiceId, s.Expressiveness / 100.0, s.Pace / 100.0);

    public static int Clamp(int v) => Math.Clamp(v, 0, 100);

    public static SettingsDto ToDto(UserSettingsRecord? s, bool voiceEnabled) => new(
        s?.Talkativeness ?? 50, s?.Warmth ?? 50, s?.Formality ?? 50, s?.Expressiveness ?? 60, s?.Pace ?? 40,
        s is { VoiceId: not null, VoiceConsentAt: { } at } ? new VoiceInfo(s.VoiceSeconds ?? 0, at) : null, voiceEnabled);

    public static async Task<UserSettingsRecord> GetOrAddAsync(LotsDbContext db, string userId, CancellationToken ct)
    {
        var row = await db.UserSettings.SingleOrDefaultAsync(s => s.UserId == userId, ct);
        if (row is null) db.UserSettings.Add(row = new UserSettingsRecord { UserId = userId });
        return row;
    }
}

public sealed class GetSettingsEndpoint(LotsDbContext db, ICurrentPrincipal who, IOptions<SpeechOptions> speech)
    : EndpointWithoutRequest<SettingsDto>
{
    public override void Configure() => Get("/me/settings");

    public override async Task HandleAsync(CancellationToken ct) =>
        await Send.OkAsync(UserSettings.ToDto(await UserSettings.OfAsync(db, who.Get(HttpContext).UserId, ct), speech.Value.Enabled), ct);
}

public sealed class SetSettingsEndpoint(LotsDbContext db, ICurrentPrincipal who, IOptions<SpeechOptions> speech, TimeProvider clock)
    : Endpoint<SetSettingsRequest, SettingsDto>
{
    public override void Configure() => Put("/me/settings");

    public override async Task HandleAsync(SetSettingsRequest req, CancellationToken ct)
    {
        var row = await UserSettings.GetOrAddAsync(db, who.Get(HttpContext).UserId, ct);
        row.Talkativeness = UserSettings.Clamp(req.Talkativeness);
        row.Warmth = UserSettings.Clamp(req.Warmth);
        row.Formality = UserSettings.Clamp(req.Formality);
        row.Expressiveness = UserSettings.Clamp(req.Expressiveness);
        row.Pace = UserSettings.Clamp(req.Pace);
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(UserSettings.ToDto(row, speech.Value.Enabled), ct);
    }
}

public sealed class RecordVoiceRequest
{
    public IFormFile? Audio { get; set; }

    /// <summary>The user confirms that the recording is their own voice. Required.</summary>
    public bool Consent { get; set; }
}

/// <summary>
/// Registers the caller's own voice (ADR 0015): a 10-30 s recording that the agent then uses when speaking to them.
/// Only the caller's own recording, only after explicit consent, only for the caller's own conversations.
/// </summary>
public sealed class RecordVoiceEndpoint(
    IVoiceRegistry registry, IOptions<SpeechOptions> speech, LotsDbContext db, ICurrentPrincipal who, TimeProvider clock)
    : Endpoint<RecordVoiceRequest, SettingsDto>
{
    public override void Configure()
    {
        Put("/me/voice");
        AllowFileUploads();
    }

    public override async Task HandleAsync(RecordVoiceRequest req, CancellationToken ct)
    {
        var o = speech.Value;
        if (!o.Enabled)
        {
            AddError("Voice is not configured.");
            await Send.ErrorsAsync(503, ct);
            return;
        }
        if (!req.Consent)
        {
            AddError("Confirm that the recording is your own voice.");
            await Send.ErrorsAsync(400, ct);
            return;
        }
        if (req.Audio is null || req.Audio.Length == 0)
        {
            AddError("A recording is required.");
            await Send.ErrorsAsync(400, ct);
            return;
        }
        if (req.Audio.Length > o.MaxAudioBytes)
        {
            AddError($"The recording is larger than {o.MaxAudioBytes / (1024 * 1024)} MB.");
            await Send.ErrorsAsync(413, ct);
            return;
        }

        var me = who.Get(HttpContext);
        var row = await UserSettings.GetOrAddAsync(db, me.UserId, ct);
        var voiceId = row.VoiceId ?? "u-" + Guid.NewGuid().ToString("N")[..20];
        try
        {
            await using var stream = req.Audio.OpenReadStream();
            row.VoiceSeconds = await registry.RegisterAsync(
                voiceId, new AudioInput(stream, req.Audio.ContentType ?? "application/octet-stream", req.Audio.FileName ?? "voice"), ct);
        }
        catch (VoiceRejectedException e)
        {
            AddError(e.Message);
            await Send.ErrorsAsync(400, ct);
            return;
        }
        catch (SpeechUnavailableException)
        {
            AddError("The voice service is unavailable right now.");
            await Send.ErrorsAsync(503, ct);
            return;
        }

        row.VoiceId = voiceId;
        row.VoiceConsentAt = clock.GetUtcNow();
        row.UpdatedAt = row.VoiceConsentAt.Value;
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(UserSettings.ToDto(row, o.Enabled), ct);
    }
}

/// <summary>Deletes the caller's own voice: the clip in the voice service and the reference here.</summary>
public sealed class DeleteVoiceEndpoint(IVoiceRegistry registry, IOptions<SpeechOptions> speech, LotsDbContext db, ICurrentPrincipal who, TimeProvider clock)
    : EndpointWithoutRequest<SettingsDto>
{
    public override void Configure() => Delete("/me/voice");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var row = await db.UserSettings.SingleOrDefaultAsync(s => s.UserId == who.Get(HttpContext).UserId, ct);
        if (row?.VoiceId is { } id)
        {
            try
            {
                await registry.DeleteAsync(id, ct);
            }
            catch (SpeechUnavailableException)
            {
                AddError("The voice service is unavailable, so the recording could not be deleted. Try again.");
                await Send.ErrorsAsync(503, ct);
                return;
            }
            row.VoiceId = null;
            row.VoiceSeconds = null;
            row.VoiceConsentAt = null;
            row.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        await Send.OkAsync(UserSettings.ToDto(row, speech.Value.Enabled), ct);
    }
}
