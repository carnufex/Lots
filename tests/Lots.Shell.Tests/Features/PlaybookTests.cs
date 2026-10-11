using System.Text.Json;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Playbooks;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Runs;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Tests.Features;

/// <summary>#161 (ADR 0022): playbooks are enforced by the shell. The step's tools only, approvals where the step says, no skipping.</summary>
public class PlaybookTests
{
    private static string Repo(string path) =>
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", path);

    private static readonly Profile Ops = ProfileParser.Parse(File.ReadAllText(Repo("examples/profiles/docker-ops.yaml")));
    private static readonly ProfileRegistry Registry = new([Ops]);

    private static PlaybookSpec Sample()
    {
        var errors = new List<string>();
        var spec = PlaybookParser.Parse(File.ReadAllText(Repo("examples/playbooks/restart-after-logs.yaml")), "restart-after-logs", Registry, errors);
        Assert.Empty(errors);
        return spec!;
    }

    [Fact]
    public void The_sample_playbook_is_valid_and_bad_ones_are_refused()
    {
        Assert.Equal(["inspect", "restart", "verify"], Sample().Steps.Select(s => s.Name));
        var errors = new List<string>();
        PlaybookParser.Parse("version: 1\nprofile: docker-ops\ndescription: x\nsteps:\n  - { name: a, instruction: do, tools: [drop_database] }\n", "bad", Registry, errors);
        Assert.Contains(errors, e => e.Contains("'drop_database' is not declared"));
        errors.Clear();
        PlaybookParser.Parse("version: 1\nprofile: docker-ops\ndescription: x\nsteps:\n  - { name: a, instruction: do, tools: [list_containers], check: { tool: restart_container } }\n", "bad", Registry, errors);
        Assert.Contains(errors, e => e.Contains("check.tool must be one of the step's tools"));
        errors.Clear();
        PlaybookParser.Parse("version: 1\nprofile: docker-ops\ndescription: x\nsteps: []\nsurprise: 1\n", "bad", Registry, errors);
        Assert.NotEmpty(errors); // strict YAML
    }

    [Fact]
    public void A_step_only_narrows_what_policy_allows()
    {
        var spec = Sample();
        Principal At(int step, params string[] roles) => new("u", roles) { Playbook = new PlaybookState(spec, step, 0, []).Gate() };

        Assert.Equal(Decision.Allow, PolicyEngine.Decide(At(0, "operator"), Ops, "get_container_logs").Decision);
        Assert.Equal(Decision.Deny, PolicyEngine.Decide(At(0, "operator"), Ops, "restart_container").Decision); // a later step's tool
        Assert.Equal(Decision.RequireApproval, PolicyEngine.Decide(At(1, "operator"), Ops, "restart_container").Decision);
        Assert.Equal(Decision.Deny, PolicyEngine.Decide(At(1, "operator"), Ops, "get_container_logs").Decision); // an earlier step's tool
        Assert.Equal(Decision.Deny, PolicyEngine.Decide(At(0, "guest"), Ops, "get_container_logs").Decision); // never widens: no role, no call
        Assert.Equal(Decision.Allow, PolicyEngine.Decide(At(0, "guest"), Ops, PlaybookParser.CompleteStep).Decision);
        Assert.Equal(Decision.Deny, PolicyEngine.Decide(At(3, "operator"), Ops, "list_containers").Decision); // finished: nothing
        Assert.Equal(["get_container_logs", "list_containers"], PolicyEngine.VisibleTools(At(0, "operator"), Ops).Order());
    }

    private sealed class Tools : IToolSource
    {
        public List<string> Called { get; } = [];

        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ToolDescriptor>>(
            new[] { "list_containers", "get_container_logs", "restart_container" }.Select(n => new ToolDescriptor(n, n, JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone())).ToList());

        public Task<string?> ServerOfAsync(string toolName, CancellationToken ct) => Task.FromResult<string?>("docker-ops");

        public Task<string> CallAsync(string name, string argumentsJson, CancellationToken ct)
        {
            Called.Add(name);
            return Task.FromResult(name switch
            {
                "get_container_logs" => "web: out of memory, killed",
                "restart_container" => "web restarted",
                _ => Called.Contains("restart_container") ? "web running" : "web exited",
            });
        }
    }

    /// <summary>Plays the given replies in order; records which tools the model was offered each time.</summary>
    private sealed class Script(params Func<IReadOnlyList<ChatMessage>, ChatMessage>[] replies) : IModelClient
    {
        private int _i;
        public List<string[]> Offered { get; } = [];

        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
        {
            Offered.Add(tools.Select(t => t.Name).Order().ToArray());
            return Task.FromResult(new ModelResponse(replies[Math.Min(_i++, replies.Length - 1)](m), "stop", new ModelUsage(1, 1), TimeSpan.FromMilliseconds(1)));
        }
    }

    private static ChatMessage Call(string tool, string args = "{}") => new("assistant", null, [new ToolCall(Guid.NewGuid().ToString("N")[..8], tool, args)]);
    private static ChatMessage Done(string evidence) => Call(PlaybookParser.CompleteStep, JsonSerializer.Serialize(new { evidence }));

    private static async Task<(LotsDbContext Db, RunRecord Run, Tools Tools, AgentRunner Runner)> Start(string name, Script model)
    {
        var db = new LotsDbContext(new DbContextOptionsBuilder<LotsDbContext>().UseInMemoryDatabase(name).Options);
        var run = new RunRecord
        {
            Id = Guid.NewGuid(), Prompt = "the web service keeps dying, fix it", Profile = "docker-ops", UserId = "claude-test-playbook", Roles = "operator",
            CreatedAt = DateTimeOffset.UtcNow, RoutingMode = "playbook",
        };
        new PlaybookState(Sample(), 0, 0, []).Save(run);
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        var tools = new Tools();
        return (db, run, tools, new AgentRunner(db, model, new ToolInvoker([tools], Registry), Registry, Options.Create(new AgentOptions()), TimeProvider.System));
    }

    [Fact]
    public async Task The_sample_playbook_runs_end_to_end_with_its_approval()
    {
        var model = new Script(
            _ => Call("get_container_logs"), _ => Done("out of memory"),
            _ => Call("restart_container"),
            _ => Done("restarted"), _ => Call("list_containers"), _ => Done("running"),
            _ => new ChatMessage("assistant", "It ran out of memory; I restarted it and it runs again."));
        var (db, run, tools, runner) = await Start(nameof(The_sample_playbook_runs_end_to_end_with_its_approval), model);

        await runner.ExecuteAsync(run.Id, default);
        Assert.Equal(RunStatus.WaitingForApproval, run.Status); // the restart step needs a person
        var approval = await db.Approvals.SingleAsync();
        Assert.Equal("restart_container", approval.ToolName);
        approval.Status = ApprovalStatus.Approved;
        approval.DecidedBy = "claude-test-approver";
        approval.DecidedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await runner.ExecuteAsync(run.Id, default);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(["get_container_logs", "restart_container", "list_containers"], tools.Called);
        var state = PlaybookState.Of(run)!;
        Assert.True(state.Finished);
        Assert.Equal(["inspect", "restart", "verify"], state.Completed);
        // The model only ever saw the current step's tools.
        Assert.Equal(["complete_step", "get_container_logs", "list_containers"], model.Offered[0]);
        Assert.Contains(["complete_step", "restart_container"], model.Offered);
        // Every decision records the playbook and its version.
        Assert.All(await db.AuditLog.Where(a => a.RunId == run.Id).ToListAsync(), a => Assert.StartsWith("restart-after-logs v1", a.Playbook));
    }

    [Fact]
    public async Task A_step_cannot_be_skipped()
    {
        var model = new Script(
            _ => Call("restart_container"), // jump ahead: not part of the first step
            _ => Done("trust me"),          // the check fails: no logs were read
            _ => new ChatMessage("assistant", "Restarted."),
            _ => new ChatMessage("assistant", "Restarted, really."));
        var (_, run, tools, runner) = await Start(nameof(A_step_cannot_be_skipped), model);

        await runner.ExecuteAsync(run.Id, default);
        Assert.Empty(tools.Called);
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("step 'inspect'", run.Error);
        Assert.Contains("not done", run.Messages.Single(m => m.Role == "tool" && m.Content!.Contains("is not done")).Content);
    }
}
