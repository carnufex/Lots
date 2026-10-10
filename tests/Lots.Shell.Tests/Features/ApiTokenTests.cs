using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lots.Shell.Core.Security;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

/// <summary>#88: API tokens act as their creator, limited by scopes and expiry; security headers are on every response.</summary>
public class ApiTokenTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbName = Guid.NewGuid().ToString();

    public ApiTokenTests(WebApplicationFactory<Program> factory) => _factory = factory.WithWebHostBuilder(b =>
    {
        b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:MigrateOnStartup"] = "false",
            ["Auth:Mode"] = "Dev",
            ["ConnectionStrings:Lots"] = "Host=none",
            ["Auth:Dev:AllowHeaders"] = "true",
            ["Auth:ApiTokens:MaxDays"] = "30",
        }));
        b.ConfigureServices(s =>
        {
            s.RemoveAll<DbContextOptions<LotsDbContext>>();
            s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
            s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(_dbName));
            s.AddSingleton(TestProfiles.Registry());
        });
    });

    private sealed record Created(string Token, Info Info);
    private sealed record Info(Guid Id, string Name, string Hint, List<string> Scopes, List<string> Roles, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);

    private static HttpRequestMessage As(HttpMethod method, string url, string user, string roles, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Dev-User", user);
        req.Headers.Add("X-Dev-Roles", roles);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static HttpRequestMessage WithToken(HttpMethod method, string url, string token, object? body = null)
    {
        var req = new HttpRequestMessage(method, url) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } };
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private async Task<Created> CreateAsync(HttpClient client, string user, string roles, string[] scopes, int days = 7)
    {
        var res = await client.SendAsync(As(HttpMethod.Post, "/me/tokens", user, roles, new { name = "ci", scopes, expiresInDays = days }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<Created>())!;
    }

    [Fact]
    public async Task A_token_acts_as_its_creator_and_is_stored_only_as_a_hash()
    {
        var client = _factory.CreateClient();
        var created = await CreateAsync(client, "claude-test-tok", "operator", ["read", "runs"]);
        Assert.StartsWith(ApiTokens.Prefix, created.Token);
        Assert.Equal(["operator"], created.Info.Roles);

        var started = await client.SendAsync(WithToken(HttpMethod.Post, "/runs", created.Token, new { prompt = "hello" }));
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var runs = await client.SendAsync(WithToken(HttpMethod.Get, "/runs", created.Token));
        Assert.Contains("claude-test-tok", await runs.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<LotsDbContext>().ApiTokens.SingleAsync(t => t.Id == created.Info.Id);
        Assert.NotEqual(created.Token, row.Hash);
        Assert.Equal(ApiTokens.Hash(created.Token), row.Hash);
        Assert.NotNull(row.LastUsedAt);
    }

    [Fact]
    public async Task Scopes_limit_what_a_token_can_do()
    {
        var client = _factory.CreateClient();
        var readOnly = await CreateAsync(client, "claude-test-tok2", "operator", ["read"]);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(WithToken(HttpMethod.Get, "/runs", readOnly.Token))).StatusCode);
        var post = await client.SendAsync(WithToken(HttpMethod.Post, "/runs", readOnly.Token, new { prompt = "x" }));
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Contains("runs", await post.Content.ReadAsStringAsync());
        // A token can never mint or list tokens, whatever its scopes.
        var full = await CreateAsync(client, "claude-test-tok2", "operator", ["read", "runs", "approvals", "admin"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(WithToken(HttpMethod.Post, "/me/tokens", full.Token, new { name = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(WithToken(HttpMethod.Get, "/me/tokens", full.Token))).StatusCode);
    }

    [Fact]
    public async Task Revoked_expired_and_unknown_tokens_are_rejected()
    {
        var client = _factory.CreateClient();
        var created = await CreateAsync(client, "claude-test-tok3", "operator", ["read"]);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(As(HttpMethod.Delete, $"/me/tokens/{created.Info.Id}", "claude-test-tok3", "operator"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(WithToken(HttpMethod.Get, "/runs", created.Token))).StatusCode);

        var other = await CreateAsync(client, "claude-test-tok3", "operator", ["read"]);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
            (await db.ApiTokens.SingleAsync(t => t.Id == other.Info.Id)).ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(WithToken(HttpMethod.Get, "/runs", other.Token))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(WithToken(HttpMethod.Get, "/runs", ApiTokens.NewToken()))).StatusCode);
    }

    [Fact]
    public async Task Tokens_always_expire_and_scopes_are_validated()
    {
        var client = _factory.CreateClient();
        var tooLong = await client.SendAsync(As(HttpMethod.Post, "/me/tokens", "claude-test-tok4", "operator", new { name = "x", scopes = new[] { "read" }, expiresInDays = 365 }));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        var badScope = await client.SendAsync(As(HttpMethod.Post, "/me/tokens", "claude-test-tok4", "operator", new { name = "x", scopes = new[] { "root" } }));
        Assert.Equal(HttpStatusCode.BadRequest, badScope.StatusCode);
    }

    [Fact]
    public async Task Only_admins_see_everyones_tokens()
    {
        var client = _factory.CreateClient();
        await CreateAsync(client, "claude-test-tok5", "operator", ["read"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(As(HttpMethod.Get, "/admin/api-tokens", "claude-test-tok5", "operator"))).StatusCode);
        var all = await client.SendAsync(As(HttpMethod.Get, "/admin/api-tokens", "claude-test-admin", "admin"));
        Assert.Contains("claude-test-tok5", await all.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var res = await _factory.CreateClient().GetAsync("/health");
        var csp = res.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("script-src 'self' blob:", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public void The_policy_lets_the_browser_reach_the_identity_provider_only()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Oidc:Authority"] = "https://idp.example/application/o/lots/",
        }).Build();
        var csp = SecurityHeadersMiddleware.BuildCsp(config);
        Assert.Contains("connect-src 'self' https://idp.example", csp);
        Assert.Contains("frame-src https://idp.example", csp);
        Assert.DoesNotContain("/application/o/lots", csp);
    }

    [Theory]
    [InlineData("GET", "/runs", "read")]
    [InlineData("POST", "/runs", "runs")]
    [InlineData("POST", "/runs/1/cancel", "runs")]
    [InlineData("POST", "/approvals/1/approve", "approvals")]
    [InlineData("PUT", "/admin/resources/profile/x", "admin")]
    [InlineData("POST", "/knowledge/sources", "admin")]
    [InlineData("POST", "/me/tokens", null)]
    public void Each_request_needs_a_scope(string method, string path, string? scope) =>
        Assert.Equal(scope, ApiTokens.RequiredScope(method, path));
}
