using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lots.Shell.Core.Knowledge;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Outcomes;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Routing;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Tests.Features;

/// <summary>#150: the shell picks the context. Policy first (never widens access), a cheap score, stickiness, and asking when unsure.</summary>
public class ContextRouterTests
{
    public static Profile Homelab => ProfileParser.Parse("""
        name: homelab
        version: 1
        description: Read-only insight into the homelab's Docker containers.
        routing:
          examples: ["Which containers are unhealthy?", "Show the logs of a container."]
        tools:
          - { name: list_containers, risk: read }
          - { name: get_container_logs, risk: read }
        roles:
          - { name: operator, allow: [read] }
        """);

    public static Profile Cmdb => ProfileParser.Parse("""
        name: cmdb
        version: 1
        description: Search, trace and plan changes in the network CMDB.
        routing:
          examples: ["Which circuits go through node X?", "Trace the path between two sites."]
        tools:
          - { name: search_cis, risk: read }
          - { name: trace_circuit, risk: read }
          - { name: plan_change, risk: write }
        roles:
          - { name: operator, allow: [read] }
          - { name: planner, allow: [read, write], requireApproval: [write] }
        """);

    private static ContextRouter Router(IEmbeddingModel? embeddings = null, params Profile[] profiles) =>
        new(new ProfileRegistry(profiles.Length == 0 ? [Homelab, Cmdb] : profiles), Options.Create(new RoutingOptions()), embeddings);

    private static readonly Principal Operator = new("u", ["operator"]);

    [Theory]
    [InlineData("which containers are unhealthy?", "homelab")]
    [InlineData("which circuits go through node X?", "cmdb")]
    [InlineData("Show me the logs of the postgres container", "homelab")]
    [InlineData("trace the circuit between site A and site B", "cmdb")]
    public async Task Clear_questions_go_to_their_context_without_a_choice(string question, string expected)
    {
        var d = await Router().RouteAsync(Operator, question, null, default);
        Assert.Equal(("auto", expected), (d.Mode, d.Profile));
        Assert.Equal("lexical", d.Method);
    }

    [Fact]
    public async Task A_question_without_a_domain_asks_instead_of_guessing()
    {
        var d = await Router().RouteAsync(Operator, "Hello there!", null, default);
        Assert.Equal("ask", d.Mode);
        Assert.Null(d.Profile);
        Assert.Equal(["cmdb", "homelab"], d.Candidates.Select(c => c.Profile).Order());
    }

    [Fact]
    public async Task A_user_with_one_usable_context_never_sees_a_choice()
    {
        var onlyHomelab = new Principal("u", ["homelab-only"]);
        var homelab = ProfileParser.Parse("name: homelab\nversion: 1\ntools: [{ name: list_containers, risk: read }]\nroles: [{ name: homelab-only, allow: [read] }]\n");
        var d = await Router(null, homelab, Cmdb).RouteAsync(onlyHomelab, "which circuits go through node X?", null, default);
        Assert.Equal(("only", "homelab"), (d.Mode, d.Profile));
    }

    [Fact]
    public async Task Routing_never_scores_a_context_the_user_cannot_use()
    {
        var guest = new Principal("g", ["guest"]);
        var d = await Router().RouteAsync(guest, "which circuits go through node X?", null, default);
        Assert.Equal("none", d.Mode);
        Assert.Empty(d.Candidates);
        Assert.Equal(["homelab"], ContextRouter.Usable(new Principal("u", ["operator"]), new ProfileRegistry([Homelab, ProfileParser.Parse(
            "name: hidden\nversion: 1\ntools: [{ name: t, risk: read }]\nroles: [{ name: admin, allow: [read] }]\n")])).Select(p => p.Name));
    }

    [Fact]
    public async Task A_conversation_keeps_its_context_unless_another_clearly_leads()
    {
        var sticky = await Router().RouteAsync(Operator, "and what about the other one?", "cmdb", default);
        Assert.Equal(("sticky", "cmdb"), (sticky.Mode, sticky.Profile));
        var moved = await Router().RouteAsync(Operator, "which containers are unhealthy?", "cmdb", default);
        Assert.Equal(("auto", "homelab"), (moved.Mode, moved.Profile));
    }

    [Fact]
    public async Task A_context_that_lets_the_user_write_needs_a_clearer_lead()
    {
        var planner = new Principal("p", ["operator", "planner"]);
        var cmdbRo = ContextRouter.ReadOnlyFor(Operator, Cmdb);
        var cmdbRw = ContextRouter.ReadOnlyFor(planner, Cmdb);
        Assert.True(cmdbRo);
        Assert.False(cmdbRw);
        var o = new RoutingOptions();
        Assert.True(o.Lexical.WriteMargin > o.Lexical.AskMargin && o.Embedding.WriteMargin > o.Embedding.AskMargin);
    }

    private sealed class WordEmbeddings(bool fail = false) : IEmbeddingModel
    {
        private static readonly string[] Vocab = ["container", "logs", "unhealthy", "circuit", "node", "trace", "site", "docker", "network"];
        public bool Configured => true;
        public string Model => "words";
        public int Calls { get; private set; }

        public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            Calls++;
            if (fail) throw new HttpRequestException("embedding service down");
            var vectors = texts.Select(t => Vocab.Select(w => t.Contains(w, StringComparison.OrdinalIgnoreCase) ? 1f : 0f).Append(0.05f).ToArray()).ToList();
            return Task.FromResult(new EmbeddingResult(vectors, Model, Vocab.Length + 1, 1, TimeSpan.Zero));
        }
    }

    [Fact]
    public async Task Embeddings_score_when_available_and_context_vectors_are_cached()
    {
        var model = new WordEmbeddings();
        var router = Router(model);
        var d = await router.RouteAsync(Operator, "which circuit passes node 7?", null, default);
        Assert.Equal(("embedding", "auto", "cmdb"), (d.Method, d.Mode, d.Profile));
        var calls = model.Calls;
        await router.RouteAsync(Operator, "docker container logs please", null, default);
        Assert.Equal(calls + 1, model.Calls); // only the question is embedded again
    }

    /// <summary>An embedding that only sees the language: every Swedish text looks alike (what a mostly English model does).</summary>
    private sealed class LanguageOnlyEmbeddings : IEmbeddingModel
    {
        public bool Configured => true;
        public string Model => "language-only";

        public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
            Task.FromResult(new EmbeddingResult(texts.Select(t => t.Any(c => "åäöÅÄÖ".Contains(c)) ? new[] { 1f, 0.1f } : new[] { 0.1f, 1f }).ToList(),
                Model, 2, 1, TimeSpan.Zero));
    }

    [Fact]
    public async Task When_meaning_and_words_disagree_confidently_the_router_asks()
    {
        var meetings = ProfileParser.Parse("""
            name: meetings
            version: 1
            description: Your recorded meetings.
            routing:
              examples: ["Vad bestämdes på mötet?"]
            tools: [{ name: read_meeting, risk: read }]
            roles: [{ name: operator, allow: [read] }]
            """);
        var router = Router(new LanguageOnlyEmbeddings(), Homelab, meetings);
        var d = await router.RouteAsync(Operator, "Vilka containrar kör vi just nu?", null, default);
        Assert.Equal("ask", d.Mode); // the embedding says meetings (Swedish), the words say homelab (containers): never a silent guess
        Assert.Contains("the words to homelab", d.Reason);
    }

    [Fact]
    public async Task A_failing_embedding_model_falls_back_to_words()
    {
        var d = await Router(new WordEmbeddings(fail: true)).RouteAsync(Operator, "which containers are unhealthy?", null, default);
        Assert.Equal(("lexical", "homelab"), (d.Method, d.Profile));
    }

    [Fact]
    public void Profiles_validate_routing_examples()
    {
        Assert.Equal(2, Homelab.Routing.Count);
        var ex = Assert.Throws<ProfileException>(() => ProfileParser.Parse("name: p\nversion: 1\nrouting:\n  examples: [\"" + new string('x', 301) + "\"]\n"));
        Assert.Contains("longer than 300", Assert.Single(ex.Errors));
        Assert.Throws<ProfileException>(() => ProfileParser.Parse("name: p\nversion: 1\nrouting:\n  sample: [a]\n"));
    }

    [Fact]
    public void A_turn_answered_again_in_another_context_is_a_misrouted_signal() =>
        Assert.Equal("misrouted", RunOutcomes.Problem(new RunOutcomeRecord { UserHash = "", Profile = "p", Channel = "web", Status = "Completed", Rerouted = true }));
}

public class RoutingApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class CannedModel : IModelClient
    {
        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, CancellationToken ct) =>
            Task.FromResult(new ModelResponse(new ChatMessage("assistant", "ok"), "stop", new ModelUsage(1, 1), TimeSpan.FromMilliseconds(1)));
    }

    private readonly WebApplicationFactory<Program> _app;

    public RoutingApiTests(WebApplicationFactory<Program> factory)
    {
        var db = Guid.NewGuid().ToString();
        _app = factory.WithWebHostBuilder(b =>
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
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(db));
                s.AddSingleton(new ProfileRegistry([ContextRouterTests.Homelab, ContextRouterTests.Cmdb]));
                s.RemoveAll<IModelClient>();
                s.AddSingleton<IModelClient, CannedModel>();
            });
        });
    }

    private async Task<HttpResponseMessage> Post(string url, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-Dev-User", "claude-test-routing");
        req.Headers.Add("X-Dev-Roles", "operator");
        return await _app.CreateClient().SendAsync(req);
    }

    [Fact]
    public async Task A_run_without_a_profile_is_routed_and_the_decision_recorded()
    {
        var res = await Post("/runs", new { prompt = "which circuits go through node X?" });
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("cmdb", "auto"), (body.GetProperty("profile").GetString(), body.GetProperty("routing").GetString()));

        using var scope = _app.Services.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<LotsDbContext>().Runs.SingleAsync(r => r.Id == body.GetProperty("id").GetGuid());
        Assert.Equal("auto", run.RoutingMode);
        Assert.Contains("\"Method\":\"lexical\"", run.RoutingJson);
    }

    [Fact]
    public async Task When_unsure_the_shell_asks_and_the_choice_is_recorded()
    {
        var ask = await Post("/runs", new { prompt = "Hello there!" });
        Assert.Equal(HttpStatusCode.Conflict, ask.StatusCode);
        var choice = await ask.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, choice.GetProperty("candidates").GetArrayLength());

        var chosen = await (await Post("/runs", new { prompt = "Hello there!", profile = "homelab", routing = "chosen" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("chosen", chosen.GetProperty("routing").GetString());
    }

    [Fact]
    public async Task The_route_endpoint_explains_without_starting_a_run()
    {
        var d = await (await Post("/route", new { prompt = "which containers are unhealthy?" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("homelab", d.GetProperty("profile").GetString());
        using var scope = _app.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<LotsDbContext>().Runs.AnyAsync(r => r.UserId == "claude-test-routing" && r.Prompt == "which containers are unhealthy?"));
    }

    [Fact]
    public async Task Use_the_other_context_instead_answers_again_and_marks_a_correction()
    {
        var first = await (await Post("/runs", new { prompt = "which circuits go through node X?", conversationId = Guid.NewGuid() })).Content.ReadFromJsonAsync<JsonElement>();
        var id = first.GetProperty("id").GetGuid();
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
            var run = await db.Runs.SingleAsync(r => r.Id == id);
            run.Status = RunStatus.Completed; // the worker is not under test here
            await db.SaveChangesAsync();
        }
        var again = await Post($"/runs/{id}/regenerate", new { profile = "homelab" });
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        Assert.Equal("corrected", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("routing").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await Post($"/runs/{id}/regenerate", new { profile = "nope" })).StatusCode);
    }
}
