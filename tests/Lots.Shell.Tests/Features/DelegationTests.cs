using System.Net;
using System.Text;
using Lots.Shell.Core.Mcp;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Runs;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Server;

namespace Lots.Shell.Tests.Features;

[McpServerToolType]
public sealed class WhoAmITools
{
    [McpServerTool(ReadOnly = true), System.ComponentModel.Description("Returns the Authorization header the backend received")]
    public static string WhoAmI(IHttpContextAccessor http) => http.HttpContext!.Request.Headers.Authorization.ToString();
}

public class DelegationTests
{
    /// <summary>A fake STS: exchanges "subject-of-X" for "exchanged-for-X" and records what it was asked.</summary>
    private sealed class Sts : HttpMessageHandler
    {
        public List<Dictionary<string, string>> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&')
                .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
            Requests.Add(form);
            var token = "exchanged-for-" + form["subject_token"].Replace("subject-of-", "");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"access_token":"{{token}}","expires_in":3600}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static readonly ServerCredentials Exchange = new(
        CredentialTypes.TokenExchange, TokenUrl: "https://sts.test/token", ClientId: "lots", ClientSecretEnv: "LOTS_STS_SECRET",
        Audience: "backend", Scope: "read");

    private static DelegationContext Ctx(string user, FakeTimeProvider clock, string? subject = null, DateTimeOffset? expires = null) =>
        new(user, subject ?? $"subject-of-{user}", expires, clock);

    [Fact]
    public async Task Exchange_sends_the_rfc8693_request_and_caches_per_user_and_token()
    {
        var sts = new Sts();
        var clock = new FakeTimeProvider();
        var client = new TokenExchangeClient(new HttpClient(sts), clock, n => n == "LOTS_STS_SECRET" ? "s3cret" : null);
        var server = new McpServerConfig("b", "http://b/mcp", AuthStrategies.Delegated, Exchange);

        Assert.Equal("exchanged-for-alice", await client.ExchangeAsync("alice", server, "subject-of-alice", default));
        Assert.Equal("exchanged-for-alice", await client.ExchangeAsync("alice", server, "subject-of-alice", default));
        Assert.Equal("exchanged-for-bob", await client.ExchangeAsync("bob", server, "subject-of-bob", default));

        Assert.Equal(2, sts.Requests.Count); // alice was cached
        var r = sts.Requests[0];
        Assert.Equal("urn:ietf:params:oauth:grant-type:token-exchange", r["grant_type"]);
        Assert.Equal("urn:ietf:params:oauth:token-type:access_token", r["subject_token_type"]);
        Assert.Equal("backend", r["audience"]);
        Assert.Equal("lots", r["client_id"]);
        Assert.Equal("s3cret", r["client_secret"]);
        Assert.Equal("read", r["scope"]);
    }

    [Fact]
    public async Task Subject_token_must_be_present_and_unexpired()
    {
        var clock = new FakeTimeProvider();
        Assert.Throws<InvalidOperationException>(() =>
            new DelegationContext("alice", null, null, clock).RequireSubjectToken());

        var expiring = Ctx("alice", clock, expires: clock.GetUtcNow().AddMinutes(1));
        Assert.Equal("subject-of-alice", expiring.RequireSubjectToken());
        clock.Advance(TimeSpan.FromMinutes(2));
        var ex = Assert.Throws<InvalidOperationException>(() => expiring.RequireSubjectToken());
        Assert.Contains("expired", ex.Message);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_delegated_server_receives_a_per_user_exchanged_token_and_never_the_original()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["urls"] = "http://127.0.0.1:0";
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<WhoAmITools>();
        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/') + "/mcp";

        var sts = new Sts();
        var clock = new FakeTimeProvider();
        var exchange = new TokenExchangeClient(new HttpClient(sts), clock, _ => "secret");
        var server = new McpServerConfig("b", url, AuthStrategies.Delegated, Exchange);
        await using var source = new McpToolSource([server], NullLoggerFactory.Instance, clock, exchange);

        // No user context: the delegated server offers nothing (and nothing is sent anywhere).
        Assert.Empty(await source.ListAsync(default));

        string aliceSaw, bobSaw;
        using (DelegationContext.Enter(Ctx("alice", clock)))
        {
            var tools = await source.ListAsync(default);
            Assert.Equal(["who_am_i"], tools.Select(t => t.Name));
            aliceSaw = await source.CallAsync("who_am_i", "{}", default);
        }
        using (DelegationContext.Enter(Ctx("bob", clock)))
            bobSaw = await source.CallAsync("who_am_i", "{}", default);

        Assert.Equal("Bearer exchanged-for-alice", aliceSaw);
        Assert.Equal("Bearer exchanged-for-bob", bobSaw);
        Assert.DoesNotContain(sts.Requests, r => r["audience"] != "backend");
        Assert.DoesNotContain("subject-of", aliceSaw + bobSaw); // the user's own token never reaches the backend
    }

    [Fact]
    public async Task An_expired_subject_token_makes_the_delegated_call_fail_cleanly()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["urls"] = "http://127.0.0.1:0";
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<WhoAmITools>();
        await using var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/') + "/mcp";

        var clock = new FakeTimeProvider();
        var exchange = new TokenExchangeClient(new HttpClient(new Sts()), clock, _ => "secret");
        await using var source = new McpToolSource(
            [new McpServerConfig("b", url, AuthStrategies.Delegated, Exchange)], NullLoggerFactory.Instance, clock, exchange);
        var registry = new ProfileRegistry([new Profile("p", 1, "", "", [new McpServerConfig("b", url, AuthStrategies.Delegated, Exchange)],
            [new ProfileTool("who_am_i", ToolRisk.Read)], [new ProfileRole("operator", [ToolRisk.Read], [])])]);
        var invoker = new ToolInvoker([source], registry);

        var live = Ctx("alice", clock, expires: clock.GetUtcNow().AddMinutes(10));
        using var scope = DelegationContext.Enter(live);
        var user = new Principal("alice", ["operator"]);

        Assert.Equal("Bearer exchanged-for-alice", await invoker.InvokeAsync(new ToolCall("1", "who_am_i", "{}"), user, "p", default));

        using var expired = DelegationContext.Enter(Ctx("alice", clock, expires: clock.GetUtcNow().AddMinutes(-1)));
        var result = await invoker.InvokeAsync(new ToolCall("2", "who_am_i", "{}"), user, "p", default);
        Assert.StartsWith("Error: tool 'who_am_i' failed", result);
        Assert.Contains("expired", result);
    }

    [Fact]
    public void Profiles_tie_delegated_auth_to_token_exchange_credentials()
    {
        const string valid = """
            name: p
            version: 1
            servers:
              - name: s
                url: https://s/mcp
                auth: delegated
                credentials:
                  type: token-exchange
                  tokenUrl: https://idp/token
                  clientId: lots
                  clientSecretEnv: STS_SECRET
                  audience: s
            """;
        var p = ProfileParser.Parse(valid);
        var c = p.Servers.Single().Credentials!;
        Assert.Equal(("s", "STS_SECRET"), (c.Audience, c.ClientSecretEnv));

        var noCreds = Assert.Throws<ProfileException>(() => ProfileParser.Parse("name: p\nversion: 1\nservers:\n  - name: s\n    url: https://s/mcp\n    auth: delegated\n"));
        Assert.Contains(noCreds.Errors, e => e.Contains("delegated and needs token-exchange credentials"));

        var wrongAuth = Assert.Throws<ProfileException>(() => ProfileParser.Parse(valid.Replace("auth: delegated", "auth: shared-service-account")));
        Assert.Contains(wrongAuth.Errors, e => e.Contains("require auth: delegated"));

        var missing = Assert.Throws<ProfileException>(() => ProfileParser.Parse(valid.Replace("audience: s", "# audience removed")));
        Assert.Contains(missing.Errors, e => e.Contains("audience"));
    }

    [Fact]
    public async Task Run_keeps_the_login_token_encrypted_while_running_and_clears_it_when_finished()
    {
        var vault = new SubjectTokenVault(new EphemeralDataProtectionProvider());
        var protectedToken = vault.Protect("subject-of-alice");
        Assert.DoesNotContain("subject-of-alice", protectedToken);
        Assert.Equal("subject-of-alice", vault.Unprotect(protectedToken));
        Assert.Null(vault.Unprotect("garbage"));                                   // unreadable: no token, no crash
        Assert.Null(new SubjectTokenVault(new EphemeralDataProtectionProvider()).Unprotect(protectedToken)); // other key ring

        var db = new LotsDbContext(new DbContextOptionsBuilder<LotsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var run = new RunRecord
        {
            Id = Guid.NewGuid(), Prompt = "q", Profile = "p", UserId = "alice", Roles = "operator", CreatedAt = DateTimeOffset.UtcNow,
            SubjectTokenProtected = protectedToken, SubjectTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync();

        var registry = new ProfileRegistry([new Profile("p", 1, "", "", [], [], [new ProfileRole("operator", [ToolRisk.Read], [])])]);
        var model = new AnswerModel();
        var runner = new AgentRunner(db, model, new ToolInvoker([], registry), registry, Options.Create(new AgentOptions()),
            TimeProvider.System, vault: vault);

        await runner.ExecuteAsync(run.Id, default);

        var done = await db.Runs.SingleAsync();
        Assert.Equal(RunStatus.Completed, done.Status);
        Assert.Null(done.SubjectTokenProtected); // gone once the run is over
        Assert.Null(done.SubjectTokenExpiresAt);
    }

    private sealed class AnswerModel : IModelClient
    {
        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, CancellationToken ct) =>
            Task.FromResult(new ModelResponse(new ChatMessage("assistant", "done"), "stop", new ModelUsage(1, 1), TimeSpan.FromMilliseconds(1)));
    }
}
