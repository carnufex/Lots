using System.Net;
using System.Text;
using Lots.Shell.Core.Mcp;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Lots.Shell.Tests.Features;

public class BackendCredentialsTests
{
    private sealed class TokenEndpoint(params string[] tokens) : HttpMessageHandler
    {
        public List<string> Forms { get; } = [];
        private int _n;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Forms.Add(await request.Content!.ReadAsStringAsync(ct));
            var token = tokens[Math.Min(_n++, tokens.Length - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"access_token":"{{token}}","expires_in":3600}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static readonly ServerCredentials Cc = new(
        CredentialTypes.OAuthClientCredentials, TokenUrl: "https://idp.test/token", ClientId: "agents",
        Username: "svc", PasswordEnv: "SVC_PW", Scope: "openid");

    private static Func<string, string?> Env(string? value = "s3cret") => _ => value;

    [Fact]
    public async Task Client_credentials_are_posted_and_the_token_is_cached_until_it_nearly_expires()
    {
        var endpoint = new TokenEndpoint("t1", "t2");
        var clock = new FakeTimeProvider();
        var provider = new BackendTokenProvider(Cc, new HttpClient(endpoint), clock, Env());

        Assert.Equal("t1", await provider.GetTokenAsync(default));
        Assert.Equal("t1", await provider.GetTokenAsync(default));
        Assert.Single(endpoint.Forms);
        Assert.Contains("grant_type=client_credentials", endpoint.Forms[0]);
        Assert.Contains("client_id=agents", endpoint.Forms[0]);
        Assert.Contains("username=svc", endpoint.Forms[0]);
        Assert.Contains("password=s3cret", endpoint.Forms[0]);

        clock.Advance(TimeSpan.FromMinutes(59).Add(TimeSpan.FromSeconds(30))); // inside the 60 s safety margin
        Assert.Equal("t2", await provider.GetTokenAsync(default));
        Assert.Equal(2, endpoint.Forms.Count);
    }

    [Fact]
    public async Task Missing_secret_is_reported_by_name_without_leaking_anything()
    {
        var provider = new BackendTokenProvider(Cc, new HttpClient(new TokenEndpoint("t")), new FakeTimeProvider(), Env(null));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetTokenAsync(default));

        Assert.Contains("SVC_PW", ex.Message);
    }

    [Fact]
    public async Task Static_bearer_reads_the_environment_variable()
    {
        var provider = new BackendTokenProvider(
            new ServerCredentials(CredentialTypes.Bearer, TokenEnv: "TOK"), new HttpClient(), new FakeTimeProvider(), n => n == "TOK" ? "abc" : null);

        Assert.Equal("abc", await provider.GetTokenAsync(default));
    }

    [Fact]
    public void Profile_credentials_are_validated_and_never_contain_secrets()
    {
        var ok = ProfileParser.Parse("""
            name: p
            version: 1
            servers:
              - name: s
                url: http://s/mcp
                credentials:
                  type: oauth-client-credentials
                  tokenUrl: https://idp/token
                  clientId: agents
                  username: svc
                  passwordEnv: SVC_PW
            """);
        Assert.Equal("SVC_PW", ok.Servers.Single().Credentials!.PasswordEnv);

        var bad = Assert.Throws<ProfileException>(() => ProfileParser.Parse("""
            name: p
            version: 1
            servers:
              - name: s
                url: http://s/mcp
                credentials:
                  type: password-in-yaml
            """));
        Assert.Contains(bad.Errors, e => e.Contains("unknown type 'password-in-yaml'"));

        var incomplete = Assert.Throws<ProfileException>(() => ProfileParser.Parse("""
            name: p
            version: 1
            servers:
              - name: s
                url: http://s/mcp
                credentials:
                  type: oauth-client-credentials
                  clientId: agents
            """));
        Assert.Contains(incomplete.Errors, e => e.Contains("tokenUrl"));
    }

    [Fact]
    public async Task Mcp_source_authenticates_with_the_server_credentials()
    {
        // An MCP server that only answers requests carrying the right bearer token.
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["urls"] = "http://127.0.0.1:0";
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<SampleTools>();
        await using var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Headers.Authorization != "Bearer good-token") { ctx.Response.StatusCode = 401; return; }
            await next();
        });
        app.MapMcp("/mcp");
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/') + "/mcp";

        var withToken = new McpServerConfig("s", url, Credentials: new ServerCredentials(CredentialTypes.Bearer, TokenEnv: "LOTS_TEST_BEARER"));
        Environment.SetEnvironmentVariable("LOTS_TEST_BEARER", "good-token");
        await using var good = new McpToolSource([withToken], NullLoggerFactory.Instance);
        Assert.Contains("echo", (await good.ListAsync(default)).Select(t => t.Name));

        // Without credentials the server rejects the shell: no tools, no exception.
        await using var anonymous = new McpToolSource([new McpServerConfig("s", url)], NullLoggerFactory.Instance);
        Assert.Empty(await anonymous.ListAsync(default));
    }
}
