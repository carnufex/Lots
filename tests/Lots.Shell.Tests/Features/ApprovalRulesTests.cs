using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Notifications;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Lots.Shell.Tests.Features;

public class ApprovalRulesTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class Tools : IToolSource
    {
        public List<string> Called { get; } = [];
        private static readonly JsonElement Schema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>([new ToolDescriptor("drop", "drop it", Schema), new ToolDescriptor("restart", "restart", Schema)]);
        public Task<string> CallAsync(string name, string a, CancellationToken ct)
        {
            Called.Add(name);
            return Task.FromResult("done");
        }
    }

    /// <summary>Calls the tool named in the prompt once, then answers.</summary>
    private sealed class Model : IModelClient
    {
        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, CancellationToken ct)
        {
            var tool = m.First(x => x.Role == "user").Content!;
            var reply = m.Any(x => x.Role == "tool")
                ? new ChatMessage("assistant", "result: " + m.Last(x => x.Role == "tool").Content)
                : new ChatMessage("assistant", null, [new ToolCall("c1", tool, "{}")]);
            return Task.FromResult(new ModelResponse(reply, "stop", new ModelUsage(1, 1), TimeSpan.Zero));
        }
    }

    private sealed class Hook : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private readonly Tools _tools = new();
    private readonly Hook _hook = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly WebApplicationFactory<Program> _factory;

    public ApprovalRulesTests(WebApplicationFactory<Program> factory)
    {
        var dbName = Guid.NewGuid().ToString();
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false", ["Auth:Mode"] = "Dev", ["ConnectionStrings:Lots"] = "Host=none", ["Auth:Dev:AllowHeaders"] = "true",
                ["Auth:Dev:Roles"] = "admin",
                ["Notifications:PublicUrl"] = "https://lots.example",
                ["Notifications:Webhooks:0:Url"] = "https://hooks.example/approvals",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(dbName));
                s.AddSingleton(new ProfileRegistry([ProfileParser.Parse("""
                    name: ops
                    version: 1
                    tools:
                      - { name: drop, risk: destructive }
                      - { name: restart, risk: write }
                    approvals: { expireAfterHours: 2, twoPerson: [destructive], requireComment: [destructive] }
                    roles:
                      - { name: admin, allow: [read, write, destructive], requireApproval: [write, destructive], approve: [write, destructive] }
                    """)]));
                s.RemoveAll<IModelClient>();
                s.AddSingleton<IModelClient, Model>();
                s.RemoveAll<IToolSource>();
                s.AddSingleton<IToolSource>(_tools);
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(_clock);
                s.AddHttpClient(nameof(NotificationWorker)).ConfigurePrimaryHttpMessageHandler(() => _hook);
            });
        });
    }

    private sealed record Approval(Guid Id, Guid RunId, string Tool, string Status, int RequiredApprovals, List<string> ApprovedBy, bool CommentRequired, DateTimeOffset? ExpiresAt);
    private sealed record Run(Guid Id, string Status, string? FinalAnswer);

    private HttpRequestMessage As(HttpMethod m, string url, string user, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("X-Dev-User", user);
        r.Headers.Add("X-Dev-Roles", "admin");
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    private async Task<Approval> PendingAsync(HttpClient client, string asUser)
    {
        for (var i = 0; i < 100; i++)
        {
            var list = await (await client.SendAsync(As(HttpMethod.Get, "/approvals", asUser))).Content.ReadFromJsonAsync<List<Approval>>();
            if (list is { Count: > 0 }) return list[0];
            await Task.Delay(50);
        }
        throw new TimeoutException();
    }

    private async Task<Run> WaitAsync(HttpClient client, Guid id)
    {
        for (var i = 0; i < 100; i++)
        {
            var run = (await client.GetFromJsonAsync<Run>($"/runs/{id}"))!;
            if (run.Status is "Completed" or "Failed") return run;
            await Task.Delay(50);
        }
        throw new TimeoutException();
    }

    private async Task<Guid> StartAsync(HttpClient client, string tool) =>
        (await (await client.SendAsync(As(HttpMethod.Post, "/runs", "alice", new { prompt = tool, profile = "ops" }))).Content.ReadFromJsonAsync<Run>())!.Id;

    [Fact]
    public async Task Destructive_calls_need_two_other_approvers_and_a_reason()
    {
        var client = _factory.CreateClient();
        var id = await StartAsync(client, "drop");
        var a = await PendingAsync(client, "bob");
        Assert.Equal((2, true), (a.RequiredApprovals, a.CommentRequired));
        Assert.Empty(await (await client.SendAsync(As(HttpMethod.Get, "/approvals", "alice"))).Content.ReadFromJsonAsync<List<Approval>>() ?? []);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(As(HttpMethod.Post, $"/approvals/{a.Id}/approve", "alice", new { comment = "mine" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(As(HttpMethod.Post, $"/approvals/{a.Id}/approve", "bob", new { }))).StatusCode);

        var first = (await (await client.SendAsync(As(HttpMethod.Post, $"/approvals/{a.Id}/approve", "bob", new { comment = "planned change" }))).Content.ReadFromJsonAsync<Approval>())!;
        Assert.Equal("Pending", first.Status);
        Assert.Equal(["bob"], first.ApprovedBy);
        Assert.Empty(_tools.Called);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(As(HttpMethod.Post, $"/approvals/{a.Id}/approve", "bob", new { comment = "again" }))).StatusCode);

        var second = (await (await client.SendAsync(As(HttpMethod.Post, $"/approvals/{a.Id}/approve", "carol", new { comment = "agreed" }))).Content.ReadFromJsonAsync<Approval>())!;
        Assert.Equal("Approved", second.Status);
        Assert.Equal("result: done", (await WaitAsync(client, id)).FinalAnswer);
        Assert.Equal(["drop"], _tools.Called);
    }

    [Fact]
    public async Task Undecided_requests_expire_and_the_run_reports_it_and_approvers_are_notified()
    {
        var client = _factory.CreateClient();
        var id = await StartAsync(client, "restart");
        var a = await PendingAsync(client, "bob");
        Assert.Equal(1, a.RequiredApprovals);

        var expiry = _factory.Services.GetRequiredService<ApprovalExpiryWorker>();
        Assert.Equal(0, await expiry.ExpireDueAsync(default));
        _clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(1, await expiry.ExpireDueAsync(default));

        Assert.Contains("expired without a decision", (await WaitAsync(client, id)).FinalAnswer);
        Assert.Empty(_tools.Called);

        await _factory.Services.GetRequiredService<NotificationWorker>().DeliverDueAsync(default);
        Assert.Contains(_hook.Bodies, b => b.Contains("Approval needed: restart (Write) in ops, requested by alice") && b.Contains("https://lots.example/#/approvals"));
        Assert.Contains(_hook.Bodies, b => b.Contains("Approval expired without a decision: restart"));
        Assert.DoesNotContain(_hook.Bodies, b => b.Contains("{}")); // tool arguments never leave the shell in a notification
    }
}

public class ApproverRoutingTests
{
    private static readonly Profile Ops = ProfileParser.Parse("""
        name: ops
        version: 1
        tools:
          - { name: restart, risk: write }
        roles:
          - { name: operator, allow: [read] }
          - { name: lead, allow: [read, write], requireApproval: [write], approve: [write] }
        """);

    private static UserProfileRecord P(string user, string roles, string? email = "x") => new() { UserId = user, Roles = roles, Email = email == "x" ? user + "@example.com" : email };

    [Fact]
    public void Approvers_are_notified_and_an_away_approver_is_covered_only_by_a_delegate_who_may_approve()
    {
        var now = DateTimeOffset.UtcNow;
        var people = new[] { P("alice", "operator"), P("bob", "lead"), P("carol", "lead"), P("dave", "operator"), P("erin", "lead"), P("frank", "lead", email: null) };
        var settings = new Dictionary<string, UserSettingsRecord>
        {
            ["bob"] = new() { UserId = "bob", AwayUntil = now.AddDays(1), DelegateTo = "dave" },   // dave cannot approve: nobody covers
            ["carol"] = new() { UserId = "carol", AwayUntil = now.AddDays(1), DelegateTo = "erin" },
            ["erin"] = new() { UserId = "erin", AwayUntil = now.AddDays(-1), DelegateTo = "alice" }, // back already
        };

        var recipients = ApproverRouting.Recipients(Ops, "restart", "alice", people, settings, now);

        Assert.Equal([("erin", "carol")], recipients.Where(r => r.OnBehalfOf is not null).Select(r => (r.UserId, r.OnBehalfOf!)));
        Assert.Equal(["erin"], recipients.Select(r => r.UserId)); // bob away without a valid delegate, frank has no address, alice/dave cannot approve
        Assert.DoesNotContain(ApproverRouting.Recipients(Ops, "restart", "erin", people, settings, now), r => r.UserId == "erin"); // never the requester
    }
}
