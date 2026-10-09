using System.Text.Json;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Runs;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Tests.Features;

public class AgentRunnerTests
{
    private sealed class ScriptedModel(params Func<IReadOnlyList<ChatMessage>, ChatMessage>[] replies) : IModelClient
    {
        public int Calls { get; private set; }

        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
        {
            var reply = replies[Calls++](messages);
            return Task.FromResult(new ModelResponse(reply, "stop", new ModelUsage(1, 1), TimeSpan.FromMilliseconds(1)));
        }
    }

    private sealed class FakeTools(params (string Name, ToolRisk Risk)[] tools) : IToolSource
    {
        public List<string> Called { get; } = [];
        public Func<string, string> Output { get; set; } = _ => "ok";

        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>(tools.Select(t =>
                new ToolDescriptor(t.Name, t.Name, JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone(), t.Risk)).ToList());

        public Task<string> CallAsync(string name, string argumentsJson, CancellationToken ct)
        {
            Called.Add(name);
            return Task.FromResult(Output(name));
        }
    }

    private static ChatMessage CallTool(string name, string id = "c1") =>
        new("assistant", null, [new ToolCall(id, name, "{}")]);

    private static ChatMessage Answer(string text) => new("assistant", text);

    private static LotsDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<LotsDbContext>().UseInMemoryDatabase(name).Options);

    private static AgentRunner Runner(LotsDbContext db, IModelClient model, FakeTools tools, int maxSteps = 12) =>
        new(db, model, new ToolInvoker([tools]), Options.Create(new AgentOptions { MaxSteps = maxSteps }), TimeProvider.System);

    private static async Task<Guid> NewRun(LotsDbContext db)
    {
        var run = new RunRecord { Id = Guid.NewGuid(), Prompt = "which containers are unhealthy?", CreatedAt = DateTimeOffset.UtcNow };
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    [Fact]
    public async Task Runs_tool_then_completes_with_final_answer()
    {
        var db = NewDb(nameof(Runs_tool_then_completes_with_final_answer));
        var tools = new FakeTools(("list_containers", ToolRisk.Read));
        var model = new ScriptedModel(_ => CallTool("list_containers"), _ => Answer("none"));
        var id = await NewRun(db);

        await Runner(db, model, tools).ExecuteAsync(id, default);

        var run = await db.Runs.Include(r => r.Messages).Include(r => r.Steps).SingleAsync();
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal("none", run.FinalAnswer);
        Assert.Equal(["system", "user", "assistant", "tool", "assistant"], run.Messages.OrderBy(m => m.Seq).Select(m => m.Role));
        Assert.Equal(["list_containers"], tools.Called);

        var steps = run.Steps.OrderBy(s => s.Seq).ToList();
        Assert.Equal([StepKind.ModelCall, StepKind.ToolCall, StepKind.ModelCall], steps.Select(s => s.Kind));
        Assert.Equal("list_containers", steps[1].Name);
        Assert.Equal("c1", steps[1].ToolCallId);
        Assert.Equal("ok", steps[1].Result);
        Assert.Equal(1, steps[0].PromptTokens);
    }

    [Fact]
    public async Task Non_read_tools_are_hidden_and_denied()
    {
        var db = NewDb(nameof(Non_read_tools_are_hidden_and_denied));
        var tools = new FakeTools(("restart_container", ToolRisk.Destructive));
        IReadOnlyList<ChatMessage>? seen = null;
        var model = new ScriptedModel(_ => CallTool("restart_container"), m => { seen = m; return Answer("denied"); });
        var id = await NewRun(db);

        await Runner(db, model, tools).ExecuteAsync(id, default);

        Assert.Empty(tools.Called);
        Assert.Contains("not permitted", seen!.Last(m => m.Role == "tool").Content);
        Assert.Empty(await new ToolInvoker([tools]).DefinitionsAsync(default));
    }

    [Fact]
    public async Task Tool_output_is_truncated()
    {
        var db = NewDb(nameof(Tool_output_is_truncated));
        var tools = new FakeTools(("get_logs", ToolRisk.Read)) { Output = _ => new string('x', 100_000) };
        var model = new ScriptedModel(_ => CallTool("get_logs"), _ => Answer("done"));
        var id = await NewRun(db);

        await Runner(db, model, tools).ExecuteAsync(id, default);

        var tool = (await db.RunMessages.ToListAsync()).Single(m => m.Role == "tool");
        Assert.True(tool.Content!.Length < 9_000);
        Assert.Contains("[truncated", tool.Content);
    }

    [Fact]
    public async Task Stops_after_max_steps()
    {
        var db = NewDb(nameof(Stops_after_max_steps));
        var tools = new FakeTools(("t", ToolRisk.Read));
        var model = new ScriptedModel(Enumerable.Repeat<Func<IReadOnlyList<ChatMessage>, ChatMessage>>(_ => CallTool("t"), 5).ToArray());
        var id = await NewRun(db);

        await Runner(db, model, tools, maxSteps: 3).ExecuteAsync(id, default);

        var run = await db.Runs.SingleAsync();
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("3 model calls", run.Error);
        Assert.Equal(3, model.Calls);
    }

    [Fact]
    public async Task Interrupted_run_resumes_without_repeating_finished_work()
    {
        var name = nameof(Interrupted_run_resumes_without_repeating_finished_work);
        var tools = new FakeTools(("a", ToolRisk.Read), ("b", ToolRisk.Read));
        var id = Guid.Empty;

        // First process: model asks for two tools in one message; shutdown arrives after the first result is saved.
        using (var db1 = NewDb(name))
        {
            id = await NewRun(db1);
            var twoCalls = new ChatMessage("assistant", null, [new ToolCall("c1", "a", "{}"), new ToolCall("c2", "b", "{}")]);
            var model1 = new ScriptedModel(_ => twoCalls);
            using var cts = new CancellationTokenSource();
            tools.Output = n => { if (n == "a") cts.Cancel(); return "ok-" + n; };
            // Tool "b" never runs: the next invoker call observes cancellation.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner(db1, model1, tools).ExecuteAsync(id, cts.Token));
        }
        Assert.Equal(["a"], tools.Called);

        // Second process: fresh context over the same database continues the run.
        using var db2 = NewDb(name);
        Assert.Equal(RunStatus.Running, (await db2.Runs.SingleAsync()).Status);
        tools.Output = n => "ok-" + n;
        var model2 = new ScriptedModel(_ => Answer("finished"));

        await Runner(db2, model2, tools).ExecuteAsync(id, default);

        var run = await db2.Runs.Include(r => r.Messages).SingleAsync();
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal("finished", run.FinalAnswer);
        Assert.Equal(["a", "b"], tools.Called); // "a" was not executed twice
        Assert.Equal(1, model2.Calls);
    }

    [Fact]
    public async Task Old_tool_results_are_dropped_from_the_request_when_context_budget_is_exceeded()
    {
        var db = NewDb(nameof(Old_tool_results_are_dropped_from_the_request_when_context_budget_is_exceeded));
        var tools = new FakeTools(("logs", ToolRisk.Read)) { Output = _ => new string('x', 3_000) };
        IReadOnlyList<ChatMessage>? lastRequest = null;
        var model = new ScriptedModel(
            _ => CallTool("logs", "c1"), _ => CallTool("logs", "c2"), _ => CallTool("logs", "c3"),
            m => { lastRequest = m; return Answer("done"); });
        var id = await NewRun(db);
        var runner = new AgentRunner(db, model, new ToolInvoker([tools]),
            Options.Create(new AgentOptions { MaxContextChars = 7_000 }), TimeProvider.System);

        await runner.ExecuteAsync(id, default);

        var toolMessages = lastRequest!.Where(m => m.Role == "tool").ToList();
        Assert.Equal(3, toolMessages.Count);
        Assert.Contains("omitted", toolMessages[0].Content);
        Assert.Equal(3_000, toolMessages[2].Content!.Length); // newest result survives
        Assert.Equal(RunStatus.Completed, (await db.Runs.SingleAsync()).Status);
        var stored = await db.RunMessages.Where(m => m.Role == "tool").ToListAsync();
        Assert.All(stored, m => Assert.Equal(3_000, m.Content!.Length)); // stored conversation untouched
    }

    [Fact]
    public async Task Empty_reply_is_nudged_once_then_answered()
    {
        var db = NewDb(nameof(Empty_reply_is_nudged_once_then_answered));
        var model = new ScriptedModel(_ => Answer(""), m => Answer(m.Any(x => x.Role == "user" && x.Content!.Contains("empty")) ? "real answer" : "no nudge"));
        var id = await NewRun(db);

        await Runner(db, model, new FakeTools()).ExecuteAsync(id, default);

        var run = await db.Runs.SingleAsync();
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal("real answer", run.FinalAnswer);
    }

    [Fact]
    public async Task Two_empty_replies_fail_the_run()
    {
        var db = NewDb(nameof(Two_empty_replies_fail_the_run));
        var model = new ScriptedModel(_ => Answer(""), _ => Answer(" "));
        var id = await NewRun(db);

        await Runner(db, model, new FakeTools()).ExecuteAsync(id, default);

        var run = await db.Runs.SingleAsync();
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("empty answer", run.Error);
    }
}
