using System.Text.Json;
using FastEndpoints;
using Lots.Shell.Core.Meetings;
using Lots.Shell.Core.Policy;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Features.Meetings;

public sealed record MeetingDto(Guid Id, string Title, string Status, string? Error, string? Language, double? DurationSeconds, int SpeakerCount,
    bool AudioAvailable, DateTimeOffset CreatedAt, DateTimeOffset? ProcessedAt, string UserId, long Bytes);

public sealed record MeetingLineDto(int Seq, double Start, double End, string Speaker, string Label, string Text);

public sealed record MeetingDetailDto(MeetingDto Meeting, IReadOnlyList<MeetingLineDto> Lines, IReadOnlyDictionary<string, string> SpeakerNames);

/// <summary>Meetings (#41): the owner's own; admins may read others', and every such read is an audit row.</summary>
public static class MeetingAccess
{
    public static MeetingDto ToDto(MeetingRecord m) => new(m.Id, m.Title, m.Status, m.Error, m.Language, m.DurationSeconds, m.SpeakerCount, !m.AudioDeleted,
        m.CreatedAt, m.ProcessedAt, m.UserId, m.Bytes);

    public static Dictionary<string, string> Names(MeetingRecord m) =>
        m.SpeakerNamesJson is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(m.SpeakerNamesJson) ?? [];

    /// <summary>The meeting if the caller may read it: the owner, or an admin (audited). Null = not found for this caller.</summary>
    public static async Task<MeetingRecord?> ReadableAsync(LotsDbContext db, Guid id, Principal me, IConfiguration config, string action, TimeProvider clock, CancellationToken ct)
    {
        var m = await db.Meetings.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return null;
        if (m.UserId == me.UserId) return m;
        if (!Conversations.ConversationViews.IsAdmin(me, config)) return null;
        db.AuditLog.Add(new AuditRecord
        {
            Id = Guid.NewGuid(), At = clock.GetUtcNow(), UserId = me.UserId, Roles = string.Join(',', me.Roles), Profile = "meetings", RunId = Guid.Empty,
            Tool = "meeting." + action, ArgumentsJson = JsonSerializer.Serialize(new { meeting = id, owner = m.UserId }), Decision = AuditDecision.Allowed,
            Reason = "an admin read another user's meeting (#41)", ResultStatus = "ok",
        });
        await db.SaveChangesAsync(ct);
        return m;
    }

    public static async Task<List<MeetingLineDto>> LinesAsync(LotsDbContext db, MeetingRecord m, CancellationToken ct)
    {
        var names = Names(m);
        return (await db.MeetingSegments.AsNoTracking().Where(s => s.MeetingId == m.Id).OrderBy(s => s.Seq).ToListAsync(ct))
            .Select(s => new MeetingLineDto(s.Seq, s.StartMs / 1000.0, s.EndMs / 1000.0, s.Speaker, names.GetValueOrDefault(s.Speaker) ?? s.Speaker, s.Text)).ToList();
    }
}

public sealed class UploadMeetingRequest
{
    public IFormFile? File { get; set; }
    public string? Title { get; set; }
    /// <summary>sv or en; empty = detect.</summary>
    public string? Language { get; set; }
    /// <summary>How many people speak, if known (1-20): diarization is more accurate with it.</summary>
    public int? Speakers { get; set; }
}

public sealed class UploadMeetingEndpoint(LotsDbContext db, ICurrentPrincipal who, MeetingAudioStore store, IOptions<MeetingOptions> options, TimeProvider clock)
    : Endpoint<UploadMeetingRequest, MeetingDto>
{
    public override void Configure()
    {
        Post("/meetings");
        AllowFileUploads();
    }

    public override async Task HandleAsync(UploadMeetingRequest req, CancellationToken ct)
    {
        if (!store.Enabled)
        {
            AddError("Meetings are not configured here (Meetings:AudioPath or Speech:AudioPath, and a voice service).");
            await Send.ErrorsAsync(503, ct);
            return;
        }
        var max = options.Value.MaxMegabytes * 1024L * 1024;
        if (req.File is not { Length: > 0 } file) AddError(x => x.File!, "Upload a recording.");
        else if (file.Length > max) AddError(x => x.File!, $"The recording is larger than {options.Value.MaxMegabytes} MB.");
        if (req.Language is { Length: > 0 } l && l is not ("sv" or "en")) AddError(x => x.Language!, "Language is sv or en (or empty to detect).");
        if (req.Speakers is < 1 or > 20) AddError(x => x.Speakers!, "Speakers is 1-20.");
        ThrowIfAnyErrors();

        var me = who.Get(HttpContext);
        using var ms = new MemoryStream();
        await req.File!.CopyToAsync(ms, ct);
        var now = clock.GetUtcNow();
        var meeting = new MeetingRecord
        {
            Id = Guid.NewGuid(), UserId = me.UserId, Title = string.IsNullOrWhiteSpace(req.Title) ? Path.GetFileNameWithoutExtension(req.File.FileName) : req.Title.Trim()[..Math.Min(req.Title.Trim().Length, 200)],
            FileName = Path.GetFileName(req.File.FileName), ContentType = req.File.ContentType ?? "application/octet-stream", Bytes = ms.Length,
            RequestedLanguage = string.IsNullOrEmpty(req.Language) ? null : req.Language, Speakers = req.Speakers, CreatedAt = now, UpdatedAt = now,
        };
        await store.SaveAsync(meeting.Id, ms.ToArray(), ct);
        db.Meetings.Add(meeting);
        await db.SaveChangesAsync(ct);
        await Send.ResponseAsync(MeetingAccess.ToDto(meeting), 202, ct);
    }
}

public sealed class ListMeetingsEndpoint(LotsDbContext db, ICurrentPrincipal who) : EndpointWithoutRequest<List<MeetingDto>>
{
    public override void Configure() => Get("/meetings");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var me = who.Get(HttpContext).UserId;
        await Send.OkAsync((await db.Meetings.AsNoTracking().Where(m => m.UserId == me).OrderByDescending(m => m.CreatedAt).Take(200).ToListAsync(ct))
            .Select(MeetingAccess.ToDto).ToList(), ct);
    }
}

public sealed record MeetingIdRequest(Guid Id);

public sealed class GetMeetingEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config, TimeProvider clock) : Endpoint<MeetingIdRequest, MeetingDetailDto>
{
    public override void Configure() => Get("/meetings/{Id}");

    public override async Task HandleAsync(MeetingIdRequest req, CancellationToken ct)
    {
        var m = await MeetingAccess.ReadableAsync(db, req.Id, who.Get(HttpContext), config, "read", clock, ct);
        if (m is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(new MeetingDetailDto(MeetingAccess.ToDto(m), await MeetingAccess.LinesAsync(db, m, ct), MeetingAccess.Names(m)), ct);
    }
}

public sealed record SpeakerNamesRequest(Guid Id, Dictionary<string, string> Names);

/// <summary>Names for the detected speakers ("Speaker 1" -> "Anna"). Only the owner.</summary>
public sealed class RenameSpeakersEndpoint(LotsDbContext db, ICurrentPrincipal who) : Endpoint<SpeakerNamesRequest, MeetingDetailDto>
{
    public override void Configure() => Put("/meetings/{Id}/speakers");

    public override async Task HandleAsync(SpeakerNamesRequest req, CancellationToken ct)
    {
        var m = await db.Meetings.SingleOrDefaultAsync(x => x.Id == req.Id && x.UserId == who.Get(HttpContext).UserId, ct);
        if (m is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var names = (req.Names ?? []).Where(kv => kv.Key.Length <= 100 && !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Trim()[..Math.Min(kv.Value.Trim().Length, 60)]);
        m.SpeakerNamesJson = names.Count == 0 ? null : JsonSerializer.Serialize(names);
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(new MeetingDetailDto(MeetingAccess.ToDto(m), await MeetingAccess.LinesAsync(db, m, ct), names), ct);
    }
}

public sealed class ExportMeetingRequest
{
    public Guid Id { get; set; }
    [QueryParam] public string? Format { get; set; }
}

public sealed class ExportMeetingEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config, TimeProvider clock) : Endpoint<ExportMeetingRequest>
{
    public override void Configure() => Get("/meetings/{Id}/export");

    public override async Task HandleAsync(ExportMeetingRequest req, CancellationToken ct)
    {
        var format = (req.Format ?? "txt").ToLowerInvariant();
        var m = await MeetingAccess.ReadableAsync(db, req.Id, who.Get(HttpContext), config, "export", clock, ct);
        if (m is null || !MeetingExport.Formats.Contains(format))
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var lines = (await MeetingAccess.LinesAsync(db, m, ct)).Select(l => (l.Start, l.End, l.Label, l.Text)).ToList();
        var text = MeetingExport.Render(format, m.Title, lines);
        // Titles are free text (Swedish, emoji): an ASCII fallback plus the UTF-8 name (RFC 5987), never raw text in a header.
        var name = string.Concat(m.Title.Split(Path.GetInvalidFileNameChars())).Trim();
        var ascii = new string(name.Select(c => c is >= ' ' and <= '~' && c != '"' ? c : '_').ToArray());
        HttpContext.Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
        {
            FileName = $"\"{(ascii.Length == 0 ? "meeting" : ascii)}.{format}\"", FileNameStar = $"{(name.Length == 0 ? "meeting" : name)}.{format}",
        }.ToString();
        await Send.StringAsync(text, contentType: format == "json" ? "application/json" : format == "vtt" ? "text/vtt" : "text/plain; charset=utf-8", cancellation: ct);
    }
}

public sealed class MeetingAudioEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config, TimeProvider clock, MeetingAudioStore store) : Endpoint<MeetingIdRequest>
{
    public override void Configure() => Get("/meetings/{Id}/audio");

    public override async Task HandleAsync(MeetingIdRequest req, CancellationToken ct)
    {
        var m = await MeetingAccess.ReadableAsync(db, req.Id, who.Get(HttpContext), config, "audio", clock, ct);
        var audio = m is null || m.AudioDeleted ? null : await store.ReadAsync(m.Id, ct);
        if (audio is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.BytesAsync(audio, contentType: m!.ContentType, cancellation: ct);
    }
}

public sealed class CancelMeetingEndpoint(LotsDbContext db, ICurrentPrincipal who, TimeProvider clock) : Endpoint<MeetingIdRequest, MeetingDto>
{
    public override void Configure() => Post("/meetings/{Id}/cancel");

    public override async Task HandleAsync(MeetingIdRequest req, CancellationToken ct)
    {
        var m = await db.Meetings.SingleOrDefaultAsync(x => x.Id == req.Id && x.UserId == who.Get(HttpContext).UserId, ct);
        if (m is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (m.Status is MeetingStatus.Queued && m.LeaseOwner is null) m.Status = MeetingStatus.Cancelled;
        else if (m.Status is MeetingStatus.Queued or MeetingStatus.Processing) m.CancelRequested = true; // the worker stops within ~15 s
        m.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(MeetingAccess.ToDto(m), ct);
    }
}

public sealed record DeleteMeetingRequest(Guid Id, [property: QueryParam] bool? AudioOnly = null);

/// <summary>Deletes the meeting (recording and transcript), or only its recording. The owner or an admin.</summary>
public sealed class DeleteMeetingEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config, TimeProvider clock, MeetingAudioStore store) : Endpoint<DeleteMeetingRequest>
{
    public override void Configure() => Delete("/meetings/{Id}");

    public override async Task HandleAsync(DeleteMeetingRequest req, CancellationToken ct)
    {
        var m = await MeetingAccess.ReadableAsync(db, req.Id, who.Get(HttpContext), config, "delete", clock, ct);
        if (m is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        store.Delete(m.Id);
        if (req.AudioOnly == true) m.AudioDeleted = true;
        else
        {
            db.MeetingSegments.RemoveRange(await db.MeetingSegments.Where(s => s.MeetingId == m.Id).ToListAsync(ct));
            db.Meetings.Remove(m);
        }
        await db.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
