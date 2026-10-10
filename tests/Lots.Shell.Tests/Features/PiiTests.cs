using System.Text.Json;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Runs;
using Lots.Shell.Core.Security;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Tests.Features;

/// <summary>#90: personal data in stored traces is masked when the profile asks for it; Swedish formats are recognised.</summary>
public class PiiTests
{
    private static readonly PiiKind[] All = [PiiKind.Email, PiiKind.Phone, PiiKind.Personnummer, PiiKind.PaymentCard, PiiKind.Token];

    [Theory]
    [InlineData("Kontakta anna.svensson@example.se idag", "Kontakta [email] idag")]
    [InlineData("pnr 19121212-1212 registrerat", "pnr [personnummer] registrerat")]
    [InlineData("pnr 121212-1212", "pnr [personnummer]")]
    [InlineData("personnummer 8112189876.", "personnummer [personnummer].")]
    [InlineData("samordningsnummer 701063-2391", "samordningsnummer [personnummer]")]
    [InlineData("ring 070-123 45 67 efter lunch", "ring [phone] efter lunch")]
    [InlineData("tel: +46 70 123 45 67", "tel: [phone]")]
    [InlineData("växel 08-123 456 78", "växel [phone]")]
    [InlineData("call +1 415 555 0100 now", "call [phone] now")]
    [InlineData("kort 4111 1111 1111 1111 nekades", "kort [card] nekades")]
    public void Swedish_and_international_personal_data_is_masked(string input, string expected) =>
        Assert.Equal(expected, PiiRedactor.Redact(input, All));

    [Theory]
    [InlineData("backup 2026-10-10 finished in 41 s")]
    [InlineData("order 12345678 shipped")]
    [InlineData("container 192.168.1.20:8080 healthy")]
    [InlineData("19121212-1213 has a wrong check digit")]
    [InlineData("build 20261010-1755 deployed")]
    [InlineData("version 10.0.12")]
    public void Look_alikes_are_left_alone(string text) => Assert.Equal(text, PiiRedactor.Redact(text, All));

    [Fact]
    public void Only_the_chosen_kinds_are_masked()
    {
        const string text = "anna@example.se 070-123 45 67";
        Assert.Equal("[email] 070-123 45 67", PiiRedactor.Redact(text, [PiiKind.Email]));
        Assert.Equal(text, PiiRedactor.Redact(text, []));
    }

    [Fact]
    public void Profiles_opt_in_with_kinds_and_scope()
    {
        var p = ProfileParser.Parse("name: hr\nversion: 1\npii:\n  redact: [email, phone, personnummer]\n  scope: all\n");
        Assert.Equal([PiiKind.Email, PiiKind.Phone, PiiKind.Personnummer], p.Pii!.Kinds);
        Assert.True(p.Pii.All);
        Assert.Null(ProfileParser.Parse("name: x\nversion: 1\n").Pii);
        var ex = Assert.Throws<ProfileException>(() => ProfileParser.Parse("name: x\nversion: 1\npii:\n  redact: [dna]\n"));
        Assert.Contains(ex.Errors, e => e.Contains("dna"));
    }

    private sealed class Model(params Func<IReadOnlyList<ChatMessage>, ChatMessage>[] replies) : IModelClient
    {
        private int _n;
        public List<string?> SawToolText { get; } = [];
        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
        {
            SawToolText.Add(messages.LastOrDefault(m => m.Role == "tool")?.Content);
            return Task.FromResult(new ModelResponse(replies[_n++](messages), "stop", new ModelUsage(1, 1), TimeSpan.Zero));
        }
    }

    private sealed class Lookup : IToolSource
    {
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ToolDescriptor>>(
            [new ToolDescriptor("find_person", "", JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone())]);
        public Task<string> CallAsync(string name, string argumentsJson, CancellationToken ct) =>
            Task.FromResult("Anna Svensson, anna.svensson@example.se, 070-123 45 67, 19121212-1212");
    }

    private static async Task<(RunRecord Run, Model Model, List<AuditRecord> Audit)> Execute(string pii)
    {
        var db = new LotsDbContext(new DbContextOptionsBuilder<LotsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var run = new RunRecord { Id = Guid.NewGuid(), Prompt = "who is anna?", Profile = "hr", UserId = "u", Roles = "operator", CreatedAt = DateTimeOffset.UtcNow };
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        var registry = new ProfileRegistry([ProfileParser.Parse($"""
            name: hr
            version: 1
            {pii}
            tools:
              - {"{"} name: find_person, risk: read {"}"}
            roles:
              - {"{"} name: operator, allow: [read] {"}"}
            """)]);
        var model = new Model(
            _ => new ChatMessage("assistant", null, [new ToolCall("c1", "find_person", "{\"email\":\"anna.svensson@example.se\"}")]),
            m => new ChatMessage("assistant", "Anna: anna.svensson@example.se, 070-123 45 67"));
        await new AgentRunner(db, model, new ToolInvoker([new Lookup()], registry), registry, Options.Create(new AgentOptions()), TimeProvider.System)
            .ExecuteAsync(run.Id, default);
        return (await db.Runs.Include(r => r.Steps).Include(r => r.Messages).SingleAsync(), model, await db.AuditLog.ToListAsync());
    }

    [Fact]
    public async Task With_trace_scope_the_trace_and_audit_are_masked_but_the_model_and_the_user_see_the_data()
    {
        var (run, model, audit) = await Execute("pii: { redact: [email, phone, personnummer] }");
        var tool = run.Steps.Single(s => s.Kind == StepKind.ToolCall);
        Assert.Equal("Anna Svensson, [email], [phone], [personnummer]", tool.Result);
        Assert.Equal("{\"email\":\"[email]\"}", tool.ArgumentsJson);
        Assert.DoesNotContain("@example.se", run.Steps.Last().Result);
        Assert.DoesNotContain(audit, a => (a.ArgumentsJson ?? "").Contains("@example.se"));
        Assert.Contains("anna.svensson@example.se", model.SawToolText[1]); // the run still works with the real data
        Assert.Contains("anna.svensson@example.se", run.FinalAnswer);      // and the user gets the answer they asked for
    }

    [Fact]
    public async Task With_scope_all_the_stored_conversation_and_answer_are_masked_when_the_run_ends()
    {
        var (run, _, _) = await Execute("pii: { redact: [email, phone, personnummer], scope: all }");
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal("Anna: [email], [phone]", run.FinalAnswer);
        Assert.All(run.Messages, m => Assert.DoesNotContain("@example.se", m.Content ?? ""));
        Assert.All(run.Messages, m => Assert.DoesNotContain("@example.se", m.ToolCallsJson ?? ""));
    }

    [Fact]
    public async Task Without_opt_in_nothing_is_masked()
    {
        var (run, _, _) = await Execute("");
        Assert.Contains("anna.svensson@example.se", run.Steps.Single(s => s.Kind == StepKind.ToolCall).Result);
    }
}
