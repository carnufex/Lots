using System.Net;
using System.Net.Http.Json;
using Lots.Shell.Core.Knowledge;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Tools;
using Lots.Shell.Features.Knowledge;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

public class ConflictVotingTests
{
    private static ConflictVoteRecord V(string option, string? hash, string roles = "operator", int daysAgo = 10) =>
        new() { Id = Guid.NewGuid(), UserId = Guid.NewGuid().ToString(), Option = option, ContentHash = hash, Roles = roles, At = DateTimeOffset.UtcNow.AddDays(-daysAgo) };

    [Fact]
    public void Votes_for_a_changed_document_do_not_count_and_a_clear_majority_becomes_a_suggestion()
    {
        var hashes = new Dictionary<string, string?> { ["a"] = "h1", ["b"] = "h2" };

        var tally = ConflictVoting.Tally([V("a", "h1"), V("a", "h1"), V("a", "h1"), V("b", "old-hash"), V("b", "h2")], hashes, new Dictionary<string, double>(), DateTimeOffset.UtcNow);

        Assert.Equal((4, 1), (tally.Votes, tally.StaleVotes));
        Assert.Equal("a", tally.Suggestion);
        Assert.False(tally.Swing);
    }

    [Fact]
    public void Role_weights_and_age_change_the_weight_and_neither_never_becomes_a_suggestion()
    {
        var hashes = new Dictionary<string, string?> { ["a"] = "h", ["b"] = "h" };
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["admin"] = 3 };

        var tally = ConflictVoting.Tally([V("a", "h", "admin"), V("b", "h", daysAgo: 200), V("b", "h")], hashes, weights, DateTimeOffset.UtcNow);
        Assert.Equal(3, tally.Options.Single(o => o.Option == "a").Weight);
        Assert.Equal(1.5, tally.Options.Single(o => o.Option == "b").Weight);
        Assert.Null(tally.Suggestion); // 3 of 4.5 = 67 %: below the 70 % bar
    }

    [Fact]
    public void A_burst_of_recent_votes_is_flagged()
    {
        var hashes = new Dictionary<string, string?> { ["a"] = "h" };
        var votes = Enumerable.Range(0, 6).Select(_ => V("a", "h", daysAgo: 0)).ToList();

        Assert.True(ConflictVoting.Tally(votes, hashes, new Dictionary<string, double>(), DateTimeOffset.UtcNow).Swing);
    }

    [Fact]
    public void The_judge_verdict_is_parsed_from_surrounding_text()
    {
        var v = ConflictDetector.Parse("Sure: {\"conflict\": true, \"options\": [1, 3], \"summary\": \"port 5432 vs 5433\"} done");
        Assert.Equal((true, "port 5432 vs 5433"), (v!.Conflict, v.Summary));
        Assert.Equal([1, 3], v.Options);
        Assert.Null(ConflictDetector.Parse("no json"));
    }
}

public class ConflictApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>The conflict judge: always says passages 1 and 2 disagree.</summary>
    private sealed class Judge : IModelClient
    {
        public int Calls { get; private set; }

        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new ModelResponse(new ChatMessage("assistant", """{"conflict": true, "options": [1, 2], "summary": "different ports"}"""),
                "stop", new ModelUsage(1, 1), TimeSpan.Zero));
        }
    }

    private readonly Judge _judge = new();
    private readonly WebApplicationFactory<Program> _factory;

    public ConflictApiTests(WebApplicationFactory<Program> factory)
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
                s.AddSingleton(new ProfileRegistry([
                    new Profile("on", 1, "", "", [], [new ProfileTool("search_knowledge", ToolRisk.Read)], [new ProfileRole("operator", [ToolRisk.Read], [])], DetectConflicts: true),
                    new Profile("off", 1, "", "", [], [new ProfileTool("search_knowledge", ToolRisk.Read)], [new ProfileRole("operator", [ToolRisk.Read], [])]),
                ]));
                s.RemoveAll<IEmbeddingModel>();
                s.AddSingleton<IEmbeddingModel>(new FakeEmbeddings());
                s.RemoveAll<IModelClient>();
                s.AddSingleton<IModelClient>(_judge);
            });
        });
    }

    private async Task SeedAsync()
    {
        var store = _factory.Services.GetRequiredService<IKnowledgeStore>();
        using var scope = _factory.Services.CreateScope();
        var indexer = scope.ServiceProvider.GetRequiredService<KnowledgeIndexer>();
        foreach (var (id, readers, text) in new[]
                 {
                     ("wiki", "role:operator", "Postgres listens on port 5432 in production."),
                     ("runbook", "role:dba", "Postgres listens on port 5433 in production."),
                 })
        {
            var s = new KnowledgeSource(id, id, SourceKinds.Upload, null, [readers, "role:admin"], "admin");
            await store.UpsertSourceAsync(s, default);
            await store.PutDocumentAsync(id, "doc", "Postgres " + id, null, text, default);
            await indexer.IndexAsync(s, default);
            await store.SetStatusAsync(id, SourceStatus.Ready, null, "fake-embed", default);
        }
    }

    private async Task<string> SearchAsync(string profile, params string[] roles)
    {
        var tool = _factory.Services.GetServices<IToolSource>().OfType<KnowledgeToolSource>().Single();
        using var _ = ToolCallContext.Enter(new ToolCallContext(new Principal("u", roles), profile));
        return await tool.CallAsync(KnowledgeToolSource.ToolName, """{"query":"postgres port production"}""", default);
    }

    private HttpRequestMessage As(HttpMethod m, string url, string user, string roles, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("X-Dev-User", user);
        r.Headers.Add("X-Dev-Roles", roles);
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    [Fact]
    public async Task Disagreeing_sources_are_recorded_once_voted_on_within_access_and_only_admins_resolve()
    {
        await SeedAsync();

        Assert.DoesNotContain("Conflict:", await SearchAsync("off", "operator", "dba")); // off unless the profile enables it
        Assert.Equal(0, _judge.Calls);
        var result = await SearchAsync("on", "operator", "dba");
        Assert.Contains("NOTE: the passages [k1] and [k2] disagree (different ports)", result);
        var id = Guid.Parse(System.Text.RegularExpressions.Regex.Match(result, @"Conflict: ([0-9a-f-]{36})").Groups[1].Value);
        Assert.Contains($"Conflict: {id}", await SearchAsync("on", "operator", "dba")); // same disagreement, same record

        var client = _factory.CreateClient();
        // Someone who can read only one side sees no conflict at all.
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(As(HttpMethod.Get, $"/knowledge/conflicts/{id}", "olle", "operator"))).StatusCode);

        var view = (await (await client.SendAsync(As(HttpMethod.Get, $"/knowledge/conflicts/{id}", "dana", "operator,dba"))).Content.ReadFromJsonAsync<ConflictDto>())!;
        Assert.Equal(2, view.Options.Count);
        var wiki = view.Options.Single(o => o.Title == "Postgres wiki").ChunkId;

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(As(HttpMethod.Post, $"/knowledge/conflicts/{id}/vote", "dana", "operator,dba", new { option = "made-up" }))).StatusCode);
        await client.SendAsync(As(HttpMethod.Post, $"/knowledge/conflicts/{id}/vote", "dana", "operator,dba", new { option = "neither" }));
        var voted = (await (await client.SendAsync(As(HttpMethod.Post, $"/knowledge/conflicts/{id}/vote", "dana", "operator,dba", new { option = wiki }))).Content.ReadFromJsonAsync<ConflictDto>())!;
        Assert.Equal(wiki, voted.MyVote);
        Assert.Equal(1, voted.Tally.Votes); // the second vote replaced the first

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(As(HttpMethod.Get, "/knowledge/conflicts", "dana", "operator,dba"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(As(HttpMethod.Post, $"/knowledge/conflicts/{id}/resolve", "dana", "operator,dba", new { option = wiki }))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(As(HttpMethod.Post, $"/knowledge/conflicts/{id}/resolve", "root", "admin", new { option = wiki }))).StatusCode);
        var list = (await (await client.SendAsync(As(HttpMethod.Get, "/knowledge/conflicts", "root", "admin"))).Content.ReadFromJsonAsync<List<ConflictDto>>())!;
        Assert.Equal(("resolved", wiki, "root"), (list.Single().Status, list.Single().ResolvedOption, list.Single().ResolvedBy));
    }
}
