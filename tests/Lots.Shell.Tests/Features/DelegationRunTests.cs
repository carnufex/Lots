using System.Net.Http.Json;
using System.Text.Json;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

/// <summary>#103: a run hands a task to another profile as the same user, with a narrower tool set, in the same trace tree.</summary>
public class DelegationRunTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static ProfileRegistry Profiles() => new([
        ProfileParser.Parse("""
            name: homelab
            version: 1
            instructions: PROFILE-HOMELAB
            delegates: [cmdb]
            tools:
              - { name: delegate, risk: read }
            roles:
              - { name: operator, allow: [read] }
            """),
        ProfileParser.Parse("""
            name: cmdb
            version: 1
            instructions: PROFILE-CMDB
            delegates: [homelab]
            tools:
              - { name: find_host, risk: read }
              - { name: change_host, risk: write }
              - { name: delegate, risk: read }
            roles:
              - { name: operator, allow: [read, write], requireApproval: [write] }
            """),
    ]);

    /// <summary>Plays both assistants: the homelab one delegates, the CMDB one looks the host up (and tries to go further).</summary>
    private sealed class TwoAssistants : IModelClient
    {
        public List<(string Profile, List<string> Tools)> Calls { get; } = [];

        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
        {
            var profile = m[0].Content!.Contains("PROFILE-CMDB") ? "cmdb" : "homelab";
            lock (Calls) Calls.Add((profile, tools.Select(t => t.Name).ToList()));
            var toolResults = m.Where(x => x.Role == "tool").ToList();
            ChatMessage reply = (profile, toolResults.Count) switch
            {
                ("homelab", 0) => new("assistant", null, [new ToolCall("h1", "delegate", "{\"profile\":\"cmdb\",\"task\":\"Where is host web01?\"}")]),
                ("homelab", _) => new("assistant", "Host web01 is in rack 3. " + toolResults[^1].Content),
                ("cmdb", 0) => new("assistant", null, [new ToolCall("c1", "find_host", "{\"name\":\"web01\"}"), new ToolCall("c2", "delegate", "{\"profile\":\"homelab\",\"task\":\"loop\"}")]),
                _ => new("assistant", "web01: rack 3"),
            };
            return Task.FromResult(new ModelResponse(reply, "stop", new ModelUsage(1, 1), TimeSpan.Zero));
        }
    }

    private sealed class HostTools : IToolSource
    {
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ToolDescriptor>>(
            new[] { "find_host", "change_host" }.Select(n => new ToolDescriptor(n, n, JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone())).ToList());
        public Task<string> CallAsync(string name, string argumentsJson, CancellationToken ct) => Task.FromResult("web01 rack=3");
    }

    private readonly WebApplicationFactory<Program> _app;
    private readonly TwoAssistants _model = new();

    public DelegationRunTests(WebApplicationFactory<Program> factory)
    {
        var db = Guid.NewGuid().ToString();
        _app = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false", ["ConnectionStrings:Lots"] = "Host=none", ["Auth:Mode"] = "Dev", ["Auth:Dev:AllowHeaders"] = "true",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(db));
                s.AddSingleton(Profiles());
                s.RemoveAll<IModelClient>();
                s.AddSingleton<IModelClient>(_model);
                s.AddSingleton<IToolSource, HostTools>();
            });
        });
    }

    [Fact]
    public async Task A_sub_run_answers_as_the_same_user_with_only_its_unapproved_tools_and_cannot_delegate_further()
    {
        var client = _app.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/runs") { Content = JsonContent.Create(new { prompt = "Where is web01?", profile = "homelab" }) };
        req.Headers.Add("X-Dev-User", "claude-test-sub");
        req.Headers.Add("X-Dev-Roles", "operator");
        var id = (await (await client.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        RunRecord parent = null!;
        for (var i = 0; i < 200; i++)
        {
            using var s = _app.Services.CreateScope();
            parent = await s.ServiceProvider.GetRequiredService<LotsDbContext>().Runs.AsNoTracking().SingleAsync(r => r.Id == id);
            if (parent.Status is RunStatus.Completed or RunStatus.Failed) break;
            await Task.Delay(50);
        }
        Assert.Equal(RunStatus.Completed, parent.Status);
        Assert.Contains("Answer from the cmdb assistant", parent.FinalAnswer);
        Assert.Contains("web01: rack 3", parent.FinalAnswer);

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var child = await db.Runs.AsNoTracking().Include(r => r.Steps).SingleAsync(r => r.ParentRunId == id);
        Assert.Equal(("claude-test-sub", "operator", "cmdb", 1), (child.UserId, child.Roles, child.Profile, child.Depth));
        Assert.Equal(RunStatus.Completed, child.Status);

        // The sub-agent was offered only what needs no approval, and could not delegate on.
        var cmdbTools = _model.Calls.First(c => c.Profile == "cmdb").Tools;
        Assert.Contains("find_host", cmdbTools);
        Assert.DoesNotContain("change_host", cmdbTools);
        var nested = child.Steps.Single(s => s.Name == "delegate");
        Assert.Contains("cannot delegate further", nested.Result);

        // Its audit rows are its own, under the parent in the trace tree; the runs list shows only the parent.
        Assert.Contains(await db.AuditLog.Where(a => a.RunId == child.Id).ToListAsync(), a => a.Tool == "find_host");
        var list = new HttpRequestMessage(HttpMethod.Get, "/runs");
        list.Headers.Add("X-Dev-User", "claude-test-sub");
        var runs = await (await client.SendAsync(list)).Content.ReadAsStringAsync();
        Assert.Contains(id.ToString(), runs);
        Assert.DoesNotContain(child.Id.ToString(), runs);
    }

    [Fact]
    public void Only_listed_profiles_can_be_delegated_to()
    {
        var p = Profiles();
        Assert.Equal(["cmdb"], p.Find("homelab")!.Delegates);
        Assert.Empty(ProfileParser.Parse("name: solo\nversion: 1\ndelegates: [solo]\n").Delegates); // never itself
    }
}
