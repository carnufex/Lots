using System.Text.Json;
using FastEndpoints;
using Lots.Shell.Core.Attachments;
using Lots.Shell.Core.Policy;
using Lots.Shell.Features.Conversations;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Features.Attachments;

public sealed record AttachmentDto(Guid Id, string Name, string Kind, string ContentType, long Size, int TextChars, DateTimeOffset CreatedAt);

public sealed class UploadAttachmentRequest
{
    public IFormFile? File { get; set; }
}

/// <summary>Uploads a file for a later run (#105). Only the uploader can use it in a run or download it again.</summary>
public sealed class UploadAttachmentEndpoint(LotsDbContext db, ICurrentPrincipal who, IOptions<AttachmentOptions> options, TimeProvider clock)
    : Endpoint<UploadAttachmentRequest, AttachmentDto>
{
    public override void Configure()
    {
        Post("/attachments");
        AllowFileUploads();
    }

    public override async Task HandleAsync(UploadAttachmentRequest req, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled) AddError("Attachments are turned off (Attachments:Enabled).");
        if (req.File is null || req.File.Length == 0) AddError(x => x.File!, "A file is required.");
        else if (req.File.Length > o.MaxBytes) AddError(x => x.File!, $"Files can be at most {o.MaxBytes / (1024 * 1024)} MB.");
        if (ValidationFailed)
        {
            await Send.ErrorsAsync(req.File?.Length > o.MaxBytes ? 413 : 400, ct);
            return;
        }

        using var ms = new MemoryStream();
        await req.File!.CopyToAsync(ms, ct);
        var data = ms.ToArray();
        var name = Path.GetFileName(req.File.FileName ?? "file");
        if (name.Length > 200) name = name[^200..];
        AttachmentContent content;
        try { content = AttachmentReader.Read(name, data, o); }
        catch (InvalidDataException ex)
        {
            AddError(x => x.File!, ex.Message);
            await Send.ErrorsAsync(415, ct);
            return;
        }

        var row = new AttachmentRecord
        {
            Id = Guid.NewGuid(), UserId = who.Get(HttpContext).UserId, FileName = name, ContentType = content.ContentType, Kind = content.Kind,
            Size = data.Length, Sha256 = AttachmentReader.Sha256(data), Data = data, Text = content.Text, CreatedAt = clock.GetUtcNow(),
        };
        db.Attachments.Add(row);
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(new AttachmentDto(row.Id, row.FileName, row.Kind, row.ContentType, row.Size, row.Text?.Length ?? 0, row.CreatedAt), ct);
    }
}

public sealed record AttachmentRequest(Guid Id);

/// <summary>Downloads an attachment: its owner and admins; always as a download, never rendered by the browser.</summary>
public sealed class GetAttachmentEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config) : Endpoint<AttachmentRequest>
{
    public override void Configure() => Get("/attachments/{Id}");

    public override async Task HandleAsync(AttachmentRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var a = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == req.Id, ct);
        if (a is null || (a.UserId != me.UserId && !ConversationViews.IsAdmin(me, config)))
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        HttpContext.Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileNameStar = a.FileName }.ToString();
        await Send.BytesAsync(a.Data, contentType: a.ContentType.Split(';')[0], cancellation: ct);
    }
}

public sealed class DeleteAttachmentEndpoint(LotsDbContext db, ICurrentPrincipal who) : Endpoint<AttachmentRequest>
{
    public override void Configure() => Delete("/attachments/{Id}");

    public override async Task HandleAsync(AttachmentRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var a = await db.Attachments.SingleOrDefaultAsync(x => x.Id == req.Id && x.UserId == me.UserId, ct);
        if (a is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        db.Attachments.Remove(a);
        await db.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}

public static class RunAttachments
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Checks that the caller owns the attachments and returns what the run stores about them; null with an error otherwise.</summary>
    public static async Task<(string? Json, string? Error)> ResolveAsync(LotsDbContext db, string userId, IReadOnlyList<Guid>? ids, AttachmentOptions o, CancellationToken ct)
    {
        if (ids is not { Count: > 0 }) return (null, null);
        if (ids.Count > o.MaxPerRun) return (null, $"At most {o.MaxPerRun} attachments per question.");
        var distinct = ids.Distinct().ToList();
        var found = await db.Attachments.AsNoTracking().Where(a => distinct.Contains(a.Id) && a.UserId == userId)
            .Select(a => new AttachmentRef(a.Id, a.FileName, a.Kind)).ToListAsync(ct);
        if (found.Count != distinct.Count) return (null, "An attachment does not exist or is not yours.");
        return (JsonSerializer.Serialize(distinct.Select(id => found.Single(f => f.Id == id)), Json), null);
    }
}
