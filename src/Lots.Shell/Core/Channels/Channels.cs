using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lots.Shell.Core.Mcp;
using Lots.Shell.Core.Policy;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Core.Channels;

public sealed class SlackOptions
{
    public bool Enabled { get; set; }
    /// <summary>Reference to the app's signing secret (env:NAME or file:/path): every request from Slack is verified with it.</summary>
    public string? SigningSecretRef { get; set; }
    /// <summary>Reference to the bot token (xoxb-...): used to look up users' e-mail and to post answers.</summary>
    public string? BotTokenRef { get; set; }
    /// <summary>The profile runs started from Slack use.</summary>
    public string? Profile { get; set; }
    public string ApiUrl { get; set; } = "https://slack.com/api/";
}

public sealed class EmailChannelOptions
{
    public bool Enabled { get; set; }
    /// <summary>Shared secret the inbound mail relay sends as a bearer token.</summary>
    public string? SecretRef { get; set; }
    public string? Profile { get; set; }
    /// <summary>Only mail the relay marked as passing SPF/DKIM (<c>"verified": true</c>) may start a run: a From header alone proves nothing.</summary>
    public bool RequireVerified { get; set; } = true;
}

public sealed class OpenAiApiOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>How long a non-streaming call waits for the run before it answers with a link instead.</summary>
    public int WaitSeconds { get; set; } = 300;
}

public sealed class ChannelOptions
{
    public const string Section = "Channels";
    public SlackOptions Slack { get; set; } = new();
    public EmailChannelOptions Email { get; set; } = new();
    public OpenAiApiOptions OpenAi { get; set; } = new();
    /// <summary>A channel user is mapped to the Lots user who logged in with the same e-mail within this many days (#107).</summary>
    public int IdentityMaxAgeDays { get; set; } = 90;
}

/// <summary>Where a channel run's answer goes (#107).</summary>
public sealed record ChannelReply(string Kind, string? Channel = null, string? Thread = null, string? To = null, string? Subject = null);

public static class ChannelKinds
{
    public const string Slack = "slack";
    public const string Email = "email";
}

/// <summary>
/// Identity follows the user (#107): a Slack user or mail sender becomes the Lots user who logged in through the identity provider
/// with the same e-mail address, with the roles of that login. Nobody else: an address Lots has never seen at a login is refused.
/// </summary>
public static class ChannelIdentity
{
    public static async Task<Principal?> ByEmailAsync(LotsDbContext db, string? email, int maxAgeDays, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var normalized = email.Trim().ToLowerInvariant();
        var since = now.AddDays(-maxAgeDays);
        var matches = await db.UserProfiles.AsNoTracking().Where(p => p.Email != null && p.Email.ToLower() == normalized && p.LastSeenAt > since).ToListAsync(ct);
        if (matches.Count != 1) return null; // none, or ambiguous: never guess between two accounts
        return new Principal(matches[0].UserId, matches[0].Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}

public static class SlackSignature
{
    /// <summary>Slack's v0 signature: HMAC-SHA256 over <c>v0:{timestamp}:{body}</c>, at most five minutes old (replay protection).</summary>
    public static bool Valid(string secret, string? timestamp, string? signature, string body, DateTimeOffset now)
    {
        if (!long.TryParse(timestamp, out var ts) || Math.Abs(now.ToUnixTimeSeconds() - ts) > 300 || signature is null) return false;
        var expected = "v0=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"v0:{timestamp}:{body}")));
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature));
    }
}

/// <summary>Each channel event is handled once, also when the channel retries or two replicas get it (#107).</summary>
public static class ChannelEvents
{
    public static async Task<bool> ClaimAsync(LotsDbContext db, string id, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.ChannelEvents.AnyAsync(e => e.Id == id, ct)) return false;
        var row = new ChannelEventRecord { Id = id, At = now };
        db.ChannelEvents.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is DbUpdateException or ArgumentException)
        {
            db.Entry(row).State = EntityState.Detached;
            return false;
        }
    }
}

/// <summary>The few Slack Web API calls Lots makes: look up a user's e-mail and post a message (optionally with buttons).</summary>
public sealed class SlackClient(IHttpClientFactory http, Microsoft.Extensions.Options.IOptions<ChannelOptions> options)
{
    private HttpClient Client()
    {
        var o = options.Value.Slack;
        var c = http.CreateClient(nameof(SlackClient));
        c.BaseAddress = new Uri(o.ApiUrl);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            SecretReference.Resolve(o.BotTokenRef ?? throw new InvalidOperationException("Channels:Slack:BotTokenRef is not set.")));
        return c;
    }

    public async Task<string?> EmailOfAsync(string userId, CancellationToken ct)
    {
        using var res = await Client().GetAsync($"users.info?user={Uri.EscapeDataString(userId)}", ct);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean()
               && doc.RootElement.GetProperty("user").GetProperty("profile").TryGetProperty("email", out var e) ? e.GetString() : null;
    }

    public async Task PostAsync(string channel, string? thread, string text, object[]? blocks, CancellationToken ct)
    {
        using var res = await Client().PostAsJsonAsync("chat.postMessage", new { channel, thread_ts = thread, text, blocks }, ct);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.GetProperty("ok").GetBoolean())
            throw new HttpRequestException("Slack: " + (doc.RootElement.TryGetProperty("error", out var err) ? err.GetString() : "error"));
    }
}
