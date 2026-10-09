using System.Net.Http.Headers;
using System.Text.Json;
using Lots.Shell.Core.Profiles;

namespace Lots.Shell.Core.Mcp;

/// <summary>Supplies the bearer token the shell uses towards one MCP server.</summary>
public interface IBackendTokenProvider
{
    Task<string> GetTokenAsync(CancellationToken ct);
}

/// <summary>
/// Gets tokens for a server from its profile <see cref="ServerCredentials"/>. Secrets are read from the
/// environment variables the profile names; they are never part of the profile, logs or error messages.
/// </summary>
public sealed class BackendTokenProvider(
    ServerCredentials credentials, HttpClient http, TimeProvider clock, Func<string, string?>? env = null) : IBackendTokenProvider
{
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(60);

    private readonly Func<string, string?> _env = env ?? Environment.GetEnvironmentVariable;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (credentials.Type == CredentialTypes.Bearer)
            return Secret(credentials.TokenEnv!);

        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && clock.GetUtcNow() < _expires - Skew) return _token;

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = credentials.ClientId!,
            };
            if (!string.IsNullOrEmpty(credentials.Username)) form["username"] = credentials.Username;
            if (!string.IsNullOrEmpty(credentials.PasswordEnv)) form["password"] = Secret(credentials.PasswordEnv);
            if (!string.IsNullOrEmpty(credentials.Scope)) form["scope"] = credentials.Scope;

            using var response = await http.PostAsync(credentials.TokenUrl, new FormUrlEncodedContent(form), ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Token endpoint {credentials.TokenUrl} returned {(int)response.StatusCode}.");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            _token = doc.RootElement.GetProperty("access_token").GetString()
                     ?? throw new InvalidOperationException("Token endpoint returned no access_token.");
            var lifetime = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 300;
            _expires = clock.GetUtcNow().AddSeconds(lifetime);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string Secret(string envName) =>
        _env(envName) is { Length: > 0 } v
            ? v
            : throw new InvalidOperationException($"Environment variable '{envName}' (referenced by the profile) is not set.");
}

/// <summary>Adds the current backend token to every request to an MCP server.</summary>
public sealed class BearerHandler(IBackendTokenProvider tokens) : DelegatingHandler(new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(ct));
        return await base.SendAsync(request, ct);
    }
}
