namespace Lots.Shell.Core.Knowledge;

/// <summary>
/// Knowledge store for tests and databases that are not relational (the in-memory EF provider). Same semantics as the Postgres
/// store: reader filtering, vector + keyword rankings fused with RRF. Not durable.
/// </summary>
public sealed class InMemoryKnowledgeStore(TimeProvider clock) : IKnowledgeStore
{
    private sealed class Doc
    {
        public required KnowledgeDocument Document { get; set; }
        public List<StoredChunk> Chunks { get; set; } = [];
        public string? Model { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, KnowledgeSource> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Owner, long Until)> _leases = [];
    private readonly List<Doc> _docs = [];

    public Task<StoreInfo> InitialiseAsync(CancellationToken ct) => Task.FromResult(new StoreInfo("memory", false, "In-memory store: not durable."));

    public Task<List<KnowledgeSource>> ListSourcesAsync(CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_sources.Values.Select(WithCounts).OrderBy(s => s.Name).ToList());
    }

    public Task<KnowledgeSource?> GetSourceAsync(string id, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_sources.TryGetValue(id, out var s) ? WithCounts(s) : null);
    }

    private KnowledgeSource WithCounts(KnowledgeSource s)
    {
        var docs = _docs.Where(d => d.Document.SourceId == s.Id).ToList();
        return s with { Documents = docs.Count, Chunks = docs.Sum(d => d.Chunks.Count) };
    }

    public Task UpsertSourceAsync(KnowledgeSource source, CancellationToken ct)
    {
        lock (_gate)
        {
            var created = _sources.TryGetValue(source.Id, out var old) ? old.CreatedAt : clock.GetUtcNow();
            _sources[source.Id] = source with { Status = SourceStatus.Queued, Error = null, CreatedAt = created, IndexedAt = old?.IndexedAt, EmbedModel = old?.EmbedModel };
        }
        return Task.CompletedTask;
    }

    public Task DeleteSourceAsync(string id, CancellationToken ct)
    {
        lock (_gate)
        {
            _sources.Remove(id);
            _docs.RemoveAll(d => d.Document.SourceId == id);
        }
        return Task.CompletedTask;
    }

    public Task QueueAsync(string id, CancellationToken ct) => Update(id, s => s with { Status = SourceStatus.Queued, Error = null });

    public Task SetStatusAsync(string id, string status, string? error, string? embedModel, CancellationToken ct) =>
        Update(id, s => s with
        {
            Status = status, Error = error, EmbedModel = embedModel ?? s.EmbedModel,
            IndexedAt = status == SourceStatus.Ready ? clock.GetUtcNow() : s.IndexedAt,
        });

    private Task Update(string id, Func<KnowledgeSource, KnowledgeSource> change)
    {
        lock (_gate)
            if (_sources.TryGetValue(id, out var s)) _sources[id] = change(s);
        return Task.CompletedTask;
    }

    public Task<string?> ClaimQueuedAsync(string owner, TimeSpan ttl, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var next = _sources.Values
                .Where(s => s.Status == SourceStatus.Queued && (!_leases.TryGetValue(s.Id, out var l) || l.Until < now))
                .OrderBy(s => s.CreatedAt).FirstOrDefault();
            if (next is null) return Task.FromResult<string?>(null);
            _leases[next.Id] = (owner, now + (long)ttl.TotalMilliseconds);
            _sources[next.Id] = next with { Status = SourceStatus.Indexing };
            return Task.FromResult<string?>(next.Id);
        }
    }

    public Task ReleaseAsync(string id, string owner)
    {
        lock (_gate)
            if (_leases.TryGetValue(id, out var l) && l.Owner == owner) _leases.Remove(id);
        return Task.CompletedTask;
    }

    public Task<List<DocumentState>> DocumentsAsync(string sourceId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_docs.Where(d => d.Document.SourceId == sourceId)
                .Select(d => new DocumentState(d.Document.Id, d.Document.ExternalId, d.Document.ContentHash, d.Model, d.Chunks.Count)).ToList());
    }

    public Task<List<KnowledgeDocument>> DocumentContentsAsync(string sourceId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_docs.Where(d => d.Document.SourceId == sourceId).Select(d => d.Document).ToList());
    }

    public Task<Guid> PutDocumentAsync(string sourceId, string externalId, string title, string? url, string content, CancellationToken ct)
    {
        lock (_gate)
        {
            var existing = _docs.FirstOrDefault(d => d.Document.SourceId == sourceId && d.Document.ExternalId == externalId);
            var id = existing?.Document.Id ?? Guid.NewGuid();
            // The hash is only set when indexed, so a changed upload is always re-chunked.
            var doc = new KnowledgeDocument(id, sourceId, externalId, title, url, content, "", clock.GetUtcNow());
            if (existing is null) _docs.Add(new Doc { Document = doc });
            else existing.Document = doc;
            return Task.FromResult(id);
        }
    }

    public Task ReplaceDocumentAsync(KnowledgeDocument document, IReadOnlyList<StoredChunk> chunks, string model, CancellationToken ct)
    {
        lock (_gate)
        {
            _docs.RemoveAll(d => d.Document.SourceId == document.SourceId && d.Document.ExternalId == document.ExternalId);
            _docs.Add(new Doc { Document = document, Chunks = chunks.ToList(), Model = model });
        }
        return Task.CompletedTask;
    }

    public Task DeleteDocumentsExceptAsync(string sourceId, IReadOnlyCollection<string> keepExternalIds, CancellationToken ct)
    {
        lock (_gate) _docs.RemoveAll(d => d.Document.SourceId == sourceId && !keepExternalIds.Contains(d.Document.ExternalId));
        return Task.CompletedTask;
    }

    public Task DeleteDocumentAsync(string sourceId, Guid documentId, CancellationToken ct)
    {
        lock (_gate) _docs.RemoveAll(d => d.Document.SourceId == sourceId && d.Document.Id == documentId);
        return Task.CompletedTask;
    }

    /// <summary>Production retrieval: the last stage of the traced search (#158), so the inspector shows exactly this.</summary>
    public async Task<List<KnowledgeHit>> SearchAsync(string query, float[]? vector, string? model, string[] readerTokens, int k, string? sourceId, CancellationToken ct) =>
        [.. (await SearchTracedAsync(query, vector, model, readerTokens, k, sourceId, explain: false, ct)).Final];

    public Task<RetrievalTrace> SearchTracedAsync(string query, float[]? vector, string? model, string[] readerTokens, int k, string? sourceId, bool explain,
        CancellationToken ct)
    {
        lock (_gate)
        {
            var readable = _sources.Values
                .Where(s => s.Readers.Intersect(readerTokens, StringComparer.OrdinalIgnoreCase).Any() && (sourceId is null || s.Id == sourceId))
                .ToDictionary(s => s.Id);
            var candidates = _docs.Where(d => readable.ContainsKey(d.Document.SourceId))
                .SelectMany(d => d.Chunks.Select(c => (Doc: d, Chunk: c))).ToList();

            var vectorRanked = vector is null ? [] : candidates
                .Where(x => x.Doc.Model == model && x.Chunk.Embedding.Length == vector.Length)
                .Select(x => (x, Score: Dot(x.Chunk.Embedding, vector))).OrderByDescending(x => x.Score).Take(40).ToList();
            var byVector = vectorRanked.Select(x => Hit(readable, x.x.Doc, x.x.Chunk, x.Score)).ToList();

            var terms = KnowledgeText.Terms(query);
            int Matches((Doc Doc, StoredChunk Chunk) x) => terms.Count(t => (x.Chunk.Heading + " " + x.Chunk.Text).Contains(t, StringComparison.OrdinalIgnoreCase));
            var textRanked = candidates.Select(x => (x, Score: Matches(x))).Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(40).ToList();
            var byText = textRanked.Select(x => Hit(readable, x.x.Doc, x.x.Chunk, x.Score)).ToList();

            var hidden = new List<HiddenSource>();
            var otherModel = 0;
            if (explain)
            {
                foreach (var g in _docs.Where(d => !readable.ContainsKey(d.Document.SourceId) && (sourceId is null || d.Document.SourceId == sourceId))
                             .SelectMany(d => d.Chunks.Select(c => (Doc: d, Chunk: c))).Where(x => terms.Count > 0 && Matches(x) > 0)
                             .GroupBy(x => x.Doc.Document.SourceId))
                    hidden.Add(new HiddenSource(g.Key, _sources.TryGetValue(g.Key, out var hs) ? hs.Name : g.Key, g.Count()));
                otherModel = candidates.Count(x => x.Doc.Model != model);
            }
            static RankedHit Ranked(KnowledgeHit h, int i, double raw) => new(h.ChunkId, h.SourceId, h.Title, h.Heading, i + 1, Math.Round(raw, 5));
            return Task.FromResult(new RetrievalTrace(model, vector?.Length ?? 0, [.. readable.Keys.Order()],
                byVector.Select((h, i) => Ranked(h, i, vectorRanked[i].Score)).ToList(), byText.Select((h, i) => Ranked(h, i, textRanked[i].Score)).ToList(),
                KnowledgeText.Fuse(byVector, byText, k), terms, otherModel, hidden));
        }
    }

    public Task<List<ChunkInfo>> ChunksAsync(Guid documentId, CancellationToken ct)
    {
        lock (_gate)
        {
            var doc = _docs.FirstOrDefault(d => d.Document.Id == documentId);
            if (doc is null) return Task.FromResult(new List<ChunkInfo>());
            var ordered = doc.Chunks.OrderBy(c => c.Seq).ToList();
            return Task.FromResult(ordered.Select((c, i) =>
            {
                var norm = KnowledgeInspection.Norm(c.Embedding);
                return new ChunkInfo(c.Id, c.Seq, c.Heading, c.Text, c.Text.Length, KnowledgeInspection.Tokens(c.Text), doc.Model ?? "", c.Embedding.Length,
                    Math.Round(norm, 5), KnowledgeInspection.Flags(c.Text, norm, c.Embedding.Any(float.IsNaN), i > 0 ? ordered[i - 1].Text : null));
            }).ToList());
        }
    }

    public Task<List<ChunkMeta>> ChunkMetaAsync(int limit, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_docs.SelectMany(d => d.Chunks.Select(c => new ChunkMeta(c.Id, d.Document.SourceId, d.Document.Id, d.Document.Title,
                d.Document.UpdatedAt, c.Seq, d.Model ?? "", c.Embedding.Length, KnowledgeInspection.Norm(c.Embedding), c.Embedding.Any(float.IsNaN),
                c.Text.Length, KnowledgeText.Hash(c.Text)))).Take(limit).ToList());
    }

    public Task<List<VectorSample>> SampleVectorsAsync(string model, int limit, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_docs.Where(d => d.Model == model).SelectMany(d => d.Chunks.Select(c =>
                new VectorSample(c.Id, d.Document.SourceId, d.Document.Title, c.Heading, c.Embedding))).Take(limit).ToList());
    }

    public Task<float[]?> ChunkVectorAsync(string chunkId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_docs.SelectMany(d => d.Chunks).FirstOrDefault(c => c.Id == chunkId)?.Embedding);
    }

    public Task<KnowledgeHit?> ChunkAsync(string chunkId, string[] readerTokens, CancellationToken ct)
    {
        lock (_gate)
        {
            foreach (var d in _docs)
                if (d.Chunks.FirstOrDefault(c => c.Id == chunkId) is { } c
                    && _sources.TryGetValue(d.Document.SourceId, out var s)
                    && s.Readers.Intersect(readerTokens, StringComparer.OrdinalIgnoreCase).Any())
                    return Task.FromResult<KnowledgeHit?>(Hit(new() { [s.Id] = s }, d, c, 0));
            return Task.FromResult<KnowledgeHit?>(null);
        }
    }

    private static KnowledgeHit Hit(Dictionary<string, KnowledgeSource> sources, Doc d, StoredChunk c, double score) =>
        new(c.Id, d.Document.SourceId, sources[d.Document.SourceId].Name, d.Document.Id, d.Document.Title, d.Document.Url, d.Document.UpdatedAt,
            c.Heading, c.Text, d.Document.ContentHash, score, null, null);

    private static double Dot(float[] a, float[] b)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}
