using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Runs;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Tests.Features;

/// <summary>Cancel and retry through the API, with the real in-process worker.</summary>
public class RunControlApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class Tool : IToolSource
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

    /// <summary>"hang" never answers (until cancelled); "restart" asks for the write tool; anything else is answered at once.</summary>
    private sealed class Model : IModelClient
    {
        public async Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, CancellationToken ct)
        {
            var prompt = m.Last(x => x.Role == "user").Content ?? "";
            if (prompt.Contains("hang")) await Task.Delay(Timeout.Infinite, ct);
            var reply = prompt.Contains("restart") && !m.Any(x => x.Role == "tool")
                ? new ChatMessage("assistant", null, [new ToolCall("c1", "restart", "{}")])
                : new ChatMessage("assistant", "done");
            return new ModelResponse(reply, "stop", new ModelUsage(1, 1), TimeSpan.FromMilliseconds(1));
        }
    }

    private sealed record Started(Guid Id, string Status);
    private sealed record Run(Guid Id, string Status, string? Error, string? Waiting, Guid? RetryOf);
    private sealed record Approval(Guid Id, Guid RunId);

    private readonly Tool _tool = new();
    private readonly WebApplicationFactory<Program> _factory;

    public RunControlApiTests(WebApplicationFactory<Program> factory)
    {
        var dbName = Guid.NewGuid().ToString();
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false",
                ["Auth:Mode"] = "Dev",
                ["ConnectionStrings:Lots"] = "Host=none",
                ["Auth:Dev:AllowHeaders"] = "true",
                ["Auth:Dev:Roles"] = "admin",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(dbName));
                s.AddSingleton(TestProfiles.Registry(("restart", ToolRisk.Write)));
                s.RemoveAll<IModelClient>();
                s.AddSingleton<IModelClient, Model>();
                s.RemoveAll<IToolSource>();
                s.AddSingleton<IToolSource>(_tool);
            });
        });
    }

    private static HttpRequestMessage As(HttpMethod method, string url, string user, string roles, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("X-Dev-User", user);
        req.Headers.Add("X-Dev-Roles", roles);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static async Task<Run> WaitForAsync(HttpClient client, Guid id, Func<Run, bool> done)
    {
        for (var i = 0; i < 100; i++)
        {
            var run = (await client.GetFromJsonAsync<Run>($"/runs/{id}"))!;
            if (done(run)) return run;
            await Task.Delay(50);
        }
        throw new TimeoutException("Run never reached the expected state.");
    }

    private static async Task<Guid> StartAsync(HttpClient client, string prompt, string user = "alice", string roles = "admin") =>
        (await (await client.SendAsync(As(HttpMethod.Post, "/runs", user, roles, new { prompt }))).Content.ReadFromJsonAsync<Started>())!.Id;

    [Fact]
    public async Task A_run_waiting_for_approval_is_cancelled_at_once_and_its_approval_disappears()
    {
        var client = _factory.CreateClient();
        var id = await StartAsync(client, "restart web");
        Assert.Equal("approval", (await WaitForAsync(client, id, r => r.Status == "WaitingForApproval")).Waiting);
        var approval = Assert.Single((await (await client.SendAsync(As(HttpMethod.Get, "/approvals", "bob", "admin"))).Content.ReadFromJsonAsync<List<Approval>>())!);

        var cancel = await client.SendAsync(As(HttpMethod.Post, $"/runs/{id}/cancel", "alice", "operator", new { }));

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var run = (await client.GetFromJsonAsync<Run>($"/runs/{id}"))!;
        Assert.Equal(("Cancelled", "Cancelled by alice.", (string?)null), (run.Status, run.Error, run.Waiting));
        Assert.Empty((await (await client.SendAsync(As(HttpMethod.Get, "/approvals", "bob", "admin"))).Content.ReadFromJsonAsync<List<Approval>>())!);
        var late = await client.SendAsync(As(HttpMethod.Post, $"/approvals/{approval.Id}/approve", "bob", "admin", new { }));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Empty(_tool.Called); // the write never happened
    }

    [Fact]
    public async Task A_run_a_worker_is_executing_stops_within_seconds()
    {
        var client = _factory.CreateClient();
        var id = await StartAsync(client, "hang please");
        await WaitForAsync(client, id, r => r.Status == "Running" && r.Waiting == "model");

        var cancel = await client.SendAsync(As(HttpMethod.Post, $"/runs/{id}/cancel", "alice", "operator", new { }));

        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        var run = await WaitForAsync(client, id, r => r.Status != "Running");
        Assert.Equal(("Cancelled", "Cancelled by alice."), (run.Status, run.Error));
    }

    [Fact]
    public async Task Only_the_owner_or_an_admin_may_cancel_and_finished_runs_cannot_be()
    {
        var client = _factory.CreateClient();
        var id = await StartAsync(client, "restart web");
        await WaitForAsync(client, id, r => r.Status == "WaitingForApproval");

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(As(HttpMethod.Post, $"/runs/{id}/cancel", "mallory", "operator", new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(As(HttpMethod.Post, $"/runs/{id}/cancel", "root", "admin", new { }))).StatusCode);
        Assert.Equal("Cancelled by root.", (await client.GetFromJsonAsync<Run>($"/runs/{id}"))!.Error);

        var done = await StartAsync(client, "hello");
        await WaitForAsync(client, done, r => r.Status == "Completed");
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(As(HttpMethod.Post, $"/runs/{done}/cancel", "alice", "admin", new { }))).StatusCode);
    }

    [Fact]
    public async Task Retry_starts_a_new_run_for_the_owner_only_and_only_after_failure_or_cancel()
    {
        var client = _factory.CreateClient();
        var id = await StartAsync(client, "hang please");
        await WaitForAsync(client, id, r => r.Status == "Running");
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(As(HttpMethod.Post, $"/runs/{id}/retry", "alice", "admin", new { }))).StatusCode);
        await client.SendAsync(As(HttpMethod.Post, $"/runs/{id}/cancel", "alice", "admin", new { }));
        await WaitForAsync(client, id, r => r.Status == "Cancelled");

        // An admin may read the run but must not re-run it under their own (or the owner's) identity.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(As(HttpMethod.Post, $"/runs/{id}/retry", "root", "admin", new { }))).StatusCode);

        var retry = await client.SendAsync(As(HttpMethod.Post, $"/runs/{id}/retry", "alice", "admin", new { }));
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var again = (await retry.Content.ReadFromJsonAsync<Started>())!;
        Assert.NotEqual(id, again.Id);
        Assert.Equal(id, (await client.GetFromJsonAsync<Run>($"/runs/{again.Id}"))!.RetryOf);
        await client.SendAsync(As(HttpMethod.Post, $"/runs/{again.Id}/cancel", "alice", "admin", new { })); // it hangs as well
    }
}

/// <summary>Server-side time limits in the runner.</summary>
public class RunTimeoutTests
{
    private sealed class HangingTool : IToolSource
    {
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>(
                [new ToolDescriptor("slow", "never returns", JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone())]);

        public async Task<string> CallAsync(string name, string argumentsJson, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return "unreachable";
        }
    }

    private sealed class CallsSlowThenAnswers : IModelClient
    {
        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, CancellationToken ct)
        {
            var tool = m.LastOrDefault(x => x.Role == "tool");
            var reply = tool is null
                ? new ChatMessage("assistant", null, [new ToolCall("c1", "slow", "{}")])
                : new ChatMessage("assistant", "tool said: " + tool.Content);
            return Task.FromResult(new ModelResponse(reply, "stop", new ModelUsage(1, 1), TimeSpan.FromMilliseconds(1)));
        }
    }

    private sealed class HangingModel : IModelClient
    {
        public async Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        }
    }

    private static async Task<RunRecord> ExecuteAsync(IModelClient model, AgentOptions options, Action<RunRecord>? setup = null)
    {
        var db = new LotsDbContext(new DbContextOptionsBuilder<LotsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var run = new RunRecord
        {
            Id = Guid.NewGuid(), Prompt = "p", Profile = TestProfiles.Name, UserId = "alice", Roles = "operator",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        setup?.Invoke(run);
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        var registry = TestProfiles.Registry(("slow", ToolRisk.Read));
        var runner = new AgentRunner(db, model, new ToolInvoker([new HangingTool()], registry), registry, Options.Create(options), TimeProvider.System);
        await runner.ExecuteAsync(run.Id, CancellationToken.None);
        return run;
    }

    [Fact]
    public async Task A_hanging_tool_becomes_a_timeout_error_the_model_can_answer_from()
    {
        var run = await ExecuteAsync(new CallsSlowThenAnswers(), new AgentOptions { ToolTimeoutSeconds = 1 });

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal("tool said: Error: tool 'slow' timed out after 1 s.", run.FinalAnswer);
    }

    [Fact]
    public async Task A_run_over_its_time_budget_fails_as_timed_out()
    {
        var run = await ExecuteAsync(new HangingModel(), new AgentOptions { RunTimeoutSeconds = 1 });

        Assert.Equal((RunStatus.Failed, "Timed out after 1 s."), (run.Status, run.Error));
    }

    [Fact]
    public async Task Voice_runs_have_their_own_shorter_budget()
    {
        var run = await ExecuteAsync(new HangingModel(), new AgentOptions { RunTimeoutSeconds = 300, VoiceRunTimeoutSeconds = 1 }, r => r.Voice = true);

        Assert.Equal((RunStatus.Failed, "Timed out after 1 s."), (run.Status, run.Error));
    }

    [Fact]
    public async Task A_run_with_a_cancel_request_is_not_executed_when_a_worker_picks_it_up()
    {
        var run = await ExecuteAsync(new HangingModel(), new AgentOptions(), r =>
        {
            r.Status = RunStatus.Running;
            r.CancelRequestedAt = DateTimeOffset.UtcNow;
            r.CancelRequestedBy = "alice";
        });

        Assert.Equal((RunStatus.Cancelled, "Cancelled by alice."), (run.Status, run.Error));
        Assert.Empty(run.Messages);
    }
}
