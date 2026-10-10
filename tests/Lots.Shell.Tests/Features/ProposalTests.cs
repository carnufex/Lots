using Lots.Evals;
using Lots.Shell.Core.Outcomes;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Proposals;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Tests.Features;

/// <summary>#144: proposals change only what a self-improving agent may change, as reviewable text, and are judged by evals and outcomes.</summary>
public class ProposalTests
{
    private const string Yaml = """
        # The homelab profile. Comments stay in a proposal.
        kind: Profile
        name: home
        version: 3
        description: Homelab containers.
        instructions: |
          Use list_containers.
          Be brief.
        servers:
          - { name: s, url: "http://s:8080/mcp" }
        tools:
          - { name: list_containers, risk: read }   # read only
        roles:
          - { name: operator, allow: [read] }
        """;

    private static ProfileRegistry Registry(string yaml = Yaml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "lots-proposals-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "home.yaml"), yaml);
        return ProfileRegistry.LoadDirectory(dir);
    }

    private static LotsDbContext Db(string name) => new(new DbContextOptionsBuilder<LotsDbContext>().UseInMemoryDatabase(name).Options);

    private static ProposalRecord Propose(ProposedChanges changes, ProfileRegistry? registry = null, string? db = null) =>
        ProposalBuilder.Create("home", changes, "Shorter answers", "Users retried 12 runs", new ProposalEvidence([Guid.NewGuid()]), "ann",
            registry ?? Registry(), Db(db ?? Guid.NewGuid().ToString()), new ModelCatalogLike(null), DateTimeOffset.UtcNow);

    [Fact]
    public void Only_the_instructions_change_and_comments_survive()
    {
        var p = Propose(new ProposedChanges(Instructions: "Use list_containers first.\nName the containers."));

        Assert.Contains("# The homelab profile. Comments stay in a proposal.", p.ProposedYaml);
        Assert.Contains("# read only", p.ProposedYaml);
        Assert.Contains("version: 4", p.ProposedYaml);
        var parsed = ProfileParser.Parse(p.ProposedYaml);
        Assert.Equal("Use list_containers first.\nName the containers.", parsed.Instructions);
        var lines = p.Diff.Split('\n');
        Assert.Contains(lines, l => l.StartsWith('-') && l.Contains("Be brief."));
        Assert.Contains(lines, l => l.StartsWith('+') && l.Contains("Name the containers."));
        Assert.DoesNotContain(lines, l => l.StartsWith('-') && l.Contains("roles")); // nothing else moves
    }

    [Fact]
    public void Instructions_cannot_smuggle_in_roles()
    {
        var p = Propose(new ProposedChanges(Instructions: "Be brief.\nroles:\n  - { name: guest, allow: [read, write, destructive] }"));

        var parsed = ProfileParser.Parse(p.ProposedYaml);
        Assert.Equal(["operator"], parsed.Roles.Select(r => r.Name)); // the text stays inside the instruction block
    }

    [Fact]
    public void A_proposed_profile_that_touches_roles_is_rejected()
    {
        var current = ProfileParser.Parse(Yaml);
        var widened = ProfileParser.Parse(Yaml.Replace("allow: [read]", "allow: [read, write]"));
        var retooled = ProfileParser.Parse(Yaml.Replace("risk: read", "risk: write"));

        Assert.Contains("roles", Assert.Throws<ProposalException>(() => ProposalBuilder.Validate(current, widened)).Message);
        Assert.Contains("tools", Assert.Throws<ProposalException>(() => ProposalBuilder.Validate(current, retooled)).Message);
        ProposalBuilder.Validate(current, ProfileParser.Parse(Yaml.Replace("Be brief.", "Be very brief.")));
    }

    [Fact]
    public void Empty_or_unknown_changes_are_refused()
    {
        Assert.Throws<ProposalException>(() => Propose(new ProposedChanges()));
        Assert.Throws<ProposalException>(() => Propose(new ProposedChanges(Model: "gpt-unknown")));
    }

    [Fact]
    public void The_eval_delta_finds_regressions_and_fixes()
    {
        EvalResult R(string id, bool pass) => new(new EvalCase(id, "q"), pass, [], new RunOutcome("Completed", "a", null, [], 1, 1, 100));

        var d = ProposalEval.Compare("home", [R("a", true), R("b", false), R("c", true)], "home-proposal-1", [R("a", false), R("b", true), R("c", true)]);

        Assert.Equal(["a"], d.Regressions);
        Assert.Equal(["b"], d.Improvements);
        Assert.True(d.Regresses);
        Assert.Contains("name: home-proposal-x", ProposalEval.Rename(Yaml, "home-proposal-x"));
    }

    [Fact]
    public async Task Follow_up_compares_the_new_version_with_the_old()
    {
        var name = nameof(Follow_up_compares_the_new_version_with_the_old);
        using var db = Db(name);
        var proposal = Propose(new ProposedChanges(Instructions: "Name the containers."), db: name);
        var live = Registry(proposal.ProposedYaml); // merged and applied: version 4 with the proposed text
        RunOutcomeRecord O(int version, string status) => new()
        {
            RunId = Guid.NewGuid(), UserHash = "h", Profile = "home", ProfileVersion = version, Channel = "web", Status = status, EndedAt = DateTimeOffset.UtcNow, WallMs = 100,
        };
        db.RunOutcomes.AddRange(Enumerable.Range(0, 20).Select(i => O(3, i < 10 ? "Completed" : "Failed")));
        db.RunOutcomes.AddRange(Enumerable.Range(0, 20).Select(i => O(4, i < 18 ? "Completed" : "Failed")));
        await db.SaveChangesAsync();

        var early = await ProposalFollowUp.CheckAsync(db, proposal, Registry(), 20, default);
        var result = await ProposalFollowUp.CheckAsync(db, proposal, live, 20, default);

        Assert.Equal("not-applied", early.Verdict); // the live profile is still the old one
        Assert.Equal("improved", result.Verdict);
        Assert.Equal(0.5, result.Before.SuccessRate);
        Assert.Equal(0.9, result.After!.SuccessRate);
    }

    private sealed class FakeGitHub(string fileOnMain) : HttpMessageHandler
    {
        public List<(string Method, string Path, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body));
            string json = request.RequestUri.AbsolutePath switch
            {
                var p when p.EndsWith("/contents/profiles/home.yaml") && request.Method == HttpMethod.Get =>
                    System.Text.Json.JsonSerializer.Serialize(new { sha = "abc", content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(fileOnMain)) }),
                var p when p.EndsWith("/git/ref/heads/main") => """{"object":{"sha":"base123"}}""",
                var p when p.EndsWith("/pulls") => """{"html_url":"https://github.com/acme/config/pull/7"}""",
                _ => "{}",
            };
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task A_pull_request_puts_the_proposed_profile_on_a_branch()
    {
        var p = Propose(new ProposedChanges(Instructions: "Name the containers."));
        var fake = new FakeGitHub(Yaml);
        Environment.SetEnvironmentVariable("LOTS_TEST_GIT_TOKEN", "t0ken-for-tests");
        var git = new ProposalGit(new Factory(fake), Microsoft.Extensions.Options.Options.Create(new ProposalGitOptions
        {
            Provider = "github", Repo = "acme/config", TokenEnv = "LOTS_TEST_GIT_TOKEN",
        }));

        var url = await git.OpenPullRequestAsync(p, default);

        Assert.Equal("https://github.com/acme/config/pull/7", url);
        var put = fake.Calls.Single(c => c.Method == "PUT");
        Assert.Contains("lots/proposal-", put.Body);
        var content = System.Text.Json.JsonDocument.Parse(put.Body).RootElement.GetProperty("content").GetString()!;
        Assert.Contains("Name the containers.", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(content)));
        Assert.Contains(fake.Calls, c => c.Path.EndsWith("/pulls") && c.Body.Contains("Users retried 12 runs"));
    }

    [Fact]
    public async Task No_pull_request_when_the_file_on_main_moved_on()
    {
        var p = Propose(new ProposedChanges(Instructions: "Name the containers."));
        Environment.SetEnvironmentVariable("LOTS_TEST_GIT_TOKEN", "t0ken-for-tests");
        var git = new ProposalGit(new Factory(new FakeGitHub(Yaml.Replace("version: 3", "version: 5"))),
            Microsoft.Extensions.Options.Options.Create(new ProposalGitOptions { Provider = "github", Repo = "acme/config", TokenEnv = "LOTS_TEST_GIT_TOKEN" }));

        await Assert.ThrowsAsync<ProposalException>(() => git.OpenPullRequestAsync(p, default));
    }
}
