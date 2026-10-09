using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Lots.Shell.Tests.Features;

public class OidcAuthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Issuer = "https://issuer.test/application/o/lots/";
    private static readonly SymmetricSecurityKey Key = new(Encoding.UTF8.GetBytes("test-signing-key-test-signing-key-123456"));
    private readonly WebApplicationFactory<Program> _factory;

    public OidcAuthTests(WebApplicationFactory<Program> factory)
    {
        var dbName = Guid.NewGuid().ToString();
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false",
                ["Agent:RunWorkerEnabled"] = "false",
                ["ConnectionStrings:Lots"] = "Host=none",
                ["Auth:Mode"] = "Oidc",
                ["Auth:Oidc:Authority"] = Issuer,
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(dbName));
                var delegated = new Lots.Shell.Core.Profiles.Profile("deleg", 1, "", "",
                    [new Lots.Shell.Core.Profiles.McpServerConfig("b", "http://b/mcp", Lots.Shell.Core.Profiles.AuthStrategies.Delegated,
                        new Lots.Shell.Core.Profiles.ServerCredentials(Lots.Shell.Core.Profiles.CredentialTypes.TokenExchange,
                            TokenUrl: "https://sts/token", ClientId: "lots", Audience: "b"))],
                    [], [new Lots.Shell.Core.Profiles.ProfileRole("operator", [Lots.Shell.Core.Tools.ToolRisk.Read], [])]);
                s.AddSingleton(new Lots.Shell.Core.Profiles.ProfileRegistry(TestProfiles.Registry().All.Concat([delegated])));
                // Stand in for the IdP: fixed signing key, no metadata discovery.
                s.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
                {
                    o.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                    o.TokenValidationParameters.ValidIssuer = Issuer;
                    o.TokenValidationParameters.IssuerSigningKey = Key;
                    o.TokenValidationParameters.ValidateAudience = false;
                });
            });
        });
    }

    private static string Token(string sub, string[]? roles = null, DateTime? expires = null, SecurityKey? key = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Claims = new Dictionary<string, object> { ["sub"] = sub, ["roles"] = roles ?? [] },
            NotBefore = DateTime.UtcNow.AddMinutes(-10),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(key ?? Key, SecurityAlgorithms.HmacSha256),
        });

    private static HttpRequestMessage Req(HttpMethod m, string url, string? token, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    private sealed record Started(Guid Id);

    [Fact]
    public async Task Run_endpoints_need_a_valid_token_but_health_does_not()
    {
        var client = _factory.CreateClient();
        var wrongKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("another-signing-key-another-signing-key-1"));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Req(HttpMethod.Post, "/runs", null, new { prompt = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Req(HttpMethod.Post, "/runs", Token("alice", ["operator"], key: wrongKey), new { prompt = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Req(HttpMethod.Post, "/runs", Token("alice", ["operator"], expires: DateTime.UtcNow.AddMinutes(-5)), new { prompt = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Req(HttpMethod.Get, "/approvals", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Req(HttpMethod.Get, "/audit", null))).StatusCode);
    }

    [Fact]
    public async Task A_run_belongs_to_the_user_in_the_token_and_roles_come_from_the_claim()
    {
        var client = _factory.CreateClient();
        var alice = Token("alice", ["operator"]);

        var start = await client.SendAsync(Req(HttpMethod.Post, "/runs", alice, new { prompt = "hello" }));
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var id = (await start.Content.ReadFromJsonAsync<Started>())!.Id;

        using (var scope = _factory.Services.CreateScope())
        {
            var run = await scope.ServiceProvider.GetRequiredService<LotsDbContext>().Runs.SingleAsync(r => r.Id == id);
            Assert.Equal("alice", run.UserId);
            Assert.Equal("operator", run.Roles);
        }

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Req(HttpMethod.Get, $"/runs/{id}", alice))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Req(HttpMethod.Get, $"/runs/{id}", Token("bob", ["operator"])))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Req(HttpMethod.Get, $"/runs/{id}", Token("root", ["admin"])))).StatusCode);
    }

    [Fact]
    public async Task Login_token_is_kept_encrypted_only_for_runs_that_use_delegated_servers()
    {
        var client = _factory.CreateClient();
        var token = Token("alice", ["operator"]);

        var delegatedRun = (await (await client.SendAsync(Req(HttpMethod.Post, "/runs", token, new { prompt = "x", profile = "deleg" }))).Content.ReadFromJsonAsync<Started>())!.Id;
        var plainRun = (await (await client.SendAsync(Req(HttpMethod.Post, "/runs", token, new { prompt = "x", profile = TestProfiles.Name }))).Content.ReadFromJsonAsync<Started>())!.Id;

        using var scope = _factory.Services.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<LotsDbContext>().Runs;
        var withToken = await runs.SingleAsync(r => r.Id == delegatedRun);
        Assert.False(string.IsNullOrEmpty(withToken.SubjectTokenProtected));
        Assert.DoesNotContain(token, withToken.SubjectTokenProtected!); // encrypted, not the raw JWT
        Assert.NotNull(withToken.SubjectTokenExpiresAt);
        Assert.Null((await runs.SingleAsync(r => r.Id == plainRun)).SubjectTokenProtected);
    }

    [Fact]
    public async Task Audit_is_for_admins_and_auditors_only()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Req(HttpMethod.Get, "/audit", Token("alice", ["operator"])))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Req(HttpMethod.Get, "/audit", Token("dave", ["auditor"])))).StatusCode);
    }

    [Fact]
    public void Startup_fails_without_an_explicit_auth_mode_or_without_an_authority()
    {
        WebApplicationFactory<Program> With(params (string, string?)[] settings) => _factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(settings.ToDictionary(s => s.Item1, s => s.Item2))));

        var noMode = Assert.Throws<InvalidOperationException>(() => With(("Auth:Mode", null)).CreateClient());
        Assert.Contains("Auth:Mode must be set", noMode.Message);

        var noAuthority = Assert.Throws<InvalidOperationException>(() => With(("Auth:Oidc:Authority", "")).CreateClient());
        Assert.Contains("Authority", noAuthority.Message);
    }
}
