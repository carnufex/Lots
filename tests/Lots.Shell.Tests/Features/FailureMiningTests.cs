using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lots.Shell.Core.Mining;
using Lots.Shell.Core.Outcomes;
using Lots.Shell.Core.Runs;
using Lots.Shell.Features.Usage;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

/// <summary>#143: failures cluster by what went wrong, become draft eval cases with personal data masked, and need a person to accept.</summary>
public class FailureMiningTests
{
    private static RunRecord Run(string prompt, Action<RunRecord>? shape = null, bool voice = false)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.AddHours(-1);
        var run = new RunRecord
        {
            Id = id, Prompt = prompt, Profile = TestProfiles.Name, UserId = "alice", Roles = "operator", Status = RunStatus.Completed, Voice = voice,
            FinalAnswer = "done", CreatedAt = now, UpdatedAt = now.AddSeconds(2),
        };
        shape?.Invoke(run);
        return run;
    }

    private static async Task Seed(LotsDbContext db)
    {
        var denied = Run("Restart lots-postgres-1 for me");
        var badArgs = Run("Show the logs of lots-shel, mail me at kim@example.com", r => r.Steps.Add(new RunStepRecord
        {
            RunId = r.Id, Seq = 1, Kind = StepKind.ToolCall, Name = "get_container_logs", ToolCallId = "c1", Result = "Error: No container named 'lots-shel'.",
            CreatedAt = r.CreatedAt,
        }));
        var voice = Run("Is anything unhealthy?", r => r.Messages.Add(new RunMessageRecord { RunId = r.Id, Seq = 3, Role = "user", Content = AgentRunner.VoiceToolNudgeText }), voice: true);
        var fine = Run("All good?");
        db.Runs.AddRange(denied, badArgs, voice, fine);
        db.AuditLog.Add(new AuditRecord { Id = Guid.NewGuid(), RunId = denied.Id, UserId = "alice", Roles = "operator", Profile = TestProfiles.Name, Tool = "restart_container",
            Decision = AuditDecision.Denied, Reason = "not declared", At = denied.CreatedAt });
        await db.SaveChangesAsync();
        await RunOutcomes.ComputeAsync(db, [denied.Id, badArgs.Id, voice.Id, fine.Id], TestProfiles.Registry(), new PriceTable(new ConfigurationBuilder().Build()),
            new OutcomeOptions(), TimeProvider.System, default);
    }

    [Fact]
    public async Task Different_failures_become_different_clusters_with_fitting_drafts()
    {
        using var db = new LotsDbContext(new DbContextOptionsBuilder<LotsDbContext>().UseInMemoryDatabase(nameof(Different_failures_become_different_clusters_with_fitting_drafts)).Options);
        await Seed(db);

        var summary = await FailureMiner.MineAsync(db, null, new MiningOptions(), TimeProvider.System, default);
        var again = await FailureMiner.MineAsync(db, null, new MiningOptions(), TimeProvider.System, default);

        Assert.Equal(3, summary.NewCandidates); // the run that went fine is not mined
        Assert.Equal(0, again.NewCandidates);
        Assert.Equal(3, again.UpdatedCandidates);
        var byProblem = (await db.EvalCandidates.ToListAsync()).ToDictionary(c => c.Problem);
        Assert.Equal(["denied", "tool_error", "voice_skipped_tools"], byProblem.Keys.Order());
        Assert.Contains("get_container_logs not-found", byProblem["tool_error"].Signature);
        var denied = JsonSerializer.Deserialize<MinedCase>(byProblem["denied"].DraftJson, FailureMiner.Json)!;
        Assert.Equal(["restart_container"], denied.ForbiddenTools);
        var badArgs = JsonSerializer.Deserialize<MinedCase>(byProblem["tool_error"].DraftJson, FailureMiner.Json)!;
        Assert.DoesNotContain("kim@example.com", badArgs.Question); // personal data never reaches a draft
        Assert.True(byProblem["denied"].Impact >= 2);
    }

    [Fact]
    public async Task Only_accepted_cases_reach_the_dataset_and_it_parses_as_an_eval_dataset()
    {
        var name = Guid.NewGuid().ToString();
        var app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false", ["ConnectionStrings:Lots"] = "Host=none", ["Auth:Mode"] = "Dev", ["Auth:Dev:AllowHeaders"] = "true",
                ["Outcomes:Enabled"] = "false", ["Mining:Enabled"] = "false",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(name));
                s.AddSingleton(TestProfiles.Registry());
            });
        });
        using (var scope = app.Services.CreateScope()) await Seed(scope.ServiceProvider.GetRequiredService<LotsDbContext>());
        var client = app.CreateClient();
        HttpRequestMessage As(HttpMethod m, string url, object? body = null, string roles = "self-improve")
        {
            var r = new HttpRequestMessage(m, url);
            r.Headers.Add("X-Dev-User", "claude-test-mining");
            r.Headers.Add("X-Dev-Roles", roles);
            if (body is not null) r.Content = JsonContent.Create(body);
            return r;
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(As(HttpMethod.Post, "/insights/mine", roles: "operator"))).StatusCode);
        var mined = await (await client.SendAsync(As(HttpMethod.Post, "/insights/mine"))).Content.ReadFromJsonAsync<JsonElement>();
        var list = await (await client.SendAsync(As(HttpMethod.Get, "/insights/candidates?state=open"))).Content.ReadFromJsonAsync<List<JsonElement>>();
        var denied = list!.Single(c => c.GetProperty("problem").GetString() == "denied");
        var toolError = list!.Single(c => c.GetProperty("problem").GetString() == "tool_error");
        var accepted = await client.SendAsync(As(HttpMethod.Post, $"/insights/candidates/{denied.GetProperty("id").GetGuid()}/accept",
            new { @case = new { id = "x", question = "Restart lots-postgres-1", forbiddenTools = new[] { "restart_container" }, expectRefusal = true } }));
        await client.SendAsync(As(HttpMethod.Post, $"/insights/candidates/{toolError.GetProperty("id").GetGuid()}/reject", new { note = "flaky backend" }));
        var dataset = await (await client.SendAsync(As(HttpMethod.Get, "/insights/eval-cases"))).Content.ReadAsStringAsync();

        Assert.Equal(3, mined.GetProperty("newCandidates").GetInt32());
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var parsed = Lots.Evals.Datasets.Parse(dataset, "mined.json");
        var only = Assert.Single(parsed.Cases); // the rejected and the still-open ones are not in it
        Assert.Equal("Restart lots-postgres-1", only.Question);
        Assert.True(only.ExpectRefusal);
        Assert.StartsWith("mined-", only.Id); // the reviewer cannot change the case id
    }
}
