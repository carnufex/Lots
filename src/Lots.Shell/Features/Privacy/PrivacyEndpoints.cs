using System.IO.Compression;
using System.Text.Json;
using FastEndpoints;
using Lots.Shell.Core.Knowledge;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Speech;
using Lots.Shell.Features.Admin;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Features.Privacy;

/// <summary>
/// How long data is kept (#79), in days; 0 = forever. Conversation audio has its own setting (Speech:AudioRetentionDays, ADR 0014).
/// The audit log is kept longest: it is the accountability record (who did what, who approved).
/// </summary>
public sealed class RetentionOptions
{
    public const string Section = "Retention";
    /// <summary>Runs with their messages and trace.</summary>
    public int RunsDays { get; set; } = 365;
    public int AuditDays { get; set; } = 0;
    public int VoiceUsageDays { get; set; } = 365;
    public int NotificationsDays { get; set; } = 30;
    public int KnowledgeConflictsDays { get; set; } = 365;
}

/// <summary>Deletes data past its retention once a day (and once at start-up, after a delay).</summary>
public sealed class RetentionWorker(IServiceScopeFactory scopes, IOptions<RetentionOptions> options, TimeProvider clock, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Delay(TimeSpan.FromMinutes(5), stop).ContinueWith(_ => { });
        while (!stop.IsCancellationRequested)
        {
            try { await PurgeAsync(stop); }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested) { logger.LogWarning("Retention purge failed: {Error}", ex.Message); }
            await Task.Delay(TimeSpan.FromDays(1), stop).ContinueWith(_ => { });
        }
    }

    public async Task<Dictionary<string, int>> PurgeAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var audio = scope.ServiceProvider.GetRequiredService<IAudioStore>();
        var purged = new Dictionary<string, int>();

        async Task Purge<T>(string name, int days, IQueryable<T> expired) where T : class
        {
            if (days <= 0) return;
            var rows = await expired.ToListAsync(ct);
            if (rows.Count == 0) return;
            db.Set<T>().RemoveRange(rows);
            await db.SaveChangesAsync(ct);
            purged[name] = rows.Count;
        }

        var runsBefore = now.AddDays(-o.RunsDays);
        if (o.RunsDays > 0)
        {
            // Finished runs only: nothing that is still running or waiting for an approval disappears under it.
            var oldRuns = db.Runs.Where(r => r.UpdatedAt < runsBefore && (r.Status == RunStatus.Completed || r.Status == RunStatus.Failed || r.Status == RunStatus.Cancelled));
            foreach (var conversation in await oldRuns.Where(r => r.ConversationId != null).Select(r => r.ConversationId!.Value).Distinct().ToListAsync(ct))
                if (!await db.Runs.AnyAsync(r => r.ConversationId == conversation && r.UpdatedAt >= runsBefore, ct))
                {
                    await audio.DeleteConversationAsync(conversation, ct);
                    if (await db.Conversations.FindAsync([conversation], ct) is { } c) db.Conversations.Remove(c);
                }
            await Purge("runs", o.RunsDays, oldRuns);
            await Purge("approvals", o.RunsDays, db.Approvals.Where(a => a.Status != ApprovalStatus.Pending && a.RequestedAt < runsBefore));
        }
        await Purge("audit", o.AuditDays, db.AuditLog.Where(a => a.At < now.AddDays(-o.AuditDays)));
        await Purge("voice consents", o.AuditDays, db.VoiceConsents.Where(c => c.At < now.AddDays(-o.AuditDays)));
        await Purge("attachments", o.RunsDays, db.Attachments.Where(a => a.CreatedAt < now.AddDays(-o.RunsDays)));
        await Purge("voice_usage", o.VoiceUsageDays, db.VoiceUsage.Where(v => v.At < now.AddDays(-o.VoiceUsageDays)));
        await Purge("notifications", o.NotificationsDays, db.Notifications.Where(n => n.CreatedAt < now.AddDays(-o.NotificationsDays) && (n.SentAt != null || n.Attempts >= 5)));
        await Purge("knowledge_conflicts", o.KnowledgeConflictsDays, db.KnowledgeConflicts.Where(c => c.DetectedAt < now.AddDays(-o.KnowledgeConflictsDays)));
        if (purged.Count > 0) logger.LogInformation("Retention purge: {Purged}", string.Join(", ", purged.Select(p => $"{p.Key}={p.Value}")));
        return purged;
    }
}

/// <summary>
/// Everything Lots keeps about the caller (#79), as a zip: data.json (runs with answers and trace, conversations, settings,
/// vocabulary, approvals, votes, connections without tokens, personal knowledge, speech metadata) and their conversation audio.
/// </summary>
public sealed class ExportMyDataEndpoint(LotsDbContext db, IAudioStore audio, IKnowledgeStore knowledge, ICurrentPrincipal who, TimeProvider clock) : EndpointWithoutRequest
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public override void Configure() => Get("/me/export");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var me = who.Get(HttpContext).UserId;
        var runs = await db.Runs.AsNoTracking().Include(r => r.Messages).Include(r => r.Steps).Where(r => r.UserId == me).OrderBy(r => r.CreatedAt).ToListAsync(ct);
        var conversationIds = runs.Where(r => r.ConversationId != null).Select(r => r.ConversationId!.Value).Distinct().ToList();
        var sources = (await knowledge.ListSourcesAsync(ct)).Where(s => s.Owner == me && s.Readers.Count == 1 && s.Readers[0] == "user:" + me).ToList();
        var personal = new List<object>();
        foreach (var s in sources)
            personal.Add(new { s.Id, s.Name, documents = (await knowledge.DocumentContentsAsync(s.Id, ct)).Select(d => new { d.Title, d.ExternalId, d.Content, d.UpdatedAt }) });

        var data = new
        {
            exportedAt = clock.GetUtcNow(),
            user = me,
            profile = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == me, ct),
            settings = await db.UserSettings.AsNoTracking().SingleOrDefaultAsync(s => s.UserId == me, ct),
            vocabulary = (await db.UserVocabulary.AsNoTracking().SingleOrDefaultAsync(v => v.UserId == me, ct))?.WordsJson,
            runs = runs.Select(r => new
            {
                r.Id, r.CreatedAt, r.Profile, r.Prompt, r.FinalAnswer, r.Status, r.Error, r.ConversationId, r.Voice,
                messages = r.Messages.OrderBy(m => m.Seq).Select(m => new { m.Role, m.Content, m.ToolCallsJson }),
                steps = r.Steps.OrderBy(s => s.Seq).Select(s => new { s.Kind, s.Name, s.ArgumentsJson, s.Result, s.LatencyMs, s.CreatedAt }),
            }),
            conversations = await db.Conversations.AsNoTracking().Where(c => conversationIds.Contains(c.Id)).ToListAsync(ct),
            approvalsRequested = await db.Approvals.AsNoTracking().Where(a => a.RequestedBy == me).ToListAsync(ct),
            approvalsDecided = await db.Approvals.AsNoTracking().Where(a => a.DecidedBy != null && a.DecidedBy.Contains(me)).ToListAsync(ct),
            conflictVotes = await db.ConflictVotes.AsNoTracking().Where(v => v.UserId == me).ToListAsync(ct),
            connectedAccounts = await db.UserCredentials.AsNoTracking().Where(c => c.UserId == me).Select(c => new { c.Server, c.ConnectedAt, c.ExpiresAt, c.Scope }).ToListAsync(ct),
            personalKnowledge = personal,
            speechUsage = await db.VoiceUsage.AsNoTracking().Where(v => v.UserId == me).ToListAsync(ct),
        };

        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using (var entry = archive.CreateEntry("data.json").Open())
                await JsonSerializer.SerializeAsync(entry, data, Json, ct);
            foreach (var clip in await db.ConversationAudio.AsNoTracking().Where(a => a.UserId == me).ToListAsync(ct))
                if (await audio.ReadAsync(clip.Id, ct) is { } c)
                {
                    var ext = clip.ContentType.Contains("wav") ? "wav" : clip.ContentType.Contains("webm") ? "webm" : clip.ContentType.Contains("ogg") ? "ogg" : "bin";
                    await using var entry = archive.CreateEntry($"audio/{clip.ConversationId:N}/{clip.CreatedAt:yyyyMMddHHmmss}-{clip.Kind}.{ext}").Open();
                    await entry.WriteAsync(c.Audio, ct);
                }
            // Files the user attached to questions (#105).
            foreach (var file in await db.Attachments.AsNoTracking().Where(a => a.UserId == me).ToListAsync(ct))
            {
                await using var entry = archive.CreateEntry($"attachments/{file.Id:N}-{string.Concat(file.FileName.Split(Path.GetInvalidFileNameChars()))}").Open();
                await entry.WriteAsync(file.Data, ct);
            }
        }
        HttpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"lots-export-{clock.GetUtcNow():yyyyMMdd}.zip\"";
        await Send.BytesAsync(zip.ToArray(), contentType: "application/zip", cancellation: ct);
    }
}

public sealed record DeleteMyDataRequest(string Confirm);

public sealed record DeletionReport(Dictionary<string, int> Deleted, IReadOnlyList<string> Kept);

/// <summary>
/// Deletes everything personal Lots keeps about the caller (#79): runs and conversations (with audio), settings and own voice,
/// vocabulary, personal knowledge, connected accounts, votes, out-of-office and the login profile. Kept: the audit log (append-only
/// accountability record, purged by Retention:AuditDays) and approvals other people requested.
/// </summary>
public sealed class DeleteMyDataEndpoint(LotsDbContext db, IAudioStore audio, IKnowledgeStore knowledge, IVoiceRegistry voices, ICurrentPrincipal who)
    : Endpoint<DeleteMyDataRequest, DeletionReport>
{
    public override void Configure() => Delete("/me/data");

    public override async Task HandleAsync(DeleteMyDataRequest req, CancellationToken ct)
    {
        if (req.Confirm != "delete my data")
        {
            AddError(x => x.Confirm, "Send confirm: \"delete my data\".");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        var me = who.Get(HttpContext).UserId;
        Dictionary<string, int> deleted;
        try
        {
            deleted = await DataDeletion.DeleteUserAsync(db, audio, knowledge, voices, me, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or SpeechUnavailableException)
        {
            // The own voice could not be removed (or its removal not verified): nothing else was deleted, so the user can retry.
            AddError(ex is SpeechUnavailableException ? "The voice service is unavailable, so your recorded voice could not be deleted. Nothing was deleted; try again." : ex.Message);
            await Send.ErrorsAsync(ex is SpeechUnavailableException ? 503 : 502, ct);
            return;
        }
        await Send.OkAsync(new DeletionReport(deleted, ["audit log entries (accountability record, kept per Retention:AuditDays)"]), ct);
    }
}

public static class DataDeletion
{
    public static async Task<Dictionary<string, int>> DeleteUserAsync(LotsDbContext db, IAudioStore audio, IKnowledgeStore knowledge, IVoiceRegistry voices, string user, CancellationToken ct)
    {
        var d = new Dictionary<string, int>();
        var settings = await db.UserSettings.SingleOrDefaultAsync(s => s.UserId == user, ct);
        if (settings?.VoiceId is { } voiceId)
        {
            await voices.DeleteAsync(voiceId, ct); // the clip lives in the voice service; fails loudly if it cannot be removed
            // Verified, not assumed (#93): a clip that is still there stops the deletion before anything else is removed.
            if (await voices.ExistsAsync(voiceId, ct) == true)
                throw new InvalidOperationException("The voice service still has the recording after deleting it; nothing was deleted. Try again.");
            db.VoiceConsents.Add(new VoiceConsentRecord
            {
                Id = Guid.NewGuid(), UserId = user, VoiceId = voiceId, Event = VoiceConsentEvent.Erased, Actor = user, At = DateTimeOffset.UtcNow,
            });
            d["own voice"] = 1;
        }
        var runs = await db.Runs.Where(r => r.UserId == user).ToListAsync(ct);
        foreach (var conversation in runs.Where(r => r.ConversationId != null).Select(r => r.ConversationId!.Value).Distinct())
            d["audio clips"] = d.GetValueOrDefault("audio clips") + await audio.DeleteConversationAsync(conversation, ct);
        db.Runs.RemoveRange(runs);
        d["runs"] = runs.Count;
        var conversationIds = runs.Where(r => r.ConversationId != null).Select(r => r.ConversationId!.Value).ToHashSet();
        var conversations = await db.Conversations.Where(c => c.UserId == user || conversationIds.Contains(c.Id)).ToListAsync(ct);
        db.Conversations.RemoveRange(conversations);
        d["conversations"] = conversations.Count;
        db.Approvals.RemoveRange(await db.Approvals.Where(a => a.RequestedBy == user).ToListAsync(ct));
        if (settings is not null) { db.UserSettings.Remove(settings); d["settings"] = 1; }
        if (await db.UserVocabulary.SingleOrDefaultAsync(v => v.UserId == user, ct) is { } vocab) { db.UserVocabulary.Remove(vocab); d["vocabulary"] = 1; }
        var creds = await db.UserCredentials.Where(c => c.UserId == user).ToListAsync(ct);
        db.UserCredentials.RemoveRange(creds);
        d["connected accounts"] = creds.Count;
        var votes = await db.ConflictVotes.Where(v => v.UserId == user).ToListAsync(ct);
        db.ConflictVotes.RemoveRange(votes);
        d["votes"] = votes.Count;
        if (await db.UserProfiles.SingleOrDefaultAsync(p => p.UserId == user, ct) is { } profile) { db.UserProfiles.Remove(profile); d["login profile"] = 1; }
        if (await db.QuotaOverrides.SingleOrDefaultAsync(q => q.UserId == user, ct) is { } quota) db.QuotaOverrides.Remove(quota);
        db.VoiceUsage.RemoveRange(await db.VoiceUsage.Where(v => v.UserId == user).ToListAsync(ct));
        var files = await db.Attachments.Where(a => a.UserId == user).ToListAsync(ct);
        db.Attachments.RemoveRange(files);
        d["attachments"] = files.Count;
        await db.SaveChangesAsync(ct);
        foreach (var s in (await knowledge.ListSourcesAsync(ct)).Where(s => s.Owner == user && s.Readers.Count == 1 && s.Readers[0] == "user:" + user))
        {
            await knowledge.DeleteSourceAsync(s.Id, ct);
            d["personal knowledge sources"] = d.GetValueOrDefault("personal knowledge sources") + 1;
        }
        return d;
    }
}

public sealed record TableReport(string Name, long Rows, DateTimeOffset? Oldest, int RetentionDays, string Note);

public sealed record DataReport(IReadOnlyList<TableReport> Tables, int AudioRetentionDays, bool AudioStored);

/// <summary>What is stored, how much, how old and how long it is kept (#79). Admins.</summary>
public sealed class DataReportEndpoint(LotsDbContext db, IOptions<RetentionOptions> retention, IOptions<SpeechOptions> speech, IAudioStore audio,
    IConfiguration config, ICurrentPrincipal who) : AdminEndpoint<EmptyRequest, DataReport>(config, who)
{
    public override void Configure() => Get("/admin/v1/data-report");

    public override async Task HandleAsync(EmptyRequest _, CancellationToken ct)
    {
        if (!await AllowedAsync(ct)) return;
        var r = retention.Value;
        var tables = new List<TableReport>
        {
            new("runs (prompts, answers, trace)", await db.Runs.LongCountAsync(ct), await db.Runs.MinAsync(x => (DateTimeOffset?)x.CreatedAt, ct), r.RunsDays, "personal: prompts and answers"),
            new("audit log", await db.AuditLog.LongCountAsync(ct), await db.AuditLog.MinAsync(x => (DateTimeOffset?)x.At, ct), r.AuditDays, "accountability; kept longest"),
            new("conversation audio", await db.ConversationAudio.LongCountAsync(ct), await db.ConversationAudio.MinAsync(x => (DateTimeOffset?)x.CreatedAt, ct), speech.Value.AudioRetentionDays, "personal: voice recordings, encrypted"),
            new("speech usage metadata", await db.VoiceUsage.LongCountAsync(ct), await db.VoiceUsage.MinAsync(x => (DateTimeOffset?)x.At, ct), r.VoiceUsageDays, "no content"),
            new("notifications outbox", await db.Notifications.LongCountAsync(ct), await db.Notifications.MinAsync(x => (DateTimeOffset?)x.CreatedAt, ct), r.NotificationsDays, "tool, requester; no arguments"),
            new("knowledge conflicts", await db.KnowledgeConflicts.LongCountAsync(ct), await db.KnowledgeConflicts.MinAsync(x => (DateTimeOffset?)x.DetectedAt, ct), r.KnowledgeConflictsDays, ""),
            new("user settings and own voice", await db.UserSettings.LongCountAsync(ct), null, 0, "kept until the user deletes them"),
            new("login profiles (e-mail, roles)", await db.UserProfiles.LongCountAsync(ct), await db.UserProfiles.MinAsync(x => (DateTimeOffset?)x.LastSeenAt, ct), 0, "for approval notifications"),
            new("connected accounts", await db.UserCredentials.LongCountAsync(ct), await db.UserCredentials.MinAsync(x => (DateTimeOffset?)x.ConnectedAt, ct), 0, "encrypted tokens"),
            new("configuration versions", await db.ConfigVersions.LongCountAsync(ct), await db.ConfigVersions.MinAsync(x => (DateTimeOffset?)x.AppliedAt, ct), 0, "admin audit of config"),
        };
        await Send.OkAsync(new DataReport(tables, speech.Value.AudioRetentionDays, audio.Enabled), ct);
    }
}
