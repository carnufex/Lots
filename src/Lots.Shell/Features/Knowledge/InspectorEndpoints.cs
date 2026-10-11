using System.Collections.Concurrent;
using System.Text.Json;
using FastEndpoints;
using Lots.Shell.Core.Knowledge;
using Lots.Shell.Core.Policy;
using Lots.Shell.Persistence;

namespace Lots.Shell.Features.Knowledge;

/// <summary>
/// The knowledge inspector (#158): what was ingested, how it was chunked and embedded, and why a search finds (or misses) a passage.
/// For <c>Knowledge:InspectRoles</c> (default admin, knowledge-admin). Every read is an audit row. The playground runs the production
/// retrieval (<see cref="KnowledgeToolSource.TraceAsync"/>) and shows its stages; it is not a second implementation.
/// </summary>
public static class KnowledgeInspector
{
    public static bool Allowed(Principal me, IConfiguration config)
    {
        var roles = config.GetSection("Knowledge:InspectRoles").Get<string[]>() is { Length: > 0 } r ? r : ["admin", "knowledge-admin"];
        return me.Roles.Any(x => roles.Contains(x, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Inspection reads are audited like tool calls: who looked at what (the arguments, never the content).</summary>
    public static async Task AuditAsync(LotsDbContext db, Principal me, string action, object args, TimeProvider clock, CancellationToken ct)
    {
        db.AuditLog.Add(new AuditRecord
        {
            Id = Guid.NewGuid(), At = clock.GetUtcNow(), UserId = me.UserId, Roles = string.Join(',', me.Roles), Profile = "knowledge", RunId = Guid.Empty,
            Tool = "knowledge.inspect:" + action, ArgumentsJson = JsonSerializer.Serialize(args), Decision = AuditDecision.Allowed,
            Reason = "knowledge inspector (#158)", ResultStatus = "ok",
        });
        await db.SaveChangesAsync(ct);
    }
}

/// <param name="AsUser">Search as this user (with <paramref name="AsRoles"/>) to see what they would get; default: the caller.</param>
/// <param name="Probe">"Why not this chunk?": a chunk id to score against the query.</param>
public sealed record InspectSearchRequest(string Query, int? K = null, string? Source = null, string? AsUser = null, List<string>? AsRoles = null, string? Probe = null);

public sealed record ProbeResult(string ChunkId, bool Readable, double? Similarity, int? VectorRank, int? TextRank, int TermMatches, int? FinalRank,
    IReadOnlyList<string> RankedAbove, string Verdict);

public sealed record InspectSearchResponse(RetrievalTrace Trace, string? Explanation, IReadOnlyList<string> Readers, ProbeResult? Probe);

public sealed class InspectSearchEndpoint(IKnowledgeStore store, IEmbeddingModel embeddings, ICurrentPrincipal who, IConfiguration config, LotsDbContext db,
    TimeProvider clock) : Endpoint<InspectSearchRequest, InspectSearchResponse>
{
    public override void Configure() => Post("/knowledge/inspect/search");

    public override async Task HandleAsync(InspectSearchRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        if (!KnowledgeInspector.Allowed(me, config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        if (string.IsNullOrWhiteSpace(req.Query))
        {
            AddError(x => x.Query, "query is required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        var reader = req.AsUser is { Length: > 0 } u ? new Principal(u, req.AsRoles ?? []) : me;
        var readers = KnowledgeAccess.TokensOf(reader);
        var k = Math.Clamp(req.K ?? 4, 1, 20);
        var trace = await KnowledgeToolSource.TraceAsync(store, embeddings, req.Query, readers, k, req.Source, explain: true, ct);
        var sources = await store.ListSourcesAsync(ct);
        var readable = sources.Where(s => trace.SearchedSources.Contains(s.Id)).ToList();

        ProbeResult? probe = null;
        if (req.Probe is { Length: > 0 } chunkId)
        {
            var hit = await store.ChunkAsync(chunkId, readers, ct);
            var vector = await store.ChunkVectorAsync(chunkId, ct);
            double? similarity = null;
            if (vector is not null && embeddings.Configured)
            {
                var q = (await embeddings.EmbedAsync([req.Query], ct)).Vectors[0];
                similarity = q.Length == vector.Length ? Math.Round(Core.Routing.ContextRouter.Cosine(q, vector), 5) : null;
            }
            var vRank = trace.ByVector.FirstOrDefault(x => x.ChunkId == chunkId)?.Rank;
            var tRank = trace.ByText.FirstOrDefault(x => x.ChunkId == chunkId)?.Rank;
            var fRank = trace.Final.Select((h, i) => (h, i)).FirstOrDefault(x => x.h.ChunkId == chunkId) is { h: not null } f ? f.i + 1 : (int?)null;
            var terms = hit is null ? 0 : trace.Terms.Count(t => (hit.Heading + " " + hit.Text).Contains(t, StringComparison.OrdinalIgnoreCase));
            var verdict = hit is null ? "the reader may not read this chunk's source (or it does not exist)"
                : fRank is { } r ? $"found at position {r}"
                : vector is not null && similarity is null ? "the chunk was embedded with another model or dimension than the query: re-index its source"
                : vRank is null && tRank is null ? "neither ranking reached it: it is not among the 40 nearest vectors and shares no word with the query"
                : $"it ranked {(vRank is { } v ? $"#{v} by vector" : "outside the vector candidates")} and {(tRank is { } t ? $"#{t} by words" : "not by words")}, below the top {k} after fusion";
            probe = new ProbeResult(chunkId, hit is not null, similarity, vRank, tRank, terms, fRank,
                fRank is null ? trace.Final.Select(h => h.ChunkId).ToList() : trace.Final.Take(fRank.Value - 1).Select(h => h.ChunkId).ToList(), verdict);
        }

        await KnowledgeInspector.AuditAsync(db, me, "search", new { req.Query, k, req.Source, req.AsUser, req.AsRoles, req.Probe }, clock, ct);
        await Send.OkAsync(new InspectSearchResponse(trace, KnowledgeInspection.Explain(trace, readable), readers, probe), ct);
    }
}

public sealed record DocumentChunksRequest(Guid DocumentId);

public sealed class InspectChunksEndpoint(IKnowledgeStore store, ICurrentPrincipal who, IConfiguration config, LotsDbContext db, TimeProvider clock)
    : Endpoint<DocumentChunksRequest, List<ChunkInfo>>
{
    public override void Configure() => Get("/knowledge/inspect/documents/{DocumentId}/chunks");

    public override async Task HandleAsync(DocumentChunksRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        if (!KnowledgeInspector.Allowed(me, config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        await KnowledgeInspector.AuditAsync(db, me, "chunks", new { req.DocumentId }, clock, ct);
        await Send.OkAsync(await store.ChunksAsync(req.DocumentId, ct), ct);
    }
}

public sealed class InspectHealthEndpoint(IKnowledgeStore store, IEmbeddingModel embeddings, ICurrentPrincipal who, IConfiguration config, LotsDbContext db,
    TimeProvider clock) : EndpointWithoutRequest<KnowledgeHealth>
{
    public override void Configure() => Get("/knowledge/inspect/health");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        if (!KnowledgeInspector.Allowed(me, config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        int? dims = null;
        if (embeddings.Configured)
            try { dims = (await embeddings.EmbedAsync(["dimension probe"], ct)).Dimensions; }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* health still reports the index */ }
        var sources = await store.ListSourcesAsync(ct);
        var docs = new Dictionary<string, List<DocumentState>>();
        foreach (var s in sources) docs[s.Id] = await store.DocumentsAsync(s.Id, ct);
        await KnowledgeInspector.AuditAsync(db, me, "health", new { }, clock, ct);
        await Send.OkAsync(KnowledgeInspection.Check(sources, await store.ChunkMetaAsync(50_000, ct), docs,
            embeddings.Configured ? embeddings.Model : null, dims, clock.GetUtcNow()), ct);
    }
}

public sealed record MapRequest(string? Query = null, int? Limit = null);

public sealed record MapPoint(string ChunkId, string SourceId, string Title, string Heading, double X, double Y);

public sealed record EmbeddingMap(string Model, int Sampled, IReadOnlyList<MapPoint> Points, MapPoint? Query, IReadOnlyList<string> Neighbours);

/// <summary>A 2D projection (PCA) of a sample of chunk vectors, with the query and its nearest chunks. Cached ten minutes per model and sample.</summary>
public sealed class InspectMapEndpoint(IKnowledgeStore store, IEmbeddingModel embeddings, ICurrentPrincipal who, IConfiguration config, LotsDbContext db,
    TimeProvider clock) : Endpoint<MapRequest, EmbeddingMap>
{
    private static readonly ConcurrentDictionary<(string, int), (DateTimeOffset At, List<VectorSample> Sample, double[][] Axes, double[] Mean)> Cache = new();

    public override void Configure() => Get("/knowledge/inspect/map");

    public override async Task HandleAsync(MapRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        if (!KnowledgeInspector.Allowed(me, config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        if (!embeddings.Configured)
        {
            AddError("No embedding model is configured.");
            await Send.ErrorsAsync(503, ct);
            return;
        }
        var limit = Math.Clamp(req.Limit ?? 400, 10, 2_000);
        var key = (embeddings.Model, limit);
        if (!Cache.TryGetValue(key, out var e) || clock.GetUtcNow() - e.At > TimeSpan.FromMinutes(10))
        {
            var sample = await store.SampleVectorsAsync(embeddings.Model, limit, ct);
            var dims = sample.GroupBy(s => s.Vector.Length).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? 0;
            sample = sample.Where(s => s.Vector.Length == dims).ToList();
            var (axes, mean) = sample.Count >= 3 ? KnowledgeInspection.Pca(sample.Select(s => s.Vector).ToList()) : ([], []);
            Cache[key] = e = (clock.GetUtcNow(), sample, axes, mean);
        }
        var points = e.Axes.Length == 0 ? [] : e.Sample.Select(s =>
        {
            var (x, y) = KnowledgeInspection.Project(s.Vector, e.Axes, e.Mean);
            return new MapPoint(s.ChunkId, s.SourceId, s.Title, s.Heading, Math.Round(x, 4), Math.Round(y, 4));
        }).ToList();
        MapPoint? query = null;
        var neighbours = new List<string>();
        if (req.Query is { Length: > 0 } q && e.Axes.Length > 0)
        {
            var v = (await embeddings.EmbedAsync([q], ct)).Vectors[0];
            if (v.Length == e.Mean.Length)
            {
                var (x, y) = KnowledgeInspection.Project(v, e.Axes, e.Mean);
                query = new MapPoint("query", "", q, "", Math.Round(x, 4), Math.Round(y, 4));
                neighbours = e.Sample.OrderByDescending(s => Core.Routing.ContextRouter.Cosine(v, s.Vector)).Take(5).Select(s => s.ChunkId).ToList();
            }
        }
        await KnowledgeInspector.AuditAsync(db, me, "map", new { req.Query, limit }, clock, ct);
        await Send.OkAsync(new EmbeddingMap(embeddings.Model, e.Sample.Count, points, query, neighbours), ct);
    }
}
