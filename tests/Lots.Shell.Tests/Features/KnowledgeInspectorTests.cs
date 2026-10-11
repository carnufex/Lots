using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lots.Shell.Core.Knowledge;
using Lots.Shell.Core.Policy;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

/// <summary>#158: the inspector shows the production retrieval stage by stage, flags a broken index and explains empty results.</summary>
public class KnowledgeInspectorTests
{
    public sealed class Words : IEmbeddingModel
    {
        private static readonly string[] Vocab = ["backup", "restore", "postgres", "certificate", "renew", "vpn", "router", "deploy", "secret", "salary"];
        public bool Configured => true;
        public string Model => "words-v1";

        public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
            Task.FromResult(new EmbeddingResult(texts.Select(Vector).ToList(), Model, Vocab.Length + 1, 1, TimeSpan.Zero));

        public static float[] Vector(string text)
        {
            var v = Vocab.Select(w => text.Contains(w, StringComparison.OrdinalIgnoreCase) ? 1f : 0f).Append(0.1f).ToArray();
            var n = (float)Math.Sqrt(v.Sum(x => x * x));
            return v.Select(x => x / n).ToArray();
        }
    }

    public static async Task<InMemoryKnowledgeStore> Seed()
    {
        var store = new InMemoryKnowledgeStore(TimeProvider.System);
        await store.UpsertSourceAsync(new KnowledgeSource("runbooks", "Runbooks", SourceKinds.Upload, null, ["*"], "admin", SourceStatus.Ready), default);
        await store.UpsertSourceAsync(new KnowledgeSource("hr", "HR", SourceKinds.Upload, null, ["role:hr"], "admin", SourceStatus.Ready), default);
        async Task Doc(string source, string title, string model, params string[] texts)
        {
            var doc = new KnowledgeDocument(Guid.NewGuid(), source, title, title, null, string.Join("\n", texts), KnowledgeText.Hash(title), DateTimeOffset.UtcNow);
            await store.ReplaceDocumentAsync(doc, texts.Select((t, i) => new StoredChunk(KnowledgeText.ChunkId(source, title, i, t), i, title, t, Words.Vector(t))).ToList(), model, default);
        }
        await Doc("runbooks", "Backups", "words-v1", "How to restore a postgres backup from last night.", "Backups run every night at two.");
        await Doc("runbooks", "Certificates", "words-v1", "Renew the certificate before it expires.");
        await Doc("runbooks", "VPN (old index)", "old-model", "The vpn router config lives in the network repo.");
        await Doc("hr", "Pay", "words-v1", "Salary reviews happen in March.");
        return store;
    }

    private static readonly string[] Everyone = KnowledgeAccess.TokensOf(new Principal("u", ["operator"]));

    [Theory]
    [InlineData("restore postgres backup")]
    [InlineData("renew certificate")]
    [InlineData("nothing matches this at all")]
    public async Task The_trace_ends_in_exactly_the_production_result(string query)
    {
        var store = await Seed();
        var model = new Words();
        var production = await KnowledgeToolSource.SearchAsync(store, model, query, Everyone, 4, null, default);
        var trace = await KnowledgeToolSource.TraceAsync(store, model, query, Everyone, 4, null, explain: true, default);
        Assert.Equal(production.Select(h => (h.ChunkId, h.Score)), trace.Final.Select(h => (h.ChunkId, h.Score)));
        Assert.Equal(["runbooks"], trace.SearchedSources);
    }

    [Fact]
    public async Task A_document_embedded_with_another_model_is_flagged_and_explained()
    {
        var store = await Seed();
        var sources = await store.ListSourcesAsync(default);
        var docs = new Dictionary<string, List<DocumentState>>();
        foreach (var s in sources) docs[s.Id] = await store.DocumentsAsync(s.Id, default);
        var health = KnowledgeInspection.Check(sources, await store.ChunkMetaAsync(1000, default), docs, "words-v1", 11, DateTimeOffset.UtcNow);
        var mismatch = Assert.Single(health.Issues, i => i.Kind == "model_mismatch");
        Assert.Contains("old-model", mismatch.Detail);
        Assert.Equal(3, health.Sources.Single(s => s.Id == "runbooks").OnCurrentModel);

        var trace = await KnowledgeToolSource.TraceAsync(store, new Words(), "vpn", Everyone, 4, null, explain: true, default);
        Assert.Equal(1, trace.OtherModelChunks);
    }

    [Fact]
    public async Task An_empty_result_says_why_and_never_shows_what_is_hidden()
    {
        var store = await Seed();
        var trace = await KnowledgeToolSource.TraceAsync(store, new Words(), "salary", Everyone, 4, null, explain: true, default);
        var hidden = Assert.Single(trace.Hidden);
        Assert.Equal(("hr", 1), (hidden.SourceId, hidden.Matches));
        Assert.DoesNotContain(trace.Final, h => h.SourceId == "hr");
        var why = KnowledgeInspection.Explain(trace, (await store.ListSourcesAsync(default)).Where(s => s.Id == "runbooks").ToList());
        Assert.Contains("sources you may not read: HR", why);

        var hr = await KnowledgeToolSource.TraceAsync(store, new Words(), "salary", KnowledgeAccess.TokensOf(new Principal("h", ["hr"])), 4, null, true, default);
        Assert.Contains(hr.Final, h => h.SourceId == "hr");
    }

    [Fact]
    public async Task Chunks_show_their_embedding_facts()
    {
        var store = await Seed();
        var doc = (await store.DocumentsAsync("runbooks", default)).Single(d => d.ExternalId == "Backups");
        var chunks = await store.ChunksAsync(doc.Id, default);
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(("words-v1", 11), (c.Model, c.Dims)));
        Assert.All(chunks, c => Assert.InRange(c.Norm, 0.999, 1.001));
        Assert.Contains("zero_vector", KnowledgeInspection.Flags("text", 0, false, null));
        Assert.Contains("oversized", KnowledgeInspection.Flags(new string('x', 5000), 1, false, null));
    }

    [Fact]
    public void Pca_separates_two_clusters_deterministically()
    {
        float[] A(float j) => [1 + j, 0, 0.1f];
        float[] B(float j) => [0, 1 + j, 0.1f];
        var vectors = new List<float[]> { A(0), A(0.1f), A(0.2f), B(0), B(0.1f), B(0.2f) };
        var (axes, mean) = KnowledgeInspection.Pca(vectors);
        var xs = vectors.Select(v => KnowledgeInspection.Project(v, axes, mean).X).ToList();
        Assert.True(xs.Take(3).All(x => Math.Sign(x) == Math.Sign(xs[0])) && xs.Skip(3).All(x => Math.Sign(x) == -Math.Sign(xs[0])));
        Assert.Equal(axes[0], KnowledgeInspection.Pca(vectors).Axes[0]);
    }
}

public class KnowledgeInspectorApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _app;
    private readonly InMemoryKnowledgeStore _store = KnowledgeInspectorTests.Seed().GetAwaiter().GetResult();

    public KnowledgeInspectorApiTests(WebApplicationFactory<Program> factory)
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
                s.AddSingleton(TestProfiles.Registry());
                s.RemoveAll<IKnowledgeStore>();
                s.AddSingleton<IKnowledgeStore>(_store);
                s.RemoveAll<IEmbeddingModel>();
                s.AddSingleton<IEmbeddingModel, KnowledgeInspectorTests.Words>();
            });
        });
    }

    private async Task<HttpResponseMessage> Send(HttpMethod m, string url, string roles, object? body = null)
    {
        using var req = new HttpRequestMessage(m, url) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("X-Dev-User", "claude-test-inspector");
        req.Headers.Add("X-Dev-Roles", roles);
        return await _app.CreateClient().SendAsync(req);
    }

    [Fact]
    public async Task The_playground_reproduces_the_agents_retrieval_and_reads_are_audited()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/knowledge/inspect/search", "operator", new { query = "backup" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "/knowledge/inspect/health", "operator")).StatusCode);

        var res = await (await Send(HttpMethod.Post, "/knowledge/inspect/search", "knowledge-admin", new { query = "restore postgres backup", k = 4 }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var production = await KnowledgeToolSource.SearchAsync(_store, new KnowledgeInspectorTests.Words(), "restore postgres backup",
            KnowledgeAccess.TokensOf(new Principal("claude-test-inspector", ["knowledge-admin"])), 4, null, default);
        Assert.Equal(production.Select(h => h.ChunkId), res.GetProperty("trace").GetProperty("final").EnumerateArray().Select(h => h.GetProperty("chunkId").GetString()));

        using var scope = _app.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<LotsDbContext>().AuditLog.AnyAsync(a => a.Tool == "knowledge.inspect:search" && a.UserId == "claude-test-inspector"));
    }

    [Fact]
    public async Task Why_not_this_chunk_names_the_reason()
    {
        var hrChunk = (await _store.SearchTracedAsync("salary", null, null, ["role:hr"], 4, null, false, default)).Final.Single().ChunkId;
        var res = await (await Send(HttpMethod.Post, "/knowledge/inspect/search", "admin", new { query = "salary", probe = hrChunk })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("may not read", res.GetProperty("probe").GetProperty("verdict").GetString());
        Assert.Contains("HR", res.GetProperty("explanation").GetString());

        var asHr = await (await Send(HttpMethod.Post, "/knowledge/inspect/search", "admin", new { query = "salary", probe = hrChunk, asUser = "hr-person", asRoles = new[] { "hr" } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("found at position 1", asHr.GetProperty("probe").GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Health_and_map_answer_for_inspectors()
    {
        var health = await (await Send(HttpMethod.Get, "/knowledge/inspect/health", "admin")).Content.ReadFromJsonAsync<JsonElement>();
        // The host's indexer may already have re-embedded the old document; the mismatch itself is covered by the unit test.
        Assert.Equal("words-v1", health.GetProperty("currentModel").GetString());
        Assert.Equal(2, health.GetProperty("sources").GetArrayLength());
        var map = await (await Send(HttpMethod.Get, "/knowledge/inspect/map?query=backup", "admin")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.InRange(map.GetProperty("points").GetArrayLength(), 4, 5); // chunks of the current model
        Assert.Equal(JsonValueKind.Object, map.GetProperty("query").ValueKind);
    }
}
