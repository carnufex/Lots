using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lots.Shell.Core.Mcp;
using Lots.Shell.Core.Policy;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Lots.Shell.Core.Channels;

/// <summary>Microsoft Teams through a Bot Framework bot (#149). Needs an Azure bot registration: its app id and client secret.</summary>
public sealed class TeamsOptions
{
    public bool Enabled { get; set; }
    /// <summary>The bot's Microsoft app id: the audience of every incoming token and the client id for outgoing calls.</summary>
    public string? AppId { get; set; }
    /// <summary>Reference to the bot's client secret (env:NAME or file:/path).</summary>
    public string? AppPasswordRef { get; set; }
    /// <summary>The Entra tenant of a single-tenant bot; empty for a multi-tenant bot (tokens from botframework.com).</summary>
    public string? TenantId { get; set; }
    /// <summary>The profile runs started from Teams use.</summary>
    public string? Profile { get; set; }
    /// <summary>
    /// Map <c>from.aadObjectId</c> to the Lots user with that id. Only right when Entra is the identity provider and
    /// <c>Auth:Oidc:UserClaim</c> is <c>oid</c>; otherwise people are mapped by e-mail like in Slack.
    /// </summary>
    public bool MatchObjectId { get; set; }
    /// <summary>
    /// Where Lots may send replies (and its bot token). An activity names its own <c>serviceUrl</c>; one outside this list is refused,
    /// so a forged or misrouted activity cannot make Lots hand its token to another host.
    /// </summary>
    public List<string> ServiceUrls { get; set; } = ["https://smba.trafficmanager.net/"];
    public string OpenIdMetadataUrl { get; set; } = "https://login.botframework.com/v1/.well-known/openidconfiguration";
    public string Issuer { get; set; } = "https://api.botframework.com";
    /// <summary>Token endpoint for outgoing calls; default from <see cref="TenantId"/>.</summary>
    public string? TokenUrl { get; set; }

    public string TokenEndpoint => TokenUrl ?? $"https://login.microsoftonline.com/{(string.IsNullOrEmpty(TenantId) ? "botframework.com" : TenantId)}/oauth2/v2.0/token";

    public bool ServiceUrlAllowed(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo)) return false;
        var normalized = u.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
        return ServiceUrls.Any(a => normalized.StartsWith(a.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Verifies that an activity really comes from the Bot Framework (#149): a JWT signed by one of its published keys (endorsed for
/// the activity's channel), issued by the Bot Framework for this bot, whose <c>serviceurl</c> claim is the activity's own.
/// </summary>
public sealed class TeamsTokenValidator
{
    private readonly IOptions<ChannelOptions> _options;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _metadata;
    private readonly JsonWebTokenHandler _handler = new();

    public TeamsTokenValidator(IOptions<ChannelOptions> options, IHttpClientFactory http)
    {
        _options = options;
        var url = options.Value.Teams.OpenIdMetadataUrl;
        _metadata = new ConfigurationManager<OpenIdConnectConfiguration>(url, new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(http.CreateClient(nameof(TeamsTokenValidator))) { RequireHttps = url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) });
    }

    public async Task<bool> ValidAsync(string? authorization, string? serviceUrl, string? channelId, CancellationToken ct)
    {
        var o = _options.Value.Teams;
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal) || string.IsNullOrEmpty(o.AppId)) return false;
        var token = authorization[7..].Trim();
        OpenIdConnectConfiguration config;
        try { config = await _metadata.GetConfigurationAsync(ct); }
        catch (InvalidOperationException) { return false; } // metadata unreachable: refuse rather than accept unverified
        var result = await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = o.Issuer, ValidAudience = o.AppId, IssuerSigningKeys = config.SigningKeys, ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromMinutes(5), RequireSignedTokens = true, RequireExpirationTime = true,
        });
        if (!result.IsValid) return false;
        if (!result.Claims.TryGetValue("serviceurl", out var claimed) || !string.Equals(claimed?.ToString()?.TrimEnd('/'), serviceUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            return false;
        // A key endorsed for some channels only signs for those (the Bot Framework's rule).
        var kid = (result.SecurityToken as JsonWebToken)?.Kid;
        var key = config.JsonWebKeySet?.Keys.FirstOrDefault(k => k.Kid == kid);
        if (key is not null && key.AdditionalData.TryGetValue("endorsements", out var e) && e is not null)
        {
            var endorsed = e is JsonElement je && je.ValueKind == JsonValueKind.Array ? je.EnumerateArray().Select(x => x.GetString()).ToList()
                : e is IEnumerable<object> list ? list.Select(x => x?.ToString()).ToList() : [];
            if (endorsed.Count > 0 && !endorsed.Contains(channelId)) return false;
        }
        return o.ServiceUrlAllowed(serviceUrl);
    }
}

/// <summary>The Bot Connector calls Lots makes: a token for the bot, a member's e-mail, and a reply (optionally with an Adaptive Card).</summary>
public sealed class TeamsClient(IHttpClientFactory http, IOptions<ChannelOptions> options, TimeProvider clock)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (string Token, DateTimeOffset Until)? _token;

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_token is { } t && t.Until > clock.GetUtcNow().AddMinutes(5)) return t.Token;
            var o = options.Value.Teams;
            using var res = await http.CreateClient(nameof(TeamsClient)).PostAsync(o.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = o.AppId ?? throw new InvalidOperationException("Channels:Teams:AppId is not set."),
                ["client_secret"] = SecretReference.Resolve(o.AppPasswordRef ?? throw new InvalidOperationException("Channels:Teams:AppPasswordRef is not set.")),
                ["scope"] = "https://api.botframework.com/.default",
            }), ct);
            if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Teams token: {(int)res.StatusCode}");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var token = doc.RootElement.GetProperty("access_token").GetString()!;
            _token = (token, clock.GetUtcNow().AddSeconds(doc.RootElement.TryGetProperty("expires_in", out var s) ? s.GetInt32() : 3600));
            return token;
        }
        finally { _lock.Release(); }
    }

    private async Task<HttpClient> ConnectorAsync(string serviceUrl, CancellationToken ct)
    {
        if (!options.Value.Teams.ServiceUrlAllowed(serviceUrl)) throw new InvalidOperationException("The Teams service URL is not allowed.");
        var c = http.CreateClient(nameof(TeamsClient));
        c.BaseAddress = new Uri(serviceUrl.TrimEnd('/') + "/");
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        return c;
    }

    /// <summary>The member's e-mail (or user principal name) from the conversation roster.</summary>
    public async Task<string?> EmailOfAsync(string serviceUrl, string conversationId, string memberId, CancellationToken ct)
    {
        var c = await ConnectorAsync(serviceUrl, ct);
        using var res = await c.GetAsync($"v3/conversations/{Uri.EscapeDataString(conversationId)}/members/{Uri.EscapeDataString(memberId)}", ct);
        if (!res.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("email", out var e) && e.GetString() is { Length: > 0 } email ? email
            : doc.RootElement.TryGetProperty("userPrincipalName", out var upn) ? upn.GetString() : null;
    }

    public async Task ReplyAsync(string serviceUrl, string conversationId, string? replyToId, string text, object? card, CancellationToken ct)
    {
        var c = await ConnectorAsync(serviceUrl, ct);
        var path = $"v3/conversations/{Uri.EscapeDataString(conversationId)}/activities" + (string.IsNullOrEmpty(replyToId) ? "" : "/" + Uri.EscapeDataString(replyToId));
        object[]? attachments = card is null ? null : [new { contentType = "application/vnd.microsoft.card.adaptive", content = card }];
        using var res = await c.PostAsJsonAsync(path, new { type = "message", text, textFormat = "markdown", replyToId, attachments }, ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Teams: {(int)res.StatusCode}");
    }

    /// <summary>An approval request as an Adaptive Card with Approve/Deny (Action.Execute): a click is an invoke Lots decides as the clicker.</summary>
    public static object ApprovalCard(string text, string approvalId, string? link) => new Dictionary<string, object>
    {
        ["type"] = "AdaptiveCard", ["version"] = "1.4", ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
        ["body"] = new object[] { new { type = "TextBlock", text, wrap = true } },
        ["actions"] = new List<object>
        {
            new { type = "Action.Execute", title = "Approve", verb = "lots_approve", style = "positive", data = new { approvalId } },
            new { type = "Action.Execute", title = "Deny", verb = "lots_deny", style = "destructive", data = new { approvalId } },
        }.Concat(link is { Length: > 0 } ? [new { type = "Action.OpenUrl", title = "Open in Lots", url = link }] : Array.Empty<object>()).ToArray(),
    };
}

public static class TeamsIdentity
{
    /// <summary>
    /// The Lots user behind a Teams member: by Entra object id when configured (Entra is the IdP and its <c>oid</c> is the Lots user id),
    /// otherwise by the member's e-mail as for Slack. Unknown, stale or ambiguous = nobody.
    /// </summary>
    public static async Task<Principal?> ResolveAsync(LotsDbContext db, TeamsClient teams, ChannelOptions o, string serviceUrl, string conversationId,
        JsonElement from, DateTimeOffset now, CancellationToken ct)
    {
        if (o.Teams.MatchObjectId && from.TryGetProperty("aadObjectId", out var oid) && oid.GetString() is { Length: > 0 } objectId)
        {
            var since = now.AddDays(-o.IdentityMaxAgeDays);
            var p = await db.UserProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == objectId && x.LastSeenAt > since, ct);
            if (p is not null) return new Principal(p.UserId, p.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        var email = from.TryGetProperty("id", out var id) ? await teams.EmailOfAsync(serviceUrl, conversationId, id.GetString()!, ct) : null;
        return await ChannelIdentity.ByEmailAsync(db, email, o.IdentityMaxAgeDays, now, ct);
    }
}
