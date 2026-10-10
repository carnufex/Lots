using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Core.Notifications;

public static class NotificationEvents
{
    public const string ApprovalRequested = "approval.requested";
    public const string ApprovalDecided = "approval.decided";
    public const string ApprovalExpired = "approval.expired";
}

public sealed class WebhookTarget
{
    public string Url { get; set; } = "";
    /// <summary>slack (a {"text": ...} body, also understood by Teams/Mattermost incoming webhooks) or json (the event as is).</summary>
    public string Format { get; set; } = "slack";
    /// <summary>Events to send; empty = all.</summary>
    public List<string> Events { get; set; } = [];
}

public sealed class EmailOptions
{
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool UseTls { get; set; } = true;
    public string? UserEnv { get; set; }
    public string? PasswordEnv { get; set; }
    public string? From { get; set; }
    public List<string> To { get; set; } = [];
    public List<string> Events { get; set; } = [];
    /// <summary>Also e-mail each person who may approve a request (address from their login), or their delegate while they are away (#136).</summary>
    public bool ToApprovers { get; set; } = true;
}

public sealed class NotificationOptions
{
    public const string Section = "Notifications";
    /// <summary>Base URL of the web UI, for links in messages (e.g. https://lots.example.com).</summary>
    public string? PublicUrl { get; set; }
    public List<WebhookTarget> Webhooks { get; set; } = [];
    public EmailOptions Email { get; set; } = new();
}

/// <summary>
/// Outbox (#74): a notification is a row written in the same transaction as the change it is about, so it is never lost and never sent
/// for a change that did not happen. Payloads carry no tool arguments or results: only what an approver needs to find the request.
/// </summary>
public static class Outbox
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Add(LotsDbContext db, string @event, object payload, DateTimeOffset now) =>
        db.Notifications.Add(new NotificationRecord
        {
            Id = Guid.NewGuid(), Event = @event, PayloadJson = JsonSerializer.Serialize(payload, Json), CreatedAt = now, NextAttemptAt = now,
        });
}

/// <summary>Delivers the outbox to the configured webhooks and e-mail, retrying with backoff (5 attempts, up to about an hour).</summary>
public sealed class NotificationWorker(IServiceScopeFactory scopes, IHttpClientFactory http, IOptions<NotificationOptions> options, TimeProvider clock,
    ILogger<NotificationWorker> logger, Profiles.ProfileRegistry? profiles = null) : BackgroundService
{
    public const int MaxAttempts = 5;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var o = options.Value;
        if (o.Webhooks.Count == 0 && string.IsNullOrEmpty(o.Email.SmtpHost)) return;
        while (!stop.IsCancellationRequested)
        {
            try { await DeliverDueAsync(stop); }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested) { logger.LogWarning("Notification delivery failed: {Error}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(5), stop).ContinueWith(_ => { });
        }
    }

    public async Task DeliverDueAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var now = clock.GetUtcNow();
        var due = await db.Notifications.Where(n => n.SentAt == null && n.Attempts < MaxAttempts && n.NextAttemptAt <= now)
            .OrderBy(n => n.CreatedAt).Take(20).ToListAsync(ct);
        foreach (var n in due)
        {
            try
            {
                await SendAsync(n, db, ct);
                n.SentAt = clock.GetUtcNow();
                n.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                n.Attempts++;
                n.LastError = ex.Message;
                n.NextAttemptAt = clock.GetUtcNow().AddSeconds(30 * Math.Pow(2, n.Attempts)); // 1, 2, 4, 8 min ...
                logger.LogWarning("Notification {Event} failed (attempt {Attempt}): {Error}", n.Event, n.Attempts, ex.Message);
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task SendAsync(NotificationRecord n, LotsDbContext db, CancellationToken ct)
    {
        var o = options.Value;
        using var payload = JsonDocument.Parse(n.PayloadJson);
        var text = Describe(n.Event, payload.RootElement, o.PublicUrl);
        foreach (var hook in o.Webhooks.Where(h => h.Events.Count == 0 || h.Events.Contains(n.Event)))
        {
            object body = hook.Format == "json" ? new { @event = n.Event, at = n.CreatedAt, data = payload.RootElement } : new { text };
            using var res = await http.CreateClient(nameof(NotificationWorker)).PostAsJsonAsync(hook.Url, body, ct);
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"webhook answered {(int)res.StatusCode}");
        }
        var mail = o.Email;
        if (!string.IsNullOrEmpty(mail.SmtpHost) && mail.From is not null && (mail.Events.Count == 0 || mail.Events.Contains(n.Event)))
        {
            var recipients = mail.To.Select(t => (Email: t, Note: (string?)null)).ToList();
            if (mail.ToApprovers && n.Event == NotificationEvents.ApprovalRequested)
                recipients.AddRange((await ApproversOfAsync(payload.RootElement, db, ct)).Select(r => (r.Email, r.OnBehalfOf is null ? null : $"You receive this because {r.OnBehalfOf} is away and named you as delegate.")));
            if (recipients.Count == 0) return;
            using var smtp = new SmtpClient(mail.SmtpHost, mail.SmtpPort) { EnableSsl = mail.UseTls };
            if (mail.UserEnv is not null)
                smtp.Credentials = new NetworkCredential(Environment.GetEnvironmentVariable(mail.UserEnv), Environment.GetEnvironmentVariable(mail.PasswordEnv ?? ""));
            foreach (var (to, note) in recipients.DistinctBy(r => r.Email.ToLowerInvariant()))
            {
                using var message = new MailMessage { From = new MailAddress(mail.From), Subject = $"Lots: {text.Split('\n')[0]}", Body = note is null ? text : text + "\n\n" + note };
                message.To.Add(to);
                await smtp.SendMailAsync(message, ct);
            }
        }
    }

    /// <summary>The people to e-mail about an approval request (approvers, or the delegate of an approver who is away).</summary>
    public async Task<List<ApproverRecipient>> ApproversOfAsync(JsonElement payload, LotsDbContext db, CancellationToken ct)
    {
        string S(string name) => payload.TryGetProperty(name, out var v) ? v.ToString() : "";
        if (profiles?.Find(S("profile")) is not { } profile) return [];
        var people = await db.UserProfiles.AsNoTracking().ToListAsync(ct);
        var settings = await db.UserSettings.AsNoTracking().Where(s => s.AwayUntil != null).ToDictionaryAsync(s => s.UserId, ct);
        return ApproverRouting.Recipients(profile, S("tool"), S("requestedBy"), people, settings, clock.GetUtcNow());
    }

    /// <summary>One readable line (plus a link) per event.</summary>
    public static string Describe(string @event, JsonElement p, string? publicUrl)
    {
        string S(string name) => p.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";
        var link = string.IsNullOrEmpty(publicUrl) ? "" : $"\n{publicUrl.TrimEnd('/')}/#/approvals";
        return @event switch
        {
            NotificationEvents.ApprovalRequested =>
                $"Approval needed: {S("tool")} ({S("risk")}) in {S("profile")}, requested by {S("requestedBy")}" +
                (S("requiredApprovals") == "2" ? ", two approvers required" : "") + $". Expires {S("expiresAt")}.{link}",
            NotificationEvents.ApprovalExpired => $"Approval expired without a decision: {S("tool")} requested by {S("requestedBy")}.{link}",
            NotificationEvents.ApprovalDecided => $"{S("tool")} requested by {S("requestedBy")} was {S("outcome")} by {S("decidedBy")}.",
            _ => @event,
        };
    }
}

/// <summary>Undecided approvals past their expiry are refused (#74): the run continues with a clear "expired" result.</summary>
public sealed class ApprovalExpiryWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ApprovalExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await ExpireDueAsync(stop); }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested) { logger.LogWarning("Approval expiry failed: {Error}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(30), stop).ContinueWith(_ => { });
        }
    }

    public async Task<int> ExpireDueAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var now = clock.GetUtcNow();
        var due = await db.Approvals.Where(a => a.Status == ApprovalStatus.Pending && a.ExpiresAt != null && a.ExpiresAt < now).ToListAsync(ct);
        foreach (var a in due)
        {
            var run = await db.Runs.SingleOrDefaultAsync(r => r.Id == a.RunId, ct);
            a.Status = ApprovalStatus.Expired;
            a.DecidedAt = now;
            a.DecidedBy = "system";
            a.Comment = "expired without a decision";
            if (run is not null)
            {
                db.AuditLog.Add(new AuditRecord
                {
                    Id = Guid.NewGuid(), At = now, UserId = run.UserId, Roles = run.Roles, Profile = run.Profile, RunId = run.Id, Tool = a.ToolName,
                    ToolCallId = a.ToolCallId, ArgumentsJson = a.ArgumentsJson, Decision = AuditDecision.ApprovalRefused, Reason = "expired without a decision",
                    ApproverId = "system",
                });
                if (run.Status == RunStatus.WaitingForApproval) run.Status = RunStatus.Pending;
                run.UpdatedAt = now;
            }
            Outbox.Add(db, NotificationEvents.ApprovalExpired, new { approvalId = a.Id, runId = a.RunId, tool = a.ToolName, requestedBy = a.RequestedBy }, now);
        }
        await db.SaveChangesAsync(ct);
        return due.Count;
    }
}
