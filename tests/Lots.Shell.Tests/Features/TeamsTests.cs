using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lots.Shell.Core.Channels;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Notifications;
using Lots.Shell.Core.Tools;
using Lots.Shell.Features.Channels;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Lots.Shell.Tests.Features;

public class TeamsUnitTests
{
    [Fact]
    public void Replies_only_go_to_allowed_Bot_Connector_hosts()
    {
        var o = new TeamsOptions();
        Assert.True(o.ServiceUrlAllowed("https://smba.trafficmanager.net/emea/"));
        Assert.True(o.ServiceUrlAllowed("https://SMBA.trafficmanager.net/amer"));
        Assert.False(o.ServiceUrlAllowed("http://smba.trafficmanager.net/emea/"));          // never in clear text
        Assert.False(o.ServiceUrlAllowed("https://smba.trafficmanager.net.evil.example/"));  // a lookalike host
        Assert.False(o.ServiceUrlAllowed("https://user@smba.trafficmanager.net/emea/"));
        Assert.False(o.ServiceUrlAllowed("https://evil.example/smba.trafficmanager.net/"));
        Assert.False(o.ServiceUrlAllowed(null));
    }

    [Fact]
    public void The_question_is_the_text_without_the_mention_and_html()
    {
        Assert.Equal("are the containers healthy?", TeamsMessagesEndpoint.TextOf("<at>Lots</at>&nbsp;are the containers <b>healthy</b>?"));
        Assert.Equal("first line\nsecond",TeamsMessagesEndpoint.TextOf("<p>first line</p><p>second</p>"));
        Assert.Equal("", TeamsMessagesEndpoint.TextOf("<at>Lots</at>"));
    }
}

/// <summary>#149: Teams through the Bot Framework, with real JWT validation against a test signing key.</summary>
public class TeamsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string AppId = "11111111-2222-3333-4444-555555555555";
    private const string ServiceUrl = "https://smba.test/emea/";
    private static readonly RSA Key = RSA.Create(2048);
    private static readonly RsaSecurityKey Signing = new(Key) { KeyId = "test-key" };

    /// <summary>The Bot Framework's metadata and keys, the Entra token endpoint and the Bot Connector, all in one fake.</summary>
    private sealed class FakeMicrosoft : HttpMessageHandler
    {
        public List<(string Method, string Url, string Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            lock (Calls) Calls.Add((r.Method.Method, r.RequestUri!.ToString(), body));
            var url = r.RequestUri!.ToString();
            string json;
            if (url.EndsWith("/openidconfiguration")) json = """{"issuer":"https://api.botframework.com","jwks_uri":"https://login.test/keys","id_token_signing_alg_values_supported":["RS256"]}""";
            else if (url.EndsWith("/keys"))
            {
                var p = Key.ExportParameters(false);
                json = JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", use = "sig", kid = "test-key", n = Base64UrlEncoder.Encode(p.Modulus), e = Base64UrlEncoder.Encode(p.Exponent), endorsements = new[] { "msteams" } } } });
            }
            else if (url.Contains("/oauth2/v2.0/token")) json = """{"access_token":"bot-token","expires_in":3600}""";
            else if (url.Contains("/members/"))
                json = url.Contains("29%3Aalice") ? """{"id":"29:alice","email":"alice@example.org"}"""
                    : url.Contains("29%3Acarol") ? """{"id":"29:carol","userPrincipalName":"carol@example.org"}""" : """{"id":"29:x","email":"nobody@example.org"}""";
            else json = """{"id":"reply-1"}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Answer : IModelClient
    {
        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, CancellationToken ct) =>
            Task.FromResult(new ModelResponse(new ChatMessage("assistant", "Everything is healthy."), "stop", new ModelUsage(3, 4), TimeSpan.Zero));
    }

    private readonly WebApplicationFactory<Program> _app;
    private readonly FakeMicrosoft _ms = new();

    public TeamsApiTests(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable("LOTS_TEST_TEAMS_SECRET", "teams-client-secret");
        var db = Guid.NewGuid().ToString();
        _app = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false", ["ConnectionStrings:Lots"] = "Host=none", ["Auth:Mode"] = "Dev", ["Auth:Dev:AllowHeaders"] = "true",
                ["Channels:Teams:Enabled"] = "true", ["Channels:Teams:AppId"] = AppId, ["Channels:Teams:AppPasswordRef"] = "env:LOTS_TEST_TEAMS_SECRET",
                ["Channels:Teams:Profile"] = TestProfiles.Name, ["Channels:Teams:ServiceUrls:0"] = "https://smba.test/",
                ["Channels:Teams:OpenIdMetadataUrl"] = "https://login.test/v1/.well-known/openidconfiguration",
                ["Channels:Teams:TokenUrl"] = "https://login.test/tenant/oauth2/v2.0/token",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(db));
                s.AddSingleton(TestProfiles.Registry(("restart", ToolRisk.Write)));
                s.RemoveAll<IModelClient>();
                s.AddSingleton<IModelClient, Answer>();
                s.AddHttpClient(nameof(TeamsClient)).ConfigurePrimaryHttpMessageHandler(() => _ms);
                s.AddHttpClient(nameof(TeamsTokenValidator)).ConfigurePrimaryHttpMessageHandler(() => _ms);
            });
        });
        using var scope = _app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        ctx.UserProfiles.Add(new UserProfileRecord { UserId = "alice", Email = "alice@example.org", Roles = "operator", LastSeenAt = DateTimeOffset.UtcNow });
        ctx.UserProfiles.Add(new UserProfileRecord { UserId = "carol", Email = "carol@example.org", Roles = "admin", LastSeenAt = DateTimeOffset.UtcNow });
        ctx.SaveChanges();
    }

    private static string Token(string audience = AppId, string serviceUrl = ServiceUrl, SecurityKey? key = null, string issuer = "https://api.botframework.com",
        DateTime? expires = null) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer, Audience = audience, Expires = expires ?? DateTime.UtcNow.AddMinutes(5), NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Subject = new ClaimsIdentity([new Claim("serviceurl", serviceUrl)]),
            SigningCredentials = new SigningCredentials(key ?? Signing, SecurityAlgorithms.RsaSha256),
        });

    private static string Message(string id, string fromId, string text = "<at>Lots</at> are the containers healthy?", string serviceUrl = ServiceUrl) =>
        JsonSerializer.Serialize(new
        {
            type = "message", id, channelId = "msteams", serviceUrl, text, from = new { id = fromId, aadObjectId = "oid-" + fromId },
            conversation = new { id = "19:conv@thread.v2" }, recipient = new { id = "28:" + AppId },
        });

    private async Task<HttpResponseMessage> Post(string body, string? token)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/channels/teams/messages") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (token is not null) r.Headers.Authorization = new("Bearer", token);
        return await _app.CreateClient().SendAsync(r);
    }

    private async Task<List<RunRecord>> RunsAsync()
    {
        using var scope = _app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LotsDbContext>().Runs.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task Only_activities_signed_by_the_Bot_Framework_for_this_bot_and_this_service_url_are_accepted()
    {
        var body = Message("A0", "29:alice");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(body, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(body, Token(key: new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test-key" }))).StatusCode); // forged
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(body, Token(audience: "another-bot"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(body, Token(issuer: "https://evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(body, Token(expires: DateTime.UtcNow.AddMinutes(-10)))).StatusCode);
        // A valid token for one service URL cannot vouch for an activity that names another (where the bot token would go).
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(Message("A0", "29:alice", serviceUrl: "https://evil.example/"), Token())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(Message("A0", "29:alice", serviceUrl: "https://evil.example/"), Token(serviceUrl: "https://evil.example/"))).StatusCode);
        Assert.Empty(await RunsAsync());
        Assert.DoesNotContain(_ms.Calls, c => c.Url.Contains("evil.example"));
    }

    [Fact]
    public async Task A_message_becomes_a_run_as_the_mapped_user_once_and_the_answer_goes_back_to_the_conversation()
    {
        Assert.Equal(HttpStatusCode.OK, (await Post(Message("A1", "29:alice"), Token())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(Message("A1", "29:alice"), Token())).StatusCode); // redelivered
        var run = Assert.Single(await RunsAsync());
        Assert.Equal(("alice", "operator", "teams"), (run.UserId, run.Roles, run.Trigger));
        Assert.Equal("are the containers healthy?", run.Prompt);

        for (var i = 0; i < 100 && (await RunsAsync()).Single().Status != RunStatus.Completed; i++) await Task.Delay(50);
        using (var scope = _app.Services.CreateScope())
        {
            var reply = Assert.Single(scope.ServiceProvider.GetRequiredService<LotsDbContext>().Notifications.Where(n => n.Event == NotificationEvents.ChannelReply));
            Assert.Contains("\"kind\":\"teams\"", reply.PayloadJson);
            Assert.Contains("\"serviceUrl\":\"https://smba.test/emea/\"", reply.PayloadJson);
            Assert.Contains("Everything is healthy.", reply.PayloadJson);
        }

        // Delivery: a bot token from the token endpoint, then a reply to the question's activity in the same conversation.
        await _app.Services.GetRequiredService<TeamsClient>().ReplyAsync(ServiceUrl, "19:conv@thread.v2", "A1", "Everything is healthy.", null, default);
        Assert.Contains(_ms.Calls, c => c.Url.Contains("/oauth2/v2.0/token") && c.Body.Contains("client_credentials") && c.Body.Contains("api.botframework.com"));
        Assert.Contains(_ms.Calls, c => c.Method == "POST" && c.Url == "https://smba.test/emea/v3/conversations/19%3Aconv%40thread.v2/activities/A1" && c.Body.Contains("Everything is healthy."));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _app.Services.GetRequiredService<TeamsClient>().ReplyAsync("https://evil.example/", "c", null, "x", null, default));
    }

    [Fact]
    public async Task Someone_Lots_does_not_know_gets_an_explanation_and_no_run()
    {
        Assert.Equal(HttpStatusCode.OK, (await Post(Message("A2", "29:stranger"), Token())).StatusCode);
        Assert.Empty(await RunsAsync());
        Assert.Contains(_ms.Calls, c => c.Url.Contains("/activities/A2") && c.Body.Contains("tell who you are in Lots"));
    }

    [Fact]
    public async Task Card_buttons_decide_approvals_as_the_clicking_person_through_the_web_rules()
    {
        Guid approvalId;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
            var run = new RunRecord { Id = Guid.NewGuid(), Prompt = "restart web", Profile = TestProfiles.Name, UserId = "bob", Roles = "operator", CreatedAt = DateTimeOffset.UtcNow, Status = RunStatus.WaitingForApproval };
            db.Runs.Add(run);
            var approval = new ApprovalRecord
            {
                Id = Guid.NewGuid(), RunId = run.Id, ToolCallId = "c1", ToolName = "restart", ArgumentsJson = "{}", RequestedBy = "bob",
                RequestedAt = DateTimeOffset.UtcNow, Risk = "Write", RequiredApprovals = 1, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            };
            db.Approvals.Add(approval);
            await db.SaveChangesAsync();
            approvalId = approval.Id;
        }
        string Click(string from, string verb = "lots_approve") => JsonSerializer.Serialize(new
        {
            type = "invoke", name = "adaptiveCard/action", id = "I-" + from, channelId = "msteams", serviceUrl = ServiceUrl,
            from = new { id = from }, conversation = new { id = "19:conv@thread.v2" },
            value = new { action = new { type = "Action.Execute", verb, data = new { approvalId = approvalId.ToString() } } },
        });

        // alice is an operator: she may not approve writes, so the click only explains.
        var refused = await Post(Click("29:alice"), Token());
        Assert.Contains("Decide it in Lots instead", await refused.Content.ReadAsStringAsync());
        using (var scope = _app.Services.CreateScope())
            Assert.Equal(ApprovalStatus.Pending, (await scope.ServiceProvider.GetRequiredService<LotsDbContext>().Approvals.AsNoTracking().SingleAsync(a => a.Id == approvalId)).Status);

        // An unsigned click is refused outright.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(Click("29:carol"), null)).StatusCode);

        // carol is an admin (mapped by her user principal name): her click decides it, recorded as her.
        var approved = await Post(Click("29:carol"), Token());
        Assert.Contains("Approved by carol", await approved.Content.ReadAsStringAsync());
        using (var scope = _app.Services.CreateScope())
        {
            var a = await scope.ServiceProvider.GetRequiredService<LotsDbContext>().Approvals.AsNoTracking().SingleAsync(x => x.Id == approvalId);
            Assert.Equal((ApprovalStatus.Approved, "carol"), (a.Status, a.DecidedBy));
        }
    }

    [Fact]
    public void The_approval_card_has_explicit_buttons_and_a_link()
    {
        var json = JsonSerializer.Serialize(TeamsClient.ApprovalCard("Approval needed", "abc", "https://lots.test/#/runs/1"));
        Assert.Contains("\"verb\":\"lots_approve\"", json);
        Assert.Contains("\"verb\":\"lots_deny\"", json);
        Assert.Contains("Action.OpenUrl", json);
    }
}
