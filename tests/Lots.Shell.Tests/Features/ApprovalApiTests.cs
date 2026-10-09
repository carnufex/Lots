using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

public class ApprovalApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class WriteTool : IToolSource
    {
        public List<string> Called { get; } = [];

        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>(
                [new ToolDescriptor("restart", "restart a container", JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone())]);

        public Task<string> CallAsync(string name, string argumentsJson, CancellationToken ct)
        {
            Called.Add(name);
            return Task.FromResult("restarted");
        }
    }

    /// <summary>Calls the write tool first; after a tool result it gives the final answer.</summary>
    private sealed class RestartThenAnswer : IModelClient
    {
        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, CancellationToken ct)
        {
            var reply = m.Any(x => x.Role == "tool")
                ? new ChatMessage("assistant", "done: " + m.Last(x => x.Role == "tool").Content)
                : new ChatMessage("assistant", null, [new ToolCall("c1", "restart", "{}")]);
            return Task.FromResult(new ModelResponse(reply, "stop", new ModelUsage(1, 1), TimeSpan.FromMilliseconds(1)));
        }
    }

    private readonly WriteTool _tool = new();
    private readonly WebApplicationFactory<Program> _factory;

    public ApprovalApiTests(WebApplicationFactory<Program> factory)
    {
        var dbName = Guid.NewGuid().ToString();
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false",
                ["ConnectionStrings:Lots"] = "Host=none",
                ["Auth:Dev:AllowHeaders"] = "true",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(dbName));
                s.AddSingleton(TestProfiles.Registry(("restart", ToolRisk.Write)));
                s.RemoveAll<IModelClient>();
                s.AddSingleton<IModelClient, RestartThenAnswer>();
                s.RemoveAll<IToolSource>();
                s.AddSingleton<IToolSource>(_tool);
            });
        });
    }

    private sealed record Started(Guid Id);

    private sealed record Run(string Status, string? FinalAnswer);

    private sealed record AuditRow(string User, string Tool, string Decision, string? Approver, string? Result, string Profile, int ProfileVersion, Guid RunId);

    private sealed record Approval(Guid Id, Guid RunId, string Tool, string RequestedBy, string Status, string? DecidedBy);

    private static HttpRequestMessage As(HttpMethod method, string url, string user, string roles, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Dev-User", user);
        req.Headers.Add("X-Dev-Roles", roles);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private async Task<string> WaitForStatusAsync(HttpClient client, Guid id, params string[] statuses)
    {
        for (var i = 0; i < 100; i++)
        {
            var run = (await client.GetFromJsonAsync<Run>($"/runs/{id}"))!;
            if (statuses.Contains(run.Status)) return run.Status;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Run never reached {string.Join("/", statuses)}.");
    }

    [Fact]
    public async Task Write_call_waits_for_an_authorised_approver_then_completes()
    {
        var client = _factory.CreateClient();

        var start = await client.SendAsync(As(HttpMethod.Post, "/runs", "alice", "admin", new { prompt = "restart web" }));
        var id = (await start.Content.ReadFromJsonAsync<Started>())!.Id;
        Assert.Equal("WaitingForApproval", await WaitForStatusAsync(client, id, "WaitingForApproval", "Completed", "Failed"));
        Assert.Empty(_tool.Called);

        // A user without an approving role sees nothing and cannot decide.
        var asOperator = await (await client.SendAsync(As(HttpMethod.Get, "/approvals", "carol", "operator"))).Content.ReadFromJsonAsync<List<Approval>>();
        Assert.Empty(asOperator!);

        var asBob = await (await client.SendAsync(As(HttpMethod.Get, "/approvals", "bob", "admin"))).Content.ReadFromJsonAsync<List<Approval>>();
        var pending = Assert.Single(asBob!);
        Assert.Equal("restart", pending.Tool);
        Assert.Equal("alice", pending.RequestedBy);

        var forbidden = await client.SendAsync(As(HttpMethod.Post, $"/approvals/{pending.Id}/approve", "carol", "operator", new { }));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Empty(_tool.Called);

        var approved = await client.SendAsync(As(HttpMethod.Post, $"/approvals/{pending.Id}/approve", "bob", "admin", new { comment = "ok" }));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("Completed", await WaitForStatusAsync(client, id, "Completed", "Failed"));
        Assert.Equal(["restart"], _tool.Called);
        Assert.Equal("done: restarted", (await client.GetFromJsonAsync<Run>($"/runs/{id}"))!.FinalAnswer);

        // Audit: requested, granted by bob, executed with bob as approver; readable only by admins/auditors.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(As(HttpMethod.Get, "/audit", "carol", "operator"))).StatusCode);
        var audit = (await (await client.SendAsync(As(HttpMethod.Get, $"/audit?runId={id}", "dave", "auditor"))).Content.ReadFromJsonAsync<List<AuditRow>>())!;
        Assert.Equal(["Allowed", "ApprovalGranted", "ApprovalRequested"], audit.Select(a => a.Decision).Order());
        var executed = audit.Single(a => a.Decision == "Allowed");
        Assert.Equal(("alice", "restart", "bob", "ok"), (executed.User, executed.Tool, executed.Approver, executed.Result));
        Assert.Equal((TestProfiles.Name, 1), (executed.Profile, executed.ProfileVersion));
        Assert.Equal("bob", audit.Single(a => a.Decision == "ApprovalGranted").Approver);
        var byUser = (await (await client.SendAsync(As(HttpMethod.Get, "/audit?user=nobody", "dave", "auditor"))).Content.ReadFromJsonAsync<List<AuditRow>>())!;
        Assert.Empty(byUser);

        var again = await client.SendAsync(As(HttpMethod.Post, $"/approvals/{pending.Id}/deny", "bob", "admin", new { }));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(As(HttpMethod.Post, $"/approvals/{Guid.NewGuid()}/approve", "bob", "admin", new { }))).StatusCode);
    }

    [Fact]
    public async Task Denial_ends_the_run_without_running_the_tool()
    {
        var client = _factory.CreateClient();
        var start = await client.SendAsync(As(HttpMethod.Post, "/runs", "alice", "admin", new { prompt = "restart web" }));
        var id = (await start.Content.ReadFromJsonAsync<Started>())!.Id;
        await WaitForStatusAsync(client, id, "WaitingForApproval");
        var pending = (await (await client.SendAsync(As(HttpMethod.Get, "/approvals", "bob", "admin"))).Content.ReadFromJsonAsync<List<Approval>>())!.Single();

        var denied = await client.SendAsync(As(HttpMethod.Post, $"/approvals/{pending.Id}/deny", "bob", "admin", new { comment = "no" }));

        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
        Assert.Equal("Completed", await WaitForStatusAsync(client, id, "Completed", "Failed"));
        Assert.Empty(_tool.Called);
        Assert.Contains("denied by bob", (await client.GetFromJsonAsync<Run>($"/runs/{id}"))!.FinalAnswer);
    }
}
