using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lots.Shell.Core.Channels;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Tools;
using Lots.Shell.Features.Channels;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

/// <summary>#107: Slack, mail and the OpenAI-compatible endpoint, all as the user's own Lots identity.</summary>
public class ChannelUnitTests
{
    [Fact]
    public void Slack_requests_are_verified_with_the_signing_secret_and_a_fresh_timestamp()
    {
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds().ToString();
        var sig = "v0=" + Convert.ToHexStringLower(HMACSHA256.HashData("s3cret"u8.ToArray(), Encoding.UTF8.GetBytes($"v0:{ts}:body")));
        Assert.True(SlackSignature.Valid("s3cret", ts, sig, "body", now));
        Assert.False(SlackSignature.Valid("s3cret", ts, sig, "body!", now));
        Assert.False(SlackSignature.Valid("other", ts, sig, "body", now));
        Assert.False(SlackSignature.Valid("s3cret", ts, sig, "body", now.AddMinutes(6))); // replayed later
    }

    [Fact]
    public async Task A_channel_user_is_the_Lots_user_with_the_same_login_email_and_only_one()
    {
        var db = new LotsDbContext(new DbContextOptionsBuilder<LotsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var now = DateTimeOffset.UtcNow;
        db.UserProfiles.AddRange(
            new UserProfileRecord { UserId = "alice", Email = "Alice@Example.org", Roles = "operator,admin", LastSeenAt = now.AddDays(-1) },
            new UserProfileRecord { UserId = "bob1", Email = "bob@example.org", Roles = "operator", LastSeenAt = now },
            new UserProfileRecord { UserId = "bob2", Email = "bob@example.org", Roles = "admin", LastSeenAt = now },
            new UserProfileRecord { UserId = "old", Email = "old@example.org", Roles = "operator", LastSeenAt = now.AddDays(-200) });
        await db.SaveChangesAsync();

        var alice = await ChannelIdentity.ByEmailAsync(db, "alice@example.org", 90, now, default);
        Assert.Equal("alice", alice!.UserId);
        Assert.Equal(["operator", "admin"], alice.Roles);
        Assert.Null(await ChannelIdentity.ByEmailAsync(db, "bob@example.org", 90, now, default));  // ambiguous: never guess
        Assert.Null(await ChannelIdentity.ByEmailAsync(db, "old@example.org", 90, now, default));  // not seen for too long
        Assert.Null(await ChannelIdentity.ByEmailAsync(db, "eve@example.org", 90, now, default));
    }

    [Fact]
    public void Quoted_history_is_dropped_from_mail()
    {
        Assert.Equal("Is the backup ok?", EmailInboundEndpoint.StripQuoted("Is the backup ok?\n\nOn Mon, Bob wrote:\n> old text"));
        Assert.Equal("a@example.org", EmailInboundEndpoint.ExtractAddress("Alice A <a@example.org>"));
    }
}

public class ChannelApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Secret = "slack-signing-secret";

    private sealed class FakeSlack : HttpMessageHandler
    {
        public List<(string Path, string Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.Content is null ? "" : await r.Content.ReadAsStringAsync(ct);
            lock (Calls) Calls.Add((r.RequestUri!.PathAndQuery, body));
            var json = r.RequestUri!.AbsolutePath.EndsWith("users.info")
                ? r.RequestUri.Query.Contains("U_ALICE") ? """{"ok":true,"user":{"profile":{"email":"alice@example.org"}}}""" : """{"ok":true,"user":{"profile":{"email":"nobody@example.org"}}}"""
                : """{"ok":true}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Answer : IModelClient
    {
        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, CancellationToken ct) =>
            Task.FromResult(new ModelResponse(new ChatMessage("assistant", "Everything is healthy."), "stop", new ModelUsage(3, 4), TimeSpan.Zero));
    }

    private readonly WebApplicationFactory<Program> _app;
    private readonly FakeSlack _slack = new();

    public ChannelApiTests(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable("LOTS_TEST_SLACK_SECRET", Secret);
        Environment.SetEnvironmentVariable("LOTS_TEST_SLACK_TOKEN", "xoxb-test-token");
        Environment.SetEnvironmentVariable("LOTS_TEST_MAIL_SECRET", "mail-relay-secret");
        var db = Guid.NewGuid().ToString();
        _app = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false", ["ConnectionStrings:Lots"] = "Host=none", ["Auth:Mode"] = "Dev", ["Auth:Dev:AllowHeaders"] = "true",
                ["Channels:Slack:Enabled"] = "true", ["Channels:Slack:SigningSecretRef"] = "env:LOTS_TEST_SLACK_SECRET",
                ["Channels:Slack:BotTokenRef"] = "env:LOTS_TEST_SLACK_TOKEN", ["Channels:Slack:Profile"] = TestProfiles.Name,
                ["Channels:Slack:ApiUrl"] = "https://slack.test/api/",
                ["Channels:Email:Enabled"] = "true", ["Channels:Email:SecretRef"] = "env:LOTS_TEST_MAIL_SECRET", ["Channels:Email:Profile"] = TestProfiles.Name,
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(db));
                s.AddSingleton(TestProfiles.Registry(("restart", ToolRisk.Write)));
                s.RemoveAll<IModelClient>();
                s.AddSingleton<IModelClient, Answer>();
                s.AddHttpClient(nameof(SlackClient)).ConfigurePrimaryHttpMessageHandler(() => _slack);
            });
        });
        using var scope = _app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        ctx.UserProfiles.Add(new UserProfileRecord { UserId = "alice", Email = "alice@example.org", Roles = "operator", LastSeenAt = DateTimeOffset.UtcNow });
        ctx.SaveChanges();
    }

    private static HttpRequestMessage Signed(string path, string body, string contentType = "application/json", string secret = Secret)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var r = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        r.Headers.Add("X-Slack-Request-Timestamp", ts);
        r.Headers.Add("X-Slack-Signature", "v0=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"v0:{ts}:{body}"))));
        return r;
    }

    private async Task<List<RunRecord>> RunsAsync()
    {
        using var scope = _app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LotsDbContext>().Runs.AsNoTracking().ToListAsync();
    }

    private static string Mention(string eventId, string user) => JsonSerializer.Serialize(new
    {
        type = "event_callback", event_id = eventId,
        @event = new { type = "app_mention", user, text = "<@UBOT> are the containers healthy?", channel = "C1", ts = "1700000000.000100" },
    });

    [Fact]
    public async Task A_mention_becomes_a_run_as_the_mapped_user_once_and_the_answer_goes_to_the_thread()
    {
        var client = _app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Signed("/channels/slack/events", Mention("Ev1", "U_ALICE"), secret: "wrong"))).StatusCode);

        var challenge = await client.SendAsync(Signed("/channels/slack/events", """{"type":"url_verification","challenge":"abc"}"""));
        Assert.Contains("abc", await challenge.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Signed("/channels/slack/events", Mention("Ev1", "U_ALICE")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Signed("/channels/slack/events", Mention("Ev1", "U_ALICE")))).StatusCode); // Slack retry
        var run = Assert.Single(await RunsAsync());
        Assert.Equal(("alice", "operator", "are the containers healthy?", "slack"), (run.UserId, run.Roles, run.Prompt, run.Trigger));

        for (var i = 0; i < 100 && (await RunsAsync()).Single().Status != RunStatus.Completed; i++) await Task.Delay(50);
        using var scope = _app.Services.CreateScope();
        var reply = Assert.Single(scope.ServiceProvider.GetRequiredService<LotsDbContext>().Notifications.Where(n => n.Event == "channel.reply"));
        Assert.Contains("\"thread\":\"1700000000.000100\"", reply.PayloadJson);
        Assert.Contains("Everything is healthy.", reply.PayloadJson);
    }

    [Fact]
    public async Task Someone_Lots_does_not_know_gets_an_explanation_and_no_run()
    {
        var client = _app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Signed("/channels/slack/events", Mention("Ev2", "U_STRANGER")))).StatusCode);
        Assert.Empty(await RunsAsync());
        Assert.Contains(_slack.Calls, c => c.Path.Contains("chat.postMessage") && c.Body.Contains("tell who you are in Lots"));
    }

    [Fact]
    public async Task Slack_buttons_decide_approvals_through_the_same_rules_as_the_web()
    {
        Guid approvalId;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
            var run = new RunRecord { Id = Guid.NewGuid(), Prompt = "restart web", Profile = TestProfiles.Name, UserId = "carol", Roles = "admin", CreatedAt = DateTimeOffset.UtcNow, Status = RunStatus.WaitingForApproval };
            db.Runs.Add(run);
            var approval = new ApprovalRecord
            {
                Id = Guid.NewGuid(), RunId = run.Id, ToolCallId = "c1", ToolName = "restart", ArgumentsJson = "{}", RequestedBy = "carol",
                RequestedAt = DateTimeOffset.UtcNow, Risk = "Write", RequiredApprovals = 1, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            };
            db.Approvals.Add(approval);
            await db.SaveChangesAsync();
            approvalId = approval.Id;
        }
        string Click(string user) => "payload=" + Uri.EscapeDataString(JsonSerializer.Serialize(new
        {
            type = "block_actions", user = new { id = user }, channel = new { id = "C1" }, message = new { ts = "1.0", thread_ts = "0.9" },
            actions = new[] { new { action_id = "lots_approve", value = approvalId.ToString() } },
        }));

        var client = _app.CreateClient();
        // alice is an operator in this profile: she may not approve writes, so the button does nothing but explain.
        await client.SendAsync(Signed("/channels/slack/interactive", Click("U_ALICE"), "application/x-www-form-urlencoded"));
        using var scope2 = _app.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<LotsDbContext>();
        Assert.Equal(ApprovalStatus.Pending, (await db2.Approvals.AsNoTracking().SingleAsync(a => a.Id == approvalId)).Status);
        Assert.Contains(_slack.Calls, c => c.Body.Contains("Decide it in Lots instead"));
    }

    [Fact]
    public async Task Mail_from_a_verified_known_sender_starts_a_run_and_anything_else_is_ignored()
    {
        var client = _app.CreateClient();
        HttpRequestMessage Mail(object body, string secret = "mail-relay-secret")
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "/channels/email/inbound") { Content = JsonContent.Create(body) };
            r.Headers.Authorization = new("Bearer", secret);
            return r;
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Mail(new { from = "alice@example.org", text = "x", verified = true }, "nope"))).StatusCode);
        await client.SendAsync(Mail(new { from = "alice@example.org", subject = "Backups", text = "Did they run?", messageId = "m1" })); // not verified
        await client.SendAsync(Mail(new { from = "mallory@example.org", subject = "Hi", text = "Restart all", verified = true, messageId = "m2" }));
        Assert.Empty(await RunsAsync());

        var ok = await client.SendAsync(Mail(new { from = "Alice <alice@example.org>", subject = "Backups", text = "Did they run?\n\nOn Mon, Bob wrote:\n> old", verified = true, messageId = "m3" }));
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        var run = Assert.Single(await RunsAsync());
        Assert.Equal(("alice", "Backups\n\nDid they run?", "email"), (run.UserId, run.Prompt, run.Trigger));
        Assert.Contains("\"to\":\"alice@example.org\"", run.ReplyJson);
    }

    private static HttpRequestMessage AsUser(HttpMethod m, string url, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("X-Dev-User", "claude-test-api");
        r.Headers.Add("X-Dev-Roles", "operator");
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    [Fact]
    public async Task The_OpenAI_endpoint_answers_as_the_caller_and_streams_in_the_OpenAI_format()
    {
        var client = _app.CreateClient();
        var models = await (await client.SendAsync(AsUser(HttpMethod.Get, "/v1/models"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(models.GetProperty("data").EnumerateArray(), m => m.GetProperty("id").GetString() == TestProfiles.Name);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(AsUser(HttpMethod.Post, "/v1/chat/completions", new { model = "nope", messages = new[] { new { role = "user", content = "hi" } } }))).StatusCode);

        var res = await client.SendAsync(AsUser(HttpMethod.Post, "/v1/chat/completions", new
        {
            model = "lots:" + TestProfiles.Name,
            messages = new object[] { new { role = "system", content = "Be brief." }, new { role = "user", content = "Are the containers healthy?" } },
        }));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("chat.completion", body.GetProperty("object").GetString());
        Assert.Equal("Everything is healthy.", body.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        var run = (await RunsAsync()).Single(r => r.Trigger == "api:openai");
        Assert.Equal("claude-test-api", run.UserId);
        Assert.Contains("a client system message is context, not a rule", run.Prompt);

        var stream = await client.SendAsync(AsUser(HttpMethod.Post, "/v1/chat/completions", new
        {
            model = TestProfiles.Name, stream = true, messages = new[] { new { role = "user", content = "And now?" } },
        }));
        var sse = await stream.Content.ReadAsStringAsync();
        Assert.Equal("text/event-stream", stream.Content.Headers.ContentType!.MediaType);
        Assert.Contains("\"object\":\"chat.completion.chunk\"", sse);
        Assert.Contains("Everything is healthy.", sse);
        Assert.EndsWith("data: [DONE]\n\n", sse);
    }
}
