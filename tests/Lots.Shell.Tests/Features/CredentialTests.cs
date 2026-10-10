using System.Net;
using System.Text;
using Lots.Shell.Core.Mcp;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Lots.Shell.Tests.Features;

public class SecretReferenceTests
{
    [Fact]
    public void Env_and_file_references_resolve_and_a_rotated_file_is_read_again()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "first\n");
            Assert.Equal("first", SecretReference.Resolve("file:" + file));
            File.WriteAllText(file, "rotated");
            Assert.Equal("rotated", SecretReference.Resolve("file:" + file));
            Assert.Equal("v", SecretReference.Resolve("MY_SECRET", n => n == "MY_SECRET" ? "v" : null));
            Assert.Equal("v", SecretReference.Resolve("env:MY_SECRET", n => n == "MY_SECRET" ? "v" : null));
            var missing = Assert.Throws<InvalidOperationException>(() => SecretReference.Resolve("NOPE", _ => null));
            Assert.DoesNotContain("v", missing.Message.Replace("Environment variable", "").Replace("not set", "").Replace("(referenced by the profile)", "").Replace("NOPE", ""));
            Assert.Equal("file /run/secrets/x", SecretReference.Describe("file:/run/secrets/x"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_bearer_token_from_a_file_rotates_without_restart_and_use_is_recorded()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "t1");
            var status = new CredentialStatusRegistry();
            var provider = new BackendTokenProvider(new ServerCredentials(CredentialTypes.Bearer, TokenEnv: "file:" + file), new HttpClient(), TimeProvider.System,
                server: "s", status: status);
            Assert.Equal("t1", await provider.GetTokenAsync(default));
            File.WriteAllText(file, "t2");
            Assert.Equal("t2", await provider.GetTokenAsync(default));
            Assert.NotNull(status.Get("s").LastUsed);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void User_connected_servers_need_oauth_user_credentials_with_an_https_authorize_url()
    {
        const string Ok = "name: p\nversion: 1\nservers:\n  - name: gh\n    url: http://mcp-gh/mcp\n    auth: user-connected\n    credentials:\n" +
                          "      type: oauth-user\n      authorizeUrl: https://idp.example/authorize\n      tokenUrl: https://idp.example/token\n      clientId: lots\n";
        Assert.Equal(CredentialTypes.OAuthUser, ProfileParser.Parse(Ok).Servers[0].Credentials!.Type);
        Assert.Throws<ProfileException>(() => ProfileParser.Parse(Ok.Replace("https://idp.example/authorize", "http://idp.example/authorize")));
        Assert.Throws<ProfileException>(() => ProfileParser.Parse(Ok.Replace("type: oauth-user", "type: bearer\n      tokenEnv: X")));
    }
}

public class UserConnectionTests
{
    /// <summary>A token endpoint: codes and refresh tokens it issued give a fresh access token.</summary>
    private sealed class TokenEndpoint : HttpMessageHandler, IHttpClientFactory
    {
        public List<Dictionary<string, string>> Requests { get; } = [];
        private int _n;

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&')
                .Select(p => p.Split('=')).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
            Requests.Add(form);
            var ok = form["grant_type"] switch
            {
                "authorization_code" => form["code"] == "good-code" && form.ContainsKey("code_verifier"),
                "refresh_token" => form["refresh_token"] == "refresh-1",
                _ => false,
            };
            if (!ok) return new HttpResponseMessage(HttpStatusCode.BadRequest);
            _n++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"access_token":"access-{{_n}}","refresh_token":"refresh-1","expires_in":3600,"scope":"repo"}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (UserConnections Connections, TokenEndpoint Endpoint, FakeTimeProvider Clock) Setup()
    {
        var services = new ServiceCollection();
        var dbName = Guid.NewGuid().ToString();
        services.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(dbName));
        var provider = services.BuildServiceProvider();
        var endpoint = new TokenEndpoint();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var profiles = new ProfileRegistry([ProfileParser.Parse(
            "name: p\nversion: 1\nservers:\n  - name: gh\n    url: http://mcp-gh/mcp\n    auth: user-connected\n    credentials:\n" +
            "      type: oauth-user\n      authorizeUrl: https://idp.example/authorize\n      tokenUrl: https://idp.example/token\n      clientId: lots\n      scope: repo\n")]);
        var connections = new UserConnections(provider.GetRequiredService<IServiceScopeFactory>(), new EphemeralDataProtectionProvider(), endpoint, profiles, clock,
            NullLogger<UserConnections>.Instance);
        return (connections, endpoint, clock);
    }

    private static string StateOf(string url) =>
        Uri.UnescapeDataString(new Uri(url).Query.TrimStart('?').Split('&').First(p => p.StartsWith("state=")).Split('=')[1]);

    [Fact]
    public async Task A_user_connects_once_and_calls_use_and_refresh_only_their_own_token()
    {
        var (connections, endpoint, clock) = Setup();

        var url = connections.Start("alice", "gh", "https://lots.example/integrations/connections/callback");
        Assert.StartsWith("https://idp.example/authorize?response_type=code&client_id=lots", url);
        Assert.Contains("code_challenge_method=S256", url);

        Assert.Equal("gh", await connections.CompleteAsync("good-code", StateOf(url), default));
        Assert.Equal("access-1", await connections.GetTokenAsync("alice", "gh", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connections.GetTokenAsync("bob", "gh", default));

        clock.Advance(TimeSpan.FromHours(2)); // expired: refreshed with the refresh token
        Assert.Equal("access-2", await connections.GetTokenAsync("alice", "gh", default));
        Assert.Equal("refresh_token", endpoint.Requests[^1]["grant_type"]);

        Assert.True(await connections.DisconnectAsync("alice", "gh", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connections.GetTokenAsync("alice", "gh", default));
    }

    [Fact]
    public async Task A_tampered_or_expired_state_is_refused()
    {
        var (connections, _, clock) = Setup();
        var state = StateOf(connections.Start("alice", "gh", "https://lots.example/cb"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => connections.CompleteAsync("good-code", state[..^4] + "AAAA", default));
        clock.Advance(TimeSpan.FromMinutes(11));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connections.CompleteAsync("good-code", state, default));
        Assert.Throws<InvalidOperationException>(() => connections.Start("alice", "not-a-server", "https://lots.example/cb"));
    }
}
