using System.Text.Json;
using FastEndpoints;
using Lots.Shell.Core.Channels;
using Lots.Shell.Core.Mcp;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Features.Approvals;
using Lots.Shell.Persistence;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Features.Channels;

public static class ChannelRuns
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A run for a mapped channel user: their identity and roles, the channel's profile, the answer sent back where it was asked.</summary>
    public static RunRecord Create(Principal user, Profile profile, string prompt, string trigger, ChannelReply reply, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), Prompt = prompt, Profile = profile.Name, UserId = user.UserId, Roles = string.Join(',', user.Roles),
        CreatedAt = now, UpdatedAt = now, Trigger = trigger, ReplyJson = JsonSerializer.Serialize(reply, Json),
    };

    public static ChannelReply? ReplyOf(RunRecord run) => run.ReplyJson is null ? null : JsonSerializer.Deserialize<ChannelReply>(run.ReplyJson, Json);
}

/// <summary>
/// Slack Events API (#107): mentions of the app and direct messages become runs as the IdP user with the same e-mail. Every request
/// is verified with the signing secret; each event is handled once. The answer goes back to the thread.
/// </summary>
public sealed class SlackEventsEndpoint(LotsDbContext db, ProfileRegistry profiles, IOptions<ChannelOptions> options, SlackClient slack, TimeProvider clock,
    ILogger<SlackEventsEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/channels/slack/events");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var o = options.Value;
        var body = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync(ct);
        string? secret = null;
        try { secret = o.Slack.SigningSecretRef is { } r ? SecretReference.Resolve(r) : null; } catch (InvalidOperationException) { }
        var now = clock.GetUtcNow();
        if (!o.Slack.Enabled || secret is null
            || !SlackSignature.Valid(secret, HttpContext.Request.Headers["X-Slack-Request-Timestamp"], HttpContext.Request.Headers["X-Slack-Signature"], body, now))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();
        if (type == "url_verification")
        {
            await Send.OkAsync(new { challenge = root.GetProperty("challenge").GetString() }, ct);
            return;
        }
        if (type != "event_callback" || !root.TryGetProperty("event", out var e))
        {
            await Send.OkAsync(ct);
            return;
        }

        var kind = e.GetProperty("type").GetString();
        var isDirect = kind == "message" && e.TryGetProperty("channel_type", out var ctype) && ctype.GetString() == "im";
        if ((kind != "app_mention" && !isDirect) || e.TryGetProperty("bot_id", out _) || e.TryGetProperty("subtype", out _)
            || !await ChannelEvents.ClaimAsync(db, "slack:" + root.GetProperty("event_id").GetString(), now, ct))
        {
            await Send.OkAsync(ct); // not for us, a bot's own message, an edit, or a retry of an event already handled
            return;
        }

        var channel = e.GetProperty("channel").GetString()!;
        var thread = e.TryGetProperty("thread_ts", out var t) ? t.GetString() : e.GetProperty("ts").GetString();
        var text = System.Text.RegularExpressions.Regex.Replace(e.GetProperty("text").GetString() ?? "", @"<@[A-Z0-9]+>\s*", "").Trim();
        var email = await slack.EmailOfAsync(e.GetProperty("user").GetString()!, ct);
        var user = await ChannelIdentity.ByEmailAsync(db, email, o.IdentityMaxAgeDays, now, ct);
        if (user is null || profiles.Find(o.Slack.Profile ?? "") is not { } profile || text.Length == 0)
        {
            await slack.PostAsync(channel, thread, user is null
                ? "I can't tell who you are in Lots. Log in to Lots once with the same e-mail address, then ask again."
                : "Ask me a question after the mention.", null, ct);
            await Send.OkAsync(ct);
            return;
        }

        var run = ChannelRuns.Create(user, profile, text, "slack", new ChannelReply(ChannelKinds.Slack, channel, thread), now);
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Slack message from {User} started run {Run}", user.UserId, run.Id);
        await Send.OkAsync(ct);
    }
}

/// <summary>
/// Slack buttons (#107): Approve/Deny on an approval request, decided as the clicking user's Lots identity through the same rules
/// as the web UI. Anything that needs a reason sends the user to the web UI.
/// </summary>
public sealed class SlackInteractiveEndpoint(LotsDbContext db, ProfileRegistry profiles, IOptions<ChannelOptions> options, SlackClient slack, TimeProvider clock)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/channels/slack/interactive");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var o = options.Value;
        var body = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync(ct);
        string? secret = null;
        try { secret = o.Slack.SigningSecretRef is { } r ? SecretReference.Resolve(r) : null; } catch (InvalidOperationException) { }
        var now = clock.GetUtcNow();
        if (!o.Slack.Enabled || secret is null
            || !SlackSignature.Valid(secret, HttpContext.Request.Headers["X-Slack-Request-Timestamp"], HttpContext.Request.Headers["X-Slack-Signature"], body, now))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var form = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(body);
        using var payload = JsonDocument.Parse(form["payload"].ToString());
        var p = payload.RootElement;
        var action = p.GetProperty("actions")[0];
        var actionId = action.GetProperty("action_id").GetString();
        if (actionId is not ("lots_approve" or "lots_deny") || !Guid.TryParse(action.GetProperty("value").GetString(), out var approvalId))
        {
            await Send.OkAsync(ct);
            return;
        }

        var email = await slack.EmailOfAsync(p.GetProperty("user").GetProperty("id").GetString()!, ct);
        var me = await ChannelIdentity.ByEmailAsync(db, email, o.IdentityMaxAgeDays, now, ct);
        var channel = p.GetProperty("channel").GetProperty("id").GetString()!;
        var thread = p.GetProperty("message").TryGetProperty("thread_ts", out var t) ? t.GetString() : p.GetProperty("message").GetProperty("ts").GetString();
        string reply;
        if (me is null) reply = "I can't tell who you are in Lots, so you can't decide this here.";
        else
        {
            var result = await ApprovalDecisions.DecideAsync(db, profiles, me, approvalId,
                actionId == "lots_approve" ? ApprovalStatus.Approved : ApprovalStatus.Denied, null, now, ct);
            reply = result.Status == 200
                ? $"{(actionId == "lots_approve" ? "Approved" : "Denied")} by {me.UserId}."
                : $"{me.UserId}: {result.Error} Decide it in Lots instead.";
        }
        await slack.PostAsync(channel, thread, reply, null, ct);
        await Send.OkAsync(ct);
    }
}

public sealed record InboundMail(string From, string? Subject, string? Text, string? MessageId, bool Verified = false);

/// <summary>
/// E-mail to run (#107): a mail relay (Mailgun/SendGrid inbound parse, a small script on the mail server) posts the parsed message
/// with the shared secret. Only verified mail (SPF/DKIM checked by the relay) from an address Lots knows from a login starts a run;
/// the answer is sent back by mail. Approvals cannot be given by mail.
/// </summary>
public sealed class EmailInboundEndpoint(LotsDbContext db, ProfileRegistry profiles, IOptions<ChannelOptions> options, TimeProvider clock,
    ILogger<EmailInboundEndpoint> logger) : Endpoint<InboundMail>
{
    public override void Configure()
    {
        Post("/channels/email/inbound");
        AllowAnonymous();
    }

    public override async Task HandleAsync(InboundMail req, CancellationToken ct)
    {
        var o = options.Value;
        string? secret = null;
        try { secret = o.Email.SecretRef is { } r ? SecretReference.Resolve(r) : null; } catch (InvalidOperationException) { }
        string? auth = HttpContext.Request.Headers.Authorization;
        if (!o.Email.Enabled || secret is null || !Core.Schedules.ScheduleRuns.SecretMatches(auth?.StartsWith("Bearer ") == true ? auth[7..].Trim() : null, secret))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        var now = clock.GetUtcNow();
        // Accepted but ignored (202) when unverified, unknown, a duplicate or empty: no reply, so spoofed mail cannot make Lots send mail.
        if ((o.Email.RequireVerified && !req.Verified) || string.IsNullOrWhiteSpace(req.Text)
            || (req.MessageId is { } id && !await ChannelEvents.ClaimAsync(db, "email:" + id, now, ct))
            || await ChannelIdentity.ByEmailAsync(db, ExtractAddress(req.From), o.IdentityMaxAgeDays, now, ct) is not { } user
            || profiles.Find(o.Email.Profile ?? "") is not { } profile)
        {
            logger.LogInformation("Inbound mail from {From} ignored (unverified, unknown sender, duplicate or empty)", req.From);
            await Send.ResponseAsync(new { accepted = false }, 202, ct);
            return;
        }
        var prompt = (req.Subject is { Length: > 0 } s ? s + "\n\n" : "") + StripQuoted(req.Text!);
        var run = ChannelRuns.Create(user, profile, prompt, "email",
            new ChannelReply(ChannelKinds.Email, To: ExtractAddress(req.From), Subject: "Re: " + (req.Subject ?? "your question")), now);
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        await Send.ResponseAsync(new { accepted = true, runId = run.Id }, 202, ct);
    }

    public static string ExtractAddress(string from) =>
        System.Text.RegularExpressions.Regex.Match(from, @"[^<\s]+@[^>\s]+") is { Success: true } m ? m.Value : from.Trim();

    /// <summary>Drops the quoted history below the new text ("On ... wrote:", "> ..." lines).</summary>
    public static string StripQuoted(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        foreach (var l in lines)
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(l, @"^(On .+ wrote:|Den .+ skrev .+:|-----Original Message-----)")) break;
            if (!l.StartsWith('>')) kept.Add(l);
        }
        return string.Join('\n', kept).Trim();
    }
}
