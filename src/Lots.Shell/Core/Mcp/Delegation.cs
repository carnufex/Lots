using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lots.Shell.Core.Profiles;
using Microsoft.AspNetCore.DataProtection;

namespace Lots.Shell.Core.Mcp;

/// <summary>
/// Who a delegated call is made for, available to the MCP transport through the ambient async context.
/// Holds the user's own login token only to exchange it: the token is never sent to a backend.
/// </summary>
public sealed class DelegationContext(string userId, string? subjectToken, DateTimeOffset? subjectExpires, TimeProvider clock)
{
    private static readonly AsyncLocal<DelegationContext?> Holder = new();

    public static DelegationContext? Current => Holder.Value;

    public string UserId { get; } = userId;

    /// <summary>Makes this the current context until the returned scope is disposed.</summary>
    public static IDisposable Enter(DelegationContext context)
    {
        var previous = Holder.Value;
        Holder.Value = context;
        return new Scope(previous);
    }

    public string RequireSubjectToken()
    {
        if (string.IsNullOrEmpty(subjectToken))
            throw new InvalidOperationException("Delegated access needs your login token, which this run does not have (use OIDC login).");
        if (subjectExpires is { } e && clock.GetUtcNow() >= e)
            throw new InvalidOperationException("Your login token has expired, so delegated access is no longer possible. Start a new run.");
        return subjectToken;
    }

    private sealed class Scope(DelegationContext? previous) : IDisposable
    {
        public void Dispose() => Holder.Value = previous;
    }
}

/// <summary>RFC 8693 token exchange: trades the user's token for one scoped to a backend's audience.</summary>
public sealed class TokenExchangeClient(HttpClient http, TimeProvider clock, Func<string, string?>? env = null)
{
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(30);

    private readonly Func<string, string?> _env = env ?? Environment.GetEnvironmentVariable;
    private readonly Dictionary<string, (string Token, DateTimeOffset Expires)> _cache = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> ExchangeAsync(string userId, McpServerConfig server, string subjectToken, CancellationToken ct)
    {
        var c = server.Credentials
                ?? throw new InvalidOperationException($"Server '{server.Name}' has no token-exchange credentials.");
        var key = $"{server.Name}|{userId}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subjectToken)))}";

        await _gate.WaitAsync(ct);
        // A span per exchange (#139): outcome and whether the cache answered, never the token or the subject.
        using var span = Telemetry.Tracing.Source.StartActivity("token_exchange", System.Diagnostics.ActivityKind.Client);
        span?.SetTag("lots.server", server.Name);
        try
        {
            if (_cache.TryGetValue(key, out var hit) && clock.GetUtcNow() < hit.Expires - Skew)
            {
                span?.SetTag("lots.token_exchange.cached", true);
                return hit.Token;
            }

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
                ["subject_token"] = subjectToken,
                ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
                ["requested_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
                ["audience"] = c.Audience!,
                ["client_id"] = c.ClientId!,
            };
            if (!string.IsNullOrEmpty(c.Scope)) form["scope"] = c.Scope;
            if (!string.IsNullOrEmpty(c.ClientSecretEnv))
                form["client_secret"] = SecretReference.Resolve(c.ClientSecretEnv, _env);

            using var response = await http.PostAsync(c.TokenUrl, new FormUrlEncodedContent(form), ct);
            span?.SetTag("http.response.status_code", (int)response.StatusCode);
            if (!response.IsSuccessStatusCode)
            {
                span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, "refused");
                throw new HttpRequestException($"Token exchange for server '{server.Name}' was refused ({(int)response.StatusCode}).");
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var token = doc.RootElement.GetProperty("access_token").GetString()
                        ?? throw new InvalidOperationException("Token exchange returned no access_token.");
            Security.SecretRedactor.RegisterToken(token);
            var lifetime = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 300;
            _cache[key] = (token, clock.GetUtcNow().AddSeconds(lifetime));
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Attaches the exchanged, per-user token of the current delegation context to every request.</summary>
public sealed class DelegatedBearerHandler(McpServerConfig server, TokenExchangeClient exchange, HttpMessageHandler? inner = null) : DelegatingHandler(inner ?? new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var context = DelegationContext.Current
                      ?? throw new InvalidOperationException("A delegated call was made without a user context.");
        var token = await exchange.ExchangeAsync(context.UserId, server, context.RequireSubjectToken(), ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, ct);
    }
}

/// <summary>Keeps a user's login token on a run, encrypted at rest, only while the run needs it.</summary>
public sealed class SubjectTokenVault(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Lots.Run.SubjectToken");

    public string Protect(string token) => _protector.Protect(token);

    /// <summary>Returns null if the value cannot be decrypted (e.g. the key ring was lost).</summary>
    public string? Unprotect(string? protectedToken)
    {
        if (string.IsNullOrEmpty(protectedToken)) return null;
        try { return _protector.Unprotect(protectedToken); }
        catch (CryptographicException) { return null; }
    }
}
