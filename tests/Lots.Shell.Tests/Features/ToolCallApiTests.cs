using System.Net.Http.Json;
using Lots.Shell.Features.ToolCalls;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

public class ToolCallApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ToolCallApiTests(WebApplicationFactory<Program> factory)
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
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(dbName));
                s.AddSingleton(TestProfiles.Registry());
            });
        });
        Seed();
    }

    private void Seed()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var alice = Run("alice", t0);
        var bob = Run("bob", t0);
        db.Runs.AddRange(alice, bob);
        db.RunSteps.AddRange(
            Step(alice, 0, "list_containers", "c1", "[...]", 10, t0.AddSeconds(1)),
            Step(alice, 1, "list_containers", "c2", "Error: tool 'list_containers' failed: boom", 30, t0.AddSeconds(2)),
            Step(alice, 2, "restart", "c3", "Error: tool 'restart' is not available or not permitted.", 0, t0.AddSeconds(3)),
            // Written before audit rows carried the tool call id: matched by tool, arguments and time.
            Step(bob, 0, "get_container_logs", null, "log line", 50, t0.AddSeconds(4)));
        db.AuditLog.AddRange(
            Audit(alice, "list_containers", "c1", AuditDecision.Allowed, "ok", t0.AddSeconds(1)),
            Audit(alice, "list_containers", "c2", AuditDecision.Allowed, "error", t0.AddSeconds(2)),
            Audit(alice, "restart", "c3", AuditDecision.Denied, null, t0.AddSeconds(3)),
            Audit(bob, "get_container_logs", null, AuditDecision.Allowed, "ok", t0.AddSeconds(4)));
        db.SaveChanges();
    }

    private static RunRecord Run(string user, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(), Prompt = "p", Profile = TestProfiles.Name, UserId = user, Roles = "operator",
        Status = RunStatus.Completed, CreatedAt = at, UpdatedAt = at,
    };

    private static RunStepRecord Step(RunRecord run, int seq, string tool, string? callId, string result, long ms, DateTimeOffset at) => new()
    {
        RunId = run.Id, Seq = seq, Kind = StepKind.ToolCall, Name = tool, ToolCallId = callId, ArgumentsJson = "{}",
        Result = result, LatencyMs = ms, CreatedAt = at,
    };

    private static AuditRecord Audit(RunRecord run, string tool, string? callId, AuditDecision decision, string? result, DateTimeOffset at) => new()
    {
        Id = Guid.NewGuid(), At = at, UserId = run.UserId, Profile = run.Profile, RunId = run.Id, Tool = tool, ToolCallId = callId,
        ArgumentsJson = "{}", Decision = decision, Reason = "r", ResultStatus = result,
    };

    private async Task<ToolCallList> Get(string user, string roles, string query = "")
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/tool-calls" + query);
        req.Headers.Add("X-Dev-User", user);
        req.Headers.Add("X-Dev-Roles", roles);
        var res = await _factory.CreateClient().SendAsync(req);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<ToolCallList>())!;
    }

    [Fact]
    public async Task A_user_sees_only_calls_from_their_own_runs_even_when_asking_for_someone_else()
    {
        var mine = await Get("alice", "operator", "?user=bob");

        Assert.Equal(3, mine.Total);
        Assert.All(mine.Calls, c => Assert.Equal("alice", c.User));
    }

    [Fact]
    public async Task Auditors_see_everyone_and_each_call_carries_its_policy_decision()
    {
        var all = await Get("eve", "auditor");

        Assert.Equal(4, all.Total);
        Assert.Equal(["ok", "error", "denied", "ok"], all.Calls.OrderBy(c => c.At).Select(c => c.Status));
        Assert.Equal("Denied", all.Calls.Single(c => c.Tool == "restart").Decision);
        Assert.Equal("Allowed", all.Calls.Single(c => c.Tool == "get_container_logs").Decision); // legacy row without call id
    }

    [Fact]
    public async Task Filters_and_per_tool_statistics()
    {
        var denied = await Get("eve", "admin", "?status=denied");
        Assert.Equal("restart", Assert.Single(denied.Calls).Tool);

        var list = await Get("eve", "admin", "?tool=list_containers");
        var stats = Assert.Single(list.Tools);
        Assert.Equal(2, stats.Calls);
        Assert.Equal(1, stats.Errors);
        Assert.Equal(0.5, stats.ErrorRate);
        Assert.Equal(10, stats.P50Ms);
        Assert.Equal(30, stats.P95Ms);

        var bobs = await Get("eve", "admin", "?user=bob");
        Assert.Equal("bob", Assert.Single(bobs.Calls).User);
    }
}
