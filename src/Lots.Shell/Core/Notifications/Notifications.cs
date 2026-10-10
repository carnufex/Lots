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
    public const string QuotaWarning = "quota.warning";
    /// <summary>A scheduled or triggered run finished (#101); delivered to the schedule's own targets as well.</summary>
    public const string RunFinished = "run.finished";
    /// <summary>The answer to a question asked in a channel (#107), sent back there (Slack thread, mail reply).</summary>
    public const string ChannelReply = "channel.reply";
    /// <summary>A channel run waits for an approval (#107): Approve/Deny buttons in the Slack thread.</summary>
    public const string ChannelApproval = "channel.approval";
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
        if (n.Event is NotificationEvents.ChannelReply or NotificationEvents.ChannelApproval)
        {
            await SendToChannelAsync(n.Event, payload.RootElement, ct);
            return;
        }
        var text = Describe(n.Event, payload.RootElement, o.PublicUrl);
        // A schedule's own targets (#101): its webhooks get a Slack/Teams style text, its addresses an e-mail.
        var ownHooks = payload.RootElement.TryGetProperty("deliverWebhooks", out var dw) ? dw.EnumerateArray().Select(x => x.GetString()!).ToList() : [];
        foreach (var url in ownHooks)
        {
            using var res = await http.CreateClient(nameof(NotificationWorker)).PostAsJsonAsync(url, new { text }, ct);
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"webhook answered {(int)res.StatusCode}");
        }
        foreach (var hook in o.Webhooks.Where(h => h.Events.Count == 0 || h.Events.Contains(n.Event)))
        {
            object body = hook.Format == "json" ? new { @event = n.Event, at = n.CreatedAt, data = payload.RootElement } : new { text };
            using var res = await http.CreateClient(nameof(NotificationWorker)).PostAsJsonAsync(hook.Url, body, ct);
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"webhook answered {(int)res.StatusCode}");
        }
        var mail = o.Email;
        if (!string.IsNullOrEmpty(mail.SmtpHost) && mail.From is not null
            && (mail.Events.Count == 0 || mail.Events.Contains(n.Event) || payload.RootElement.TryGetProperty("deliverEmail", out _)))
        {
            var recipients = (mail.Events.Count == 0 || mail.Events.Contains(n.Event) ? mail.To : []).Select(t => (Email: t, Note: (string?)null)).ToList();
            if (payload.RootElement.TryGetProperty("deliverEmail", out var de))
                recipients.AddRange(de.EnumerateArray().Select(x => (Email: x.GetString()!, Note: (string?)null)));
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

    /// <summary>Answers and approval buttons go back to where the question was asked (#107).</summary>
    private async Task SendToChannelAsync(string @event, JsonElement p, CancellationToken ct)
    {
        string S(string name) => p.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";
        var link = string.IsNullOrEmpty(options.Value.PublicUrl) ? "" : $"{options.Value.PublicUrl!.TrimEnd('/')}/#/runs/{S("runId")}";
        if (S("kind") == Channels.ChannelKinds.Slack)
        {
            var slack = scopes.CreateScope().ServiceProvider.GetRequiredService<Channels.SlackClient>();
            if (@event == NotificationEvents.ChannelReply)
            {
                await slack.PostAsync(S("channel"), S("thread"), S("answer"), null, ct);
                return;
            }
            // Explicit buttons only: an approval is never given by typing in the thread.
            var text = $"Approval needed: {S("tool")} ({S("risk")}) for {S("requestedBy")}. Decide here or in Lots{(link.Length > 0 ? ": " + link : ".")}";
            object[] blocks =
            [
                new { type = "section", text = new { type = "mrkdwn", text } },
                new
                {
                    type = "actions",
                    elements = new object[]
                    {
                        new { type = "button", action_id = "lots_approve", style = "primary", text = new { type = "plain_text", text = "Approve" }, value = S("approvalId") },
                        new { type = "button", action_id = "lots_deny", style = "danger", text = new { type = "plain_text", text = "Deny" }, value = S("approvalId") },
                    },
                },
            ];
            await slack.PostAsync(S("channel"), S("thread"), text, blocks, ct);
            return;
        }
        if (S("kind") == Channels.ChannelKinds.Email && @event == NotificationEvents.ChannelReply)
        {
            var mail = options.Value.Email;
            if (string.IsNullOrEmpty(mail.SmtpHost) || mail.From is null) throw new InvalidOperationException("No SMTP server configured for mail replies.");
            using var smtp = new SmtpClient(mail.SmtpHost, mail.SmtpPort) { EnableSsl = mail.UseTls };
            if (mail.UserEnv is not null)
                smtp.Credentials = new NetworkCredential(Environment.GetEnvironmentVariable(mail.UserEnv), Environment.GetEnvironmentVariable(mail.PasswordEnv ?? ""));
            using var message = new MailMessage { From = new MailAddress(mail.From), Subject = S("subject"), Body = S("answer") + (link.Length > 0 ? "\n\n" + link : "") };
            message.To.Add(S("to"));
            await smtp.SendMailAsync(message, ct);
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
            NotificationEvents.QuotaWarning => $"{S("user")} has used {S("used")} of {S("limit")} ({S("share")}) of their daily {S("budget")} budget.",
            NotificationEvents.RunFinished => $"{S("trigger")} {S("status").ToLowerInvariant()}: {S("answer")}" +
                (string.IsNullOrEmpty(publicUrl) ? "" : $"\n{publicUrl.TrimEnd('/')}/#/runs/{S("runId")}"),
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
            Telemetry.LotsMetrics.Approvals.Add(1, new("event", "expired"), new("risk", a.Risk));
            if (run is not null) Telemetry.Tracing.ApprovalWait(run, a, "Expired", now);
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
