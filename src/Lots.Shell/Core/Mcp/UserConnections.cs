using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Core.Mcp;

/// <summary>
/// Per-user OAuth connections (#62, ADR 0004 strategy "user-connected"): a user connects their own account at a backend once
/// (authorization code + PKCE); calls to that server are then made with that user's token, refreshed as needed. Tokens are stored
/// encrypted (Data Protection), per user and server, and deleted on disconnect. Nobody else's runs can use them.
/// </summary>
public sealed class UserConnections(IServiceScopeFactory scopes, IDataProtectionProvider protection, IHttpClientFactory http, ProfileRegistry profiles,
    TimeProvider clock, ILogger<UserConnections> logger)
{
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(60);
    private readonly IDataProtector _tokens = protection.CreateProtector("Lots.UserConnection.Tokens");
    private readonly IDataProtector _state = protection.CreateProtector("Lots.UserConnection.State");

    private sealed record State(string User, string Server, long ExpiresMs, string Verifier, string RedirectUri);

    public McpServerConfig RequireServer(string name) =>
        profiles.Servers.FirstOrDefault(s => s.Name == name && s.Auth == AuthStrategies.UserConnected && s.Credentials?.Type == CredentialTypes.OAuthUser)
        ?? throw new InvalidOperationException($"'{name}' is not a server users connect their own account to.");

    /// <summary>The provider's authorization URL to send the user to. State carries the PKCE verifier, encrypted and short-lived.</summary>
    public string Start(string user, string serverName, string redirectUri)
    {
        var server = RequireServer(serverName);
        var c = server.Credentials!;
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = _state.Protect(JsonSerializer.Serialize(new State(user, server.Name, clock.GetUtcNow().Add(StateLifetime).ToUnixTimeMilliseconds(), verifier, redirectUri)));
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code", ["client_id"] = c.ClientId, ["redirect_uri"] = redirectUri, ["state"] = state,
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["scope"] = c.Scope,
        };
        return c.AuthorizeUrl + (c.AuthorizeUrl!.Contains('?') ? "&" : "?") +
               string.Join("&", query.Where(kv => kv.Value is not null).Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value!)}"));
    }

    /// <summary>Finishes the authorization code flow. Returns the server name. The state proves who started it.</summary>
    public async Task<string> CompleteAsync(string code, string protectedState, CancellationToken ct)
    {
        State state;
        try { state = JsonSerializer.Deserialize<State>(_state.Unprotect(protectedState))!; }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { throw new InvalidOperationException("The connection request is invalid or was tampered with."); }
        if (clock.GetUtcNow().ToUnixTimeMilliseconds() > state.ExpiresMs) throw new InvalidOperationException("The connection request expired; start again.");
        var server = RequireServer(state.Server);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = state.RedirectUri, ["code_verifier"] = state.Verifier,
        };
        var tokens = await TokenRequestAsync(server, form, ct);
        await SaveAsync(state.User, server.Name, tokens, ct);
        return server.Name;
    }

    public async Task<string> GetTokenAsync(string user, string serverName, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var row = await db.UserCredentials.SingleOrDefaultAsync(c => c.UserId == user && c.Server == serverName, ct)
                  ?? throw new InvalidOperationException($"Connect your account for '{serverName}' first (Integrations > Credentials).");
        if (row.ExpiresAt is { } exp && clock.GetUtcNow() < exp - Skew && Unprotect(row.AccessTokenProtected) is { } access)
        {
            row.LastUsedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return access;
        }
        if (Unprotect(row.RefreshTokenProtected) is not { } refresh)
            throw new InvalidOperationException($"Your connection to '{serverName}' expired; connect your account again.");
        try
        {
            var tokens = await TokenRequestAsync(RequireServer(serverName), new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh }, ct);
            Apply(row, tokens);
            row.LastUsedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return tokens.Access;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Refreshing {User}'s connection to {Server} failed: {Error}", user, serverName, ex.Message);
            throw new InvalidOperationException($"Your connection to '{serverName}' could not be refreshed; connect your account again.");
        }
    }

    public async Task<bool> DisconnectAsync(string user, string serverName, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var row = await db.UserCredentials.SingleOrDefaultAsync(c => c.UserId == user && c.Server == serverName, ct);
        if (row is null) return false;
        db.UserCredentials.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private sealed record Tokens(string Access, string? Refresh, DateTimeOffset? Expires, string? Scope);

    private async Task<Tokens> TokenRequestAsync(McpServerConfig server, Dictionary<string, string> form, CancellationToken ct)
    {
        var c = server.Credentials!;
        form["client_id"] = c.ClientId!;
        if (!string.IsNullOrEmpty(c.ClientSecretEnv)) form["client_secret"] = SecretReference.Resolve(c.ClientSecretEnv);
        using var res = await http.CreateClient(nameof(UserConnections)).PostAsync(c.TokenUrl, new FormUrlEncodedContent(form), ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"the token endpoint answered {(int)res.StatusCode}");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var r = doc.RootElement;
        var access = r.GetProperty("access_token").GetString() ?? throw new HttpRequestException("no access_token");
        Security.SecretRedactor.RegisterToken(access);
        var refresh = r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        DateTimeOffset? expires = r.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? clock.GetUtcNow().AddSeconds(s) : null;
        return new Tokens(access, refresh, expires, r.TryGetProperty("scope", out var sc) ? sc.GetString() : null);
    }

    private async Task SaveAsync(string user, string server, Tokens tokens, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var row = await db.UserCredentials.SingleOrDefaultAsync(c => c.UserId == user && c.Server == server, ct);
        if (row is null) db.UserCredentials.Add(row = new UserCredentialRecord { UserId = user, Server = server });
        row.ConnectedAt = clock.GetUtcNow();
        Apply(row, tokens);
        await db.SaveChangesAsync(ct);
    }

    private void Apply(UserCredentialRecord row, Tokens t)
    {
        row.AccessTokenProtected = _tokens.Protect(t.Access);
        if (t.Refresh is not null) row.RefreshTokenProtected = _tokens.Protect(t.Refresh); // providers may not rotate it
        row.ExpiresAt = t.Expires;
        row.Scope = t.Scope ?? row.Scope;
    }

    private string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try { return _tokens.Unprotect(value); }
        catch (CryptographicException) { return null; }
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Attaches the calling user's own connected-account token to every request to a user-connected server.</summary>
public sealed class UserConnectedBearerHandler(McpServerConfig server, UserConnections connections, HttpMessageHandler? inner = null) : DelegatingHandler(inner ?? new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var context = DelegationContext.Current ?? throw new InvalidOperationException("A user-connected call was made without a user context.");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await connections.GetTokenAsync(context.UserId, server.Name, ct));
        return await base.SendAsync(request, ct);
    }
}
