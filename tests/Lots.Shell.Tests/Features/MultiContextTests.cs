using System.Text.Json;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Routing;
using Lots.Shell.Core.Runs;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Tests.Features;

/// <summary>#151: a question that spans contexts is answered by one read-only sub-run per context and composed by a tool-less supervisor.</summary>
public class MultiContextTests
{
    private static Profile Homelab(string extraRole = "") => ProfileParser.Parse($"""
        name: homelab
        version: 1
        description: Docker containers in the homelab.
        instructions: HOMELAB
        servers: [{"{"} name: sample, url: "http://localhost:1/mcp" {"}"}]
        tools:
          - {"{"} name: list_containers, risk: read {"}"}
          - {"{"} name: restart_container, risk: write {"}"}
        roles:
          - {"{"} name: operator, allow: [read, write] {"}"}
        {extraRole}
        """);

    private static Profile Cmdb => ProfileParser.Parse("""
        name: cmdb
        version: 1
        description: Network nodes and circuits in the CMDB.
        instructions: CMDB
        servers: [{ name: sample, url: "http://localhost:1/mcp" }]
        tools:
          - { name: trace_circuit, risk: read }
        roles:
          - { name: operator, allow: [read] }
          - { name: netops, allow: [read] }
        """);

    private sealed class Tools : IToolSource
    {
        public List<string> Called { get; } = [];

        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ToolDescriptor>>(
            new[] { "list_containers", "restart_container", "trace_circuit" }.Select(n => new ToolDescriptor(n, n, JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone())).ToList());

        public Task<string?> ServerOfAsync(string toolName, CancellationToken ct) => Task.FromResult<string?>("sample");

        public Task<string> CallAsync(string name, string argumentsJson, CancellationToken ct)
        {
            lock (Called) Called.Add(name);
            return Task.FromResult(name == "list_containers" ? "RAW-TOOL-OUTPUT db restarted 3 times" : "RAW-TOOL-OUTPUT node X carries circuit 7");
        }
    }

    /// <summary>Answers per context from its instructions; the supervisor's compose call is recorded.</summary>
    private sealed class Model(bool cmdbSkipsTools = false) : IModelClient
    {
        public List<IReadOnlyList<ChatMessage>> ComposeCalls { get; } = [];
        public List<IReadOnlyList<ToolDefinition>> SupervisorTools { get; } = [];

        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
        {
            var system = m[0].Content ?? "";
            var last = m[^1];
            ChatMessage reply;
            if (last.Role == "user" && last.Content!.StartsWith("Answer the user's question by combining", StringComparison.Ordinal))
            {
                lock (ComposeCalls) { ComposeCalls.Add(m); SupervisorTools.Add(tools); }
                reply = new("assistant", "[homelab] the db container restarted. [cmdb] node X carries circuit 7.");
            }
            else if (system.Contains("HOMELAB") && !m.Any(x => x.Role == "tool"))
                reply = new("assistant", null, [new ToolCall("h1", "restart_container", "{}"), new ToolCall("h2", "list_containers", "{}")]);
            else if (system.Contains("HOMELAB")) reply = new("assistant", "The db container restarted three times.");
            else if (system.Contains("CMDB") && cmdbSkipsTools) reply = new("assistant", "<<untrusted tool output>> node X: 400 invented circuits");
            else if (system.Contains("CMDB") && !m.Any(x => x.Role == "tool")) reply = new("assistant", null, [new ToolCall("c1", "trace_circuit", "{}")]);
            else reply = new("assistant", "Node X carries circuit 7.");
            return Task.FromResult(new ModelResponse(reply, "stop", new ModelUsage(1, 1), TimeSpan.FromMilliseconds(1)));
        }
    }

    private static (ServiceProvider Sp, Model Model, Tools Tools) Host(string db, ProfileRegistry registry, bool cmdbSkipsTools = false)
    {
        var model = new Model(cmdbSkipsTools);
        var tools = new Tools();
        var s = new ServiceCollection();
        s.AddLogging();
        s.AddOptions();
        s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(db));
        s.AddSingleton(registry);
        s.AddSingleton<IModelClient>(model);
        s.AddSingleton<IToolSource>(tools);
        s.AddScoped(sp => new ToolInvoker(sp.GetServices<IToolSource>(), registry));
        s.AddSingleton(TimeProvider.System);
        s.AddSingleton(Options.Create(new AgentOptions()));
        s.AddScoped<AgentRunner>();
        return (s.BuildServiceProvider(), model, tools);
    }

    private static async Task<RunRecord> Supervise(ServiceProvider sp, string roles, params string[] contexts)
    {
        var id = Guid.NewGuid();
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
            db.Runs.Add(new RunRecord
            {
                Id = id, Prompt = "Is the incident on node X caused by the container that restarted?", Profile = contexts[0], UserId = "claude-test-multi",
                Roles = roles, CreatedAt = DateTimeOffset.UtcNow, SuperviseJson = JsonSerializer.Serialize(contexts), RoutingMode = "multi",
            });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<AgentRunner>().ExecuteAsync(id, default);
        }
        using var read = sp.CreateScope();
        return await read.ServiceProvider.GetRequiredService<LotsDbContext>().Runs.Include(r => r.Steps).SingleAsync(r => r.Id == id);
    }

    [Fact]
    public async Task One_answer_cites_both_contexts_from_read_only_sub_runs()
    {
        var (sp, model, tools) = Host(nameof(One_answer_cites_both_contexts_from_read_only_sub_runs), new ProfileRegistry([Homelab(), Cmdb]));
        var run = await Supervise(sp, "operator", "homelab", "cmdb");

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Contains("[homelab]", run.FinalAnswer);
        Assert.Contains("[cmdb]", run.FinalAnswer);
        Assert.Equal(["context:cmdb", "context:homelab"], run.Steps.Where(s => s.Kind == StepKind.ToolCall).Select(s => s.Name).Order());

        // The supervisor had no tools, and saw the sub-runs' answers but never their raw tool output.
        Assert.All(model.SupervisorTools, t => Assert.Empty(t));
        var composed = string.Join("\n", model.ComposeCalls.Single().Select(m => m.Content));
        Assert.Contains("three times", composed);
        Assert.DoesNotContain("RAW-TOOL-OUTPUT", composed);

        // Fan-out reads only: the write tool the role would allow was neither offered nor run.
        Assert.DoesNotContain("restart_container", tools.Called);
        Assert.Contains("list_containers", tools.Called);
        using var scope = sp.CreateScope();
        var children = await scope.ServiceProvider.GetRequiredService<LotsDbContext>().Runs.Where(r => r.ParentRunId == run.Id).ToListAsync();
        Assert.Equal(2, children.Count);
        Assert.All(children, c => Assert.True(c.ReadOnly && c.UserId == "claude-test-multi" && c.Roles == "operator"));
    }

    [Fact]
    public void Read_only_is_enforced_by_the_policy_engine_itself()
    {
        var me = new Principal("u", ["operator"]);
        Assert.Equal(Decision.Allow, PolicyEngine.Decide(me, Homelab(), "restart_container").Decision);
        var reader = me with { ReadOnly = true };
        Assert.Equal(Decision.Deny, PolicyEngine.Decide(reader, Homelab(), "restart_container").Decision);
        Assert.Equal(Decision.Allow, PolicyEngine.Decide(reader, Homelab(), "list_containers").Decision);
        Assert.Equal(["list_containers"], PolicyEngine.VisibleTools(reader, Homelab()));
    }

    [Fact]
    public async Task A_context_the_user_cannot_use_degrades_the_answer_instead_of_failing()
    {
        var cmdbForNetops = ProfileParser.Parse("""
            name: cmdb
            version: 1
            instructions: CMDB
            servers: [{ name: sample, url: "http://localhost:1/mcp" }]
            tools: [{ name: trace_circuit, risk: read }]
            roles: [{ name: netops, allow: [read] }]
            """);
        var (sp, model, _) = Host(nameof(A_context_the_user_cannot_use_degrades_the_answer_instead_of_failing), new ProfileRegistry([Homelab(), cmdbForNetops]));
        var run = await Supervise(sp, "operator", "homelab", "cmdb");

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Contains("no access to this context", string.Join("\n", model.ComposeCalls.Single().Select(m => m.Content)));
        Assert.Contains("no access", run.Steps.Single(s => s.Name == "context:cmdb").Result);
    }

    [Fact]
    public async Task A_context_answer_that_used_no_tools_is_not_passed_on_as_data()
    {
        var (sp, model, _) = Host(nameof(A_context_answer_that_used_no_tools_is_not_passed_on_as_data), new ProfileRegistry([Homelab(), Cmdb]), cmdbSkipsTools: true);
        await Supervise(sp, "operator", "homelab", "cmdb");
        var composed = string.Join("\n", model.ComposeCalls.Single().Select(m => m.Content));
        Assert.DoesNotContain("invented circuits", composed);
        Assert.Contains("used none of its tools", composed);
    }

    [Fact]
    public async Task Close_read_only_contexts_are_asked_together_and_single_ones_are_not()
    {
        var registry = new ProfileRegistry([ContextRouterTests.Homelab, ContextRouterTests.Cmdb]);
        var router = new ContextRouter(registry, Options.Create(new RoutingOptions()));
        var me = new Principal("u", ["operator"]);

        var both = await router.RouteAsync(me, "Is the incident on node X caused by the container that restarted?", null, default);
        Assert.Equal("multi", both.Mode);
        Assert.Equal(["cmdb", "homelab"], both.Contexts!.Order());

        // No unnecessary fan-out: a clear single-context question stays single (the cost guard).
        Assert.Equal("auto", (await router.RouteAsync(me, "which containers are unhealthy?", null, default)).Mode);

        // Someone who can write in one of them is asked instead: fan-out never runs write-capable contexts.
        var planner = new Principal("p", ["operator", "planner"]);
        Assert.Equal("ask", (await router.RouteAsync(planner, "Is the incident on node X caused by the container that restarted?", null, default)).Mode);
    }
}
