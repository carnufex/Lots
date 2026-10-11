using System.Text.Json;
using System.Text.RegularExpressions;
using FastEndpoints;
using Lots.Shell.Core.Channels;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Features.Approvals;
using Lots.Shell.Persistence;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Features.Channels;

/// <summary>
/// Microsoft Teams (#149): the Bot Framework messaging endpoint. Every activity carries a Bot Framework JWT that is verified before
/// anything else; messages become runs as the mapped Lots user (answer in the same conversation), and Approve/Deny on an approval
/// card (an <c>adaptiveCard/action</c> invoke) is decided as the clicking person through the same rules as the web UI.
/// </summary>
public sealed partial class TeamsMessagesEndpoint(LotsDbContext db, ProfileRegistry profiles, IOptions<ChannelOptions> options, TeamsTokenValidator validator,
    TeamsClient teams, TimeProvider clock, ILogger<TeamsMessagesEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/channels/teams/messages");
        AllowAnonymous(); // the Bot Framework token is the authentication
    }

    [GeneratedRegex(@"<at>.*?</at>", RegexOptions.Singleline)]
    private static partial Regex Mention();

    [GeneratedRegex(@"<br\s*/?>|</(p|div|li)>", RegexOptions.IgnoreCase)]
    private static partial Regex Break();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\u00a0]+")]
    private static partial Regex Spaces();

    /// <summary>The question without the bot's mention or Teams' HTML (line breaks kept, inline tags dropped).</summary>
    public static string TextOf(string? text)
    {
        var plain = Tag().Replace(Break().Replace(Mention().Replace(text ?? "", " "), "\n"), "");
        return Spaces().Replace(System.Net.WebUtility.HtmlDecode(plain), " ").Trim();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var o = options.Value;
        var body = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync(ct);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }
        using var _ = doc;
        var a = doc.RootElement;
        string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var serviceUrl = Str(a, "serviceUrl");
        if (!o.Teams.Enabled || !await validator.ValidAsync(HttpContext.Request.Headers.Authorization, serviceUrl, Str(a, "channelId"), ct))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var now = clock.GetUtcNow();
        var type = Str(a, "type");
        var conversation = a.TryGetProperty("conversation", out var conv) ? Str(conv, "id") : null;
        var from = a.TryGetProperty("from", out var f) ? f : default;
        if (conversation is null || from.ValueKind != JsonValueKind.Object)
        {
            await Send.OkAsync(ct);
            return;
        }

        if (type == "invoke" && Str(a, "name") == "adaptiveCard/action")
        {
            await DecideAsync(a, serviceUrl!, conversation, from, now, ct);
            return;
        }
        if (type != "message" || Str(a, "id") is not { } activityId || !await ChannelEvents.ClaimAsync(db, "teams:" + activityId, now, ct))
        {
            await Send.OkAsync(ct); // conversation updates, typing, reactions, or a redelivered message
            return;
        }

        var text = TextOf(Str(a, "text"));
        var user = await TeamsIdentity.ResolveAsync(db, teams, o, serviceUrl!, conversation, from, now, ct);
        if (user is null || profiles.Find(o.Teams.Profile ?? "") is not { } profile || text.Length == 0)
        {
            await teams.ReplyAsync(serviceUrl!, conversation, activityId, user is null
                ? "I can't tell who you are in Lots. Log in to Lots once with the same account, then ask again."
                : "Ask me a question after the mention.", null, ct);
            await Send.OkAsync(ct);
            return;
        }

        var run = ChannelRuns.Create(user, profile, text, "teams", new ChannelReply(ChannelKinds.Teams, conversation, activityId, ServiceUrl: serviceUrl), now);
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Teams message from {User} started run {Run}", user.UserId, run.Id);
        await Send.OkAsync(ct);
    }

    private async Task DecideAsync(JsonElement a, string serviceUrl, string conversation, JsonElement from, DateTimeOffset now, CancellationToken ct)
    {
        var action = a.TryGetProperty("value", out var v) && v.TryGetProperty("action", out var act) ? act : default;
        var verb = action.ValueKind == JsonValueKind.Object && action.TryGetProperty("verb", out var vb) ? vb.GetString() : null;
        var id = action.ValueKind == JsonValueKind.Object && action.TryGetProperty("data", out var d) && d.TryGetProperty("approvalId", out var aid) ? aid.GetString() : null;
        string message;
        if (verb is not ("lots_approve" or "lots_deny") || !Guid.TryParse(id, out var approvalId)) message = "Nothing to do.";
        else if (await TeamsIdentity.ResolveAsync(db, teams, options.Value, serviceUrl, conversation, from, now, ct) is not { } me)
            message = "I can't tell who you are in Lots, so you can't decide this here.";
        else
        {
            var result = await ApprovalDecisions.DecideAsync(db, profiles, me, approvalId,
                verb == "lots_approve" ? ApprovalStatus.Approved : ApprovalStatus.Denied, null, now, ct);
            message = result.Status == 200 ? $"{(verb == "lots_approve" ? "Approved" : "Denied")} by {me.UserId}." : $"{me.UserId}: {result.Error} Decide it in Lots instead.";
        }
        // The invoke response: Teams shows the message to the person who clicked.
        await Send.OkAsync(new { statusCode = 200, type = "application/vnd.microsoft.activity.message", value = message }, ct);
    }
}
