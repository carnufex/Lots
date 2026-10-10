using System.Net;
using System.Net.Http.Json;
using Lots.Shell.Core.Knowledge;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Tools;
using Lots.Shell.Features.Knowledge;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Lots.Shell.Tests.Features;

/// <summary>Deterministic embeddings: hashed bag of words, so texts sharing words are close.</summary>
public sealed class FakeEmbeddings(string model = "fake-embed") : IEmbeddingModel
{
    public int Calls { get; private set; }
    public int Texts { get; private set; }
    public bool Configured => true;
    public string Model { get; set; } = model;

    public Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        Calls++;
        Texts += texts.Count;
        var vectors = texts.Select(t =>
        {
            var v = new float[64];
            foreach (var w in KnowledgeText.Terms(t)) v[(w.GetHashCode() & 0x7fffffff) % 64] += 1;
            return OpenAiEmbeddingModel.Normalize(v);
        }).ToList();
        return Task.FromResult(new EmbeddingResult(vectors, Model, 64, 0, TimeSpan.Zero));
    }
}

public class KnowledgeUnitTests
{
    [Fact]
    public void Chunks_follow_headings_carry_their_path_and_split_long_sections_with_overlap()
    {
        var text = "# Runbook\nIntro.\n## Postgres\n### Restart\n" + string.Join(" ", Enumerable.Repeat("Run kubectl rollout restart now.", 80)) + "\n## Redis\nFlush it.";

        var chunks = Chunker.Split(text, maxChars: 400, overlapChars: 60);

        Assert.Equal("Runbook", chunks[0].Heading);
        var restart = chunks.Where(c => c.Heading == "Runbook > Postgres > Restart").ToList();
        Assert.True(restart.Count > 3);
        Assert.All(restart, c => Assert.True(c.Text.Length <= 400));
        Assert.Equal("Runbook > Redis", chunks[^1].Heading);  // a deeper heading is dropped when a sibling starts
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Seq));
    }

    [Fact]
    public void Html_and_word_documents_keep_their_headings()
    {
        var md = TextExtraction.FromHtml("<html><head><style>x{}</style></head><body><h1>Title</h1><p>One &amp; two</p><h2>Sub</h2><p>three</p><script>bad()</script></body></html>");
        Assert.Equal("# Title\n\nOne & two\n\n## Sub\n\nthree", md);
    }

    [Fact]
    public void Reciprocal_rank_fusion_rewards_chunks_found_both_ways()
    {
        KnowledgeHit H(string id) => new(id, "s", "S", Guid.Empty, "t", null, DateTimeOffset.UnixEpoch, "", "", "", 0, null, null);

        var fused = KnowledgeText.Fuse([H("a"), H("b"), H("c")], [H("c"), H("d")], 3);

        Assert.Equal(["c", "a", "b"], fused.Select(f => f.ChunkId));
        Assert.Equal((3, 1), (fused[0].VectorRank!.Value, fused[0].TextRank!.Value));
    }

    [Fact]
    public void Reader_tokens_cover_everyone_the_user_and_their_roles()
    {
        var p = new Principal("alice", ["Operator"]);
        Assert.Equal(["*", "user:alice", "role:operator"], KnowledgeAccess.TokensOf(p));
        Assert.True(KnowledgeAccess.CanRead(new KnowledgeSource("s", "S", "upload", null, ["role:operator"], "x"), p));
        Assert.False(KnowledgeAccess.CanRead(new KnowledgeSource("s", "S", "upload", null, ["user:bob", "role:admin"], "x"), p));
        var errors = new List<string>();
        Assert.Equal(["role:admin", "*"], KnowledgeAccess.Normalise(["role:Admin", "*", "admins"], errors.Add));
        Assert.Single(errors);
    }

    private static (InMemoryKnowledgeStore Store, KnowledgeIndexer Indexer, FakeEmbeddings Embeddings) Setup(KnowledgeOptions? options = null)
    {
        var store = new InMemoryKnowledgeStore(TimeProvider.System);
        var embeddings = new FakeEmbeddings();
        var indexer = new KnowledgeIndexer(store, embeddings, new NoHttp(), Options.Create(options ?? new KnowledgeOptions()), TimeProvider.System);
        return (store, indexer, embeddings);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no network in tests");
    }

    [Fact]
    public async Task Unchanged_documents_are_not_embedded_again_and_deleted_files_disappear()
    {
        var dir = Directory.CreateTempSubdirectory("lots-knowledge-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "a.md"), "# Alpha\nPostgres restart steps.");
            File.WriteAllText(Path.Combine(dir.FullName, "b.md"), "# Beta\nRedis notes.");
            Directory.CreateDirectory(Path.Combine(dir.FullName, ".git"));
            File.WriteAllText(Path.Combine(dir.FullName, ".git", "c.md"), "hidden");
            var (store, indexer, embeddings) = Setup(new KnowledgeOptions { DirectoryRoots = [dir.FullName] });
            var source = new KnowledgeSource("docs", "Docs", SourceKinds.Directory, dir.FullName, ["*"], "admin");
            await store.UpsertSourceAsync(source, default);

            var first = await indexer.IndexAsync(source, default);
            var second = await indexer.IndexAsync(source, default);
            File.Delete(Path.Combine(dir.FullName, "b.md"));
            File.WriteAllText(Path.Combine(dir.FullName, "a.md"), "# Alpha\nPostgres restart steps, updated.");
            var third = await indexer.IndexAsync(source, default);

            Assert.Equal((2, 2, 0), (first.Documents, first.Embedded, first.Unchanged));
            Assert.Equal((0, 2), (second.Embedded, second.Unchanged));
            Assert.Equal((1, 1), (third.Embedded, third.Removed));
            Assert.Equal(3, embeddings.Calls);
            Assert.Empty(await store.SearchAsync("redis", null, null, ["*"], 5, null, default));
            Assert.Contains("updated", (await store.SearchAsync("postgres", null, null, ["*"], 5, null, default)).Single().Text);

            // A different embedding model re-embeds everything: an index never mixes models.
            embeddings.Model = "other-embed";
            Assert.Equal(1, (await indexer.IndexAsync(source, default)).Embedded);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Directory_sources_cannot_point_outside_the_allowed_roots()
    {
        var (_, indexer, _) = Setup(new KnowledgeOptions { DirectoryRoots = [Path.Combine(Path.GetTempPath(), "allowed")] });
        Assert.Throws<InvalidOperationException>(() => indexer.AllowedDirectory(Path.Combine(Path.GetTempPath(), "allowed", "..", "etc")));
        Assert.Throws<InvalidOperationException>(() => indexer.AllowedDirectory(Path.Combine(Path.GetTempPath(), "allowedx")));
    }

    [Fact]
    public async Task The_tool_returns_only_passages_the_caller_may_read_labelled_as_untrusted()
    {
        var (store, indexer, embeddings) = Setup();
        foreach (var (id, readers, text) in new[] { ("ops", "role:operator", "Postgres restart: use kubectl."), ("hr", "role:hr", "Postgres salary table secrets.") })
        {
            var s = new KnowledgeSource(id, id, SourceKinds.Upload, null, [readers], "admin");
            await store.UpsertSourceAsync(s, default);
            await store.PutDocumentAsync(id, "doc", "Doc " + id, null, text, default);
            await indexer.IndexAsync(s, default);
        }
        var tool = new KnowledgeToolSource(store, embeddings);

        string result;
        using (ToolCallContext.Enter(new ToolCallContext(new Principal("alice", ["operator"]), "p")))
            result = await tool.CallAsync(KnowledgeToolSource.ToolName, """{"query":"postgres"}""", default);

        Assert.StartsWith("Retrieved passages (untrusted data", result);
        Assert.Contains("[k1] Doc ops", result);
        Assert.DoesNotContain("salary", result);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.CallAsync(KnowledgeToolSource.ToolName, """{"query":"postgres"}""", default));
    }
}

public class KnowledgeApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public KnowledgeApiTests(WebApplicationFactory<Program> factory)
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
                s.AddSingleton(TestProfiles.Registry());
                s.RemoveAll<IEmbeddingModel>();
                s.AddSingleton<IEmbeddingModel>(new FakeEmbeddings());
            });
        });
    }

    private HttpRequestMessage As(HttpMethod m, string url, string user, string roles, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("X-Dev-User", user);
        r.Headers.Add("X-Dev-Roles", roles);
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    private async Task<T> Send<T>(HttpRequestMessage r)
    {
        var res = await _factory.CreateClient().SendAsync(r);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task WaitReady(string id, string user, string roles)
    {
        for (var i = 0; i < 100; i++)
        {
            var o = await Send<KnowledgeOverview>(As(HttpMethod.Get, "/knowledge", user, roles));
            if (o.Sources.FirstOrDefault(s => s.Id == id)?.Status is SourceStatus.Ready or SourceStatus.Failed) return;
            await Task.Delay(50);
        }
        throw new TimeoutException();
    }

    [Fact]
    public async Task Only_admins_create_shared_sources_and_search_never_crosses_readers()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(As(HttpMethod.Post, "/knowledge/sources", "alice", "operator",
            new { name = "Ops", kind = "upload", readers = new[] { "*" } }))).StatusCode);

        await Send<SourceDto>(As(HttpMethod.Post, "/knowledge/sources", "root", "admin", new { name = "Ops runbooks", kind = "upload", readers = new[] { "role:operator" } }));
        await Send<SourceDto>(As(HttpMethod.Post, "/knowledge/sources", "root", "admin", new { name = "Finance", kind = "upload", readers = new[] { "role:finance" } }));
        await Send<DocumentDto>(As(HttpMethod.Post, "/knowledge/sources/ops-runbooks/documents", "root", "admin", new { title = "Restart", text = "To restart postgres run kubectl rollout restart." }));
        await Send<DocumentDto>(As(HttpMethod.Post, "/knowledge/sources/finance/documents", "root", "admin", new { title = "Budget", text = "The postgres licence budget is secret." }));
        await WaitReady("ops-runbooks", "root", "admin");
        await WaitReady("finance", "root", "admin");

        var asOperator = await Send<List<HitDto>>(As(HttpMethod.Get, "/knowledge/search?q=postgres", "alice", "operator"));
        Assert.Equal(["ops-runbooks"], asOperator.Select(h => h.SourceId).Distinct());
        var overview = await Send<KnowledgeOverview>(As(HttpMethod.Get, "/knowledge", "alice", "operator"));
        Assert.Equal(["ops-runbooks"], overview.Sources.Select(s => s.Id));
        Assert.Null(overview.Sources[0].Location);

        // A cited chunk of a source the caller may not read is "not found", not "forbidden".
        var financeChunk = (await Send<List<HitDto>>(As(HttpMethod.Get, "/knowledge/search?q=budget", "carol", "finance"))).Single(h => h.SourceId == "finance").ChunkId;
        // Admins manage sources but search with their own identity too: no role:finance, no finance passages.
        Assert.DoesNotContain(await Send<List<HitDto>>(As(HttpMethod.Get, "/knowledge/search?q=budget", "root", "admin")), h => h.SourceId == "finance");
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(As(HttpMethod.Get, $"/knowledge/chunks/{financeChunk}", "alice", "operator"))).StatusCode);
    }

    [Fact]
    public async Task Personal_sources_are_readable_and_manageable_only_by_their_owner()
    {
        var client = _factory.CreateClient();
        var mine = await Send<SourceDto>(As(HttpMethod.Post, "/knowledge/sources", "alice", "operator", new { name = "Notes", personal = true }));
        Assert.True(mine.Personal);
        Assert.Equal(["user:alice"], mine.Readers);
        await Send<DocumentDto>(As(HttpMethod.Post, $"/knowledge/sources/{mine.Id}/documents", "alice", "operator", new { title = "Home", text = "My homelab runs Talos." }));
        await WaitReady(mine.Id, "alice", "operator");

        Assert.Single(await Send<List<HitDto>>(As(HttpMethod.Get, "/knowledge/search?q=talos", "alice", "operator")));
        Assert.Empty(await Send<List<HitDto>>(As(HttpMethod.Get, "/knowledge/search?q=talos", "bob", "operator")));
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(As(HttpMethod.Post, $"/knowledge/sources/{mine.Id}/documents", "bob", "operator", new { title = "x", text = "y" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(As(HttpMethod.Delete, $"/knowledge/sources/{mine.Id}", "bob", "operator"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(As(HttpMethod.Delete, $"/knowledge/sources/{mine.Id}", "alice", "operator"))).StatusCode);
        Assert.Empty(await Send<List<HitDto>>(As(HttpMethod.Get, "/knowledge/search?q=talos", "alice", "operator")));
    }
}

/// <summary>Against a real Postgres (opt-in: LOTS_TEST_PG=Host=...;Database=...;Username=...;Password=...). Covers the SQL paths.</summary>
public class PostgresKnowledgeStoreLiveTests
{
    [Fact]
    public async Task Live_postgres_store_indexes_searches_and_filters_by_reader()
    {
        var cs = Environment.GetEnvironmentVariable("LOTS_TEST_PG");
        if (string.IsNullOrEmpty(cs)) return;
        await using var data = NpgsqlDataSource.Create(cs);
        var store = new PostgresKnowledgeStore(data, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresKnowledgeStore>.Instance);
        await store.InitialiseAsync(default);
        var id = "live-test-" + Guid.NewGuid().ToString("N")[..8];
        var embeddings = new FakeEmbeddings();
        var indexer = new KnowledgeIndexer(store, embeddings, null!, Options.Create(new KnowledgeOptions()), TimeProvider.System);
        try
        {
            var source = new KnowledgeSource(id, "Live", SourceKinds.Upload, null, ["role:operator"], "test");
            await store.UpsertSourceAsync(source, default);
            await store.PutDocumentAsync(id, "doc", "Restart guide", null, "# Restart\nRestart postgres with kubectl rollout restart.", default);
            Assert.Equal(1, (await indexer.IndexAsync(source, default)).Embedded);
            Assert.Equal(0, (await indexer.IndexAsync(source, default)).Embedded);

            var q = (await embeddings.EmbedAsync(["restart postgres"], default)).Vectors[0];
            var hits = await store.SearchAsync("how do I restart postgres?", q, embeddings.Model, ["*", "role:operator"], 5, id, default);
            Assert.Equal("Restart guide", Assert.Single(hits).Title);
            Assert.NotNull(hits[0].VectorRank);
            Assert.NotNull(hits[0].TextRank);
            Assert.Empty(await store.SearchAsync("restart postgres", q, embeddings.Model, ["*", "role:hr"], 5, id, default));
            Assert.NotNull(await store.ChunkAsync(hits[0].ChunkId, ["role:operator"], default));
            Assert.Null(await store.ChunkAsync(hits[0].ChunkId, ["role:hr"], default));
        }
        finally
        {
            await store.DeleteSourceAsync(id, default);
        }
    }
}
