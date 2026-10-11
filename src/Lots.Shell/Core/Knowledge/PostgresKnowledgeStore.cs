using System.Collections.Concurrent;
using Npgsql;
using NpgsqlTypes;

namespace Lots.Shell.Core.Knowledge;

/// <summary>
/// Knowledge in the shell's Postgres (ADR 0016). Tables are created here, idempotently, not by EF migrations: a database where the
/// <c>vector</c> extension cannot be installed still starts and searches with <c>real[]</c> and a SQL dot product.
/// With the extension, each embedding dimension gets an HNSW index (cosine on unit vectors).
/// </summary>
public sealed class PostgresKnowledgeStore(NpgsqlDataSource data, TimeProvider clock, ILogger<PostgresKnowledgeStore> logger) : IKnowledgeStore
{
    private const int SchemaVersion = 1;
    private bool _vector;
    private readonly ConcurrentDictionary<int, bool> _vectorIndexes = new();

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS knowledge_schema (version int NOT NULL);
        CREATE TABLE IF NOT EXISTS knowledge_sources (
            id text PRIMARY KEY,
            name text NOT NULL,
            kind text NOT NULL,
            location text,
            readers text[] NOT NULL,
            owner text NOT NULL,
            managed_by text NOT NULL DEFAULT 'api',
            status text NOT NULL,
            error text,
            embed_model text,
            created_at timestamptz NOT NULL,
            indexed_at timestamptz,
            lease_owner text,
            lease_until_ms bigint);
        CREATE INDEX IF NOT EXISTS knowledge_sources_readers ON knowledge_sources USING gin (readers);
        CREATE TABLE IF NOT EXISTS knowledge_documents (
            id uuid PRIMARY KEY,
            source_id text NOT NULL REFERENCES knowledge_sources(id) ON DELETE CASCADE,
            external_id text NOT NULL,
            title text NOT NULL,
            url text,
            content text NOT NULL,
            content_hash text NOT NULL,
            model text,
            updated_at timestamptz NOT NULL,
            UNIQUE (source_id, external_id));
        CREATE TABLE IF NOT EXISTS knowledge_chunks (
            id text PRIMARY KEY,
            document_id uuid NOT NULL REFERENCES knowledge_documents(id) ON DELETE CASCADE,
            source_id text NOT NULL,
            seq int NOT NULL,
            heading text NOT NULL,
            text text NOT NULL,
            model text NOT NULL,
            dims int NOT NULL,
            embedding real[] NOT NULL,
            tsv tsvector GENERATED ALWAYS AS (to_tsvector('simple', heading || ' ' || text)) STORED);
        CREATE INDEX IF NOT EXISTS knowledge_chunks_tsv ON knowledge_chunks USING gin (tsv);
        CREATE INDEX IF NOT EXISTS knowledge_chunks_source ON knowledge_chunks (source_id);
        CREATE OR REPLACE FUNCTION lots_dot(a real[], b real[]) RETURNS double precision
            LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$ SELECT sum(x * y) FROM unnest(a, b) AS t(x, y) $$;
        """;

    public async Task<StoreInfo> InitialiseAsync(CancellationToken ct)
    {
        await using (var cmd = data.CreateCommand(Schema)) await cmd.ExecuteNonQueryAsync(ct);
        // Data class of the source's passages (#89); added after the first release, so altered in place.
        await using (var cmd = data.CreateCommand("ALTER TABLE knowledge_sources ADD COLUMN IF NOT EXISTS sensitivity text NOT NULL DEFAULT 'internal'"))
            await cmd.ExecuteNonQueryAsync(ct);
        await using (var cmd = data.CreateCommand(
            $"DELETE FROM knowledge_schema; INSERT INTO knowledge_schema VALUES ({SchemaVersion});"))
            await cmd.ExecuteNonQueryAsync(ct);

        string? note = null;
        try
        {
            await using (var cmd = data.CreateCommand("CREATE EXTENSION IF NOT EXISTS vector")) await cmd.ExecuteNonQueryAsync(ct);
            await using (var cmd = data.CreateCommand("ALTER TABLE knowledge_chunks ADD COLUMN IF NOT EXISTS vec vector")) await cmd.ExecuteNonQueryAsync(ct);
            _vector = true;
        }
        catch (PostgresException ex)
        {
            _vector = false;
            note = $"pgvector is not installed ({ex.MessageText}); searching with a sequential scan. Run CREATE EXTENSION vector as a superuser for large sources.";
            logger.LogWarning("Knowledge: {Note}", note);
        }
        return new StoreInfo("postgres", _vector, note);
    }

    private static KnowledgeSource ReadSource(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetFieldValue<string[]>(4), r.GetString(5),
        r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10),
        r.GetInt32(12), r.GetInt32(13), r.IsDBNull(9) ? null : r.GetString(9), r.GetString(6), r.GetFieldValue<DateTimeOffset>(11), r.GetString(14));

    private const string SourceSelect = """
        SELECT s.id, s.name, s.kind, s.location, s.readers, s.owner, s.managed_by, s.status, s.error, s.embed_model, s.indexed_at, s.created_at,
               (SELECT count(*)::int FROM knowledge_documents d WHERE d.source_id = s.id),
               (SELECT count(*)::int FROM knowledge_chunks c WHERE c.source_id = s.id),
               s.sensitivity
        FROM knowledge_sources s
        """;

    public async Task<List<KnowledgeSource>> ListSourcesAsync(CancellationToken ct)
    {
        await using var cmd = data.CreateCommand(SourceSelect + " ORDER BY s.name");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<KnowledgeSource>();
        while (await r.ReadAsync(ct)) list.Add(ReadSource(r));
        return list;
    }

    public async Task<KnowledgeSource?> GetSourceAsync(string id, CancellationToken ct)
    {
        await using var cmd = data.CreateCommand(SourceSelect + " WHERE s.id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadSource(r) : null;
    }

    public async Task UpsertSourceAsync(KnowledgeSource s, CancellationToken ct)
    {
        await using var cmd = data.CreateCommand("""
            INSERT INTO knowledge_sources (id, name, kind, location, readers, owner, managed_by, status, created_at, sensitivity)
            VALUES ($1, $2, $3, $4, $5, $6, $7, 'queued', $8, $9)
            ON CONFLICT (id) DO UPDATE SET name = excluded.name, kind = excluded.kind, location = excluded.location,
                readers = excluded.readers, owner = excluded.owner, managed_by = excluded.managed_by, status = 'queued', error = NULL,
                sensitivity = excluded.sensitivity
            """);
        cmd.Parameters.AddWithValue(s.Id);
        cmd.Parameters.AddWithValue(s.Name);
        cmd.Parameters.AddWithValue(s.Kind);
        cmd.Parameters.AddWithValue((object?)s.Location ?? DBNull.Value);
        cmd.Parameters.AddWithValue(s.Readers.ToArray());
        cmd.Parameters.AddWithValue(s.Owner);
        cmd.Parameters.AddWithValue(s.ManagedBy);
        cmd.Parameters.AddWithValue(clock.GetUtcNow());
        cmd.Parameters.AddWithValue(s.Sensitivity);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task DeleteSourceAsync(string id, CancellationToken ct) =>
        Exec("DELETE FROM knowledge_sources WHERE id = $1", ct, id); // documents and chunks cascade

    public Task QueueAsync(string id, CancellationToken ct) =>
        Exec("UPDATE knowledge_sources SET status = 'queued', error = NULL WHERE id = $1", ct, id);

    public Task SetStatusAsync(string id, string status, string? error, string? embedModel, CancellationToken ct) =>
        Exec("""
            UPDATE knowledge_sources SET status = $2, error = $3, embed_model = coalesce($4, embed_model),
                indexed_at = CASE WHEN $2 = 'ready' THEN $5 ELSE indexed_at END
            WHERE id = $1
            """, ct, id, status, error, embedModel, clock.GetUtcNow());

    public async Task<string?> ClaimQueuedAsync(string owner, TimeSpan ttl, CancellationToken ct)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        // One statement: a concurrent worker skips the locked row instead of claiming it twice.
        await using var cmd = data.CreateCommand("""
            UPDATE knowledge_sources SET status = 'indexing', lease_owner = $1, lease_until_ms = $2
            WHERE id = (SELECT id FROM knowledge_sources
                        WHERE status = 'queued' AND (lease_until_ms IS NULL OR lease_until_ms < $3)
                        ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED)
            RETURNING id
            """);
        cmd.Parameters.AddWithValue(owner);
        cmd.Parameters.AddWithValue(now + (long)ttl.TotalMilliseconds);
        cmd.Parameters.AddWithValue(now);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public Task ReleaseAsync(string id, string owner) =>
        Exec("UPDATE knowledge_sources SET lease_owner = NULL, lease_until_ms = NULL WHERE id = $1 AND lease_owner = $2", CancellationToken.None, id, owner);

    public async Task<List<DocumentState>> DocumentsAsync(string sourceId, CancellationToken ct)
    {
        await using var cmd = data.CreateCommand("""
            SELECT d.id, d.external_id, d.content_hash, d.model, (SELECT count(*)::int FROM knowledge_chunks c WHERE c.document_id = d.id)
            FROM knowledge_documents d WHERE d.source_id = $1
            """);
        cmd.Parameters.AddWithValue(sourceId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<DocumentState>();
        while (await r.ReadAsync(ct))
            list.Add(new DocumentState(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4)));
        return list;
    }

    public async Task<List<KnowledgeDocument>> DocumentContentsAsync(string sourceId, CancellationToken ct)
    {
        await using var cmd = data.CreateCommand(
            "SELECT id, source_id, external_id, title, url, content, content_hash, updated_at FROM knowledge_documents WHERE source_id = $1 ORDER BY external_id");
        cmd.Parameters.AddWithValue(sourceId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<KnowledgeDocument>();
        while (await r.ReadAsync(ct))
            list.Add(new KnowledgeDocument(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.GetString(5), r.GetString(6), r.GetFieldValue<DateTimeOffset>(7)));
        return list;
    }

    public async Task<Guid> PutDocumentAsync(string sourceId, string externalId, string title, string? url, string content, CancellationToken ct)
    {
        await using var cmd = data.CreateCommand("""
            INSERT INTO knowledge_documents (id, source_id, external_id, title, url, content, content_hash, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, '', $7)
            ON CONFLICT (source_id, external_id) DO UPDATE SET title = excluded.title, url = excluded.url, content = excluded.content,
                content_hash = '', updated_at = excluded.updated_at
            RETURNING id
            """);
        cmd.Parameters.AddWithValue(Guid.NewGuid());
        cmd.Parameters.AddWithValue(sourceId);
        cmd.Parameters.AddWithValue(externalId);
        cmd.Parameters.AddWithValue(title);
        cmd.Parameters.AddWithValue((object?)url ?? DBNull.Value);
        cmd.Parameters.AddWithValue(content);
        cmd.Parameters.AddWithValue(clock.GetUtcNow());
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task ReplaceDocumentAsync(KnowledgeDocument d, IReadOnlyList<StoredChunk> chunks, string model, CancellationToken ct)
    {
        var dims = chunks.FirstOrDefault()?.Embedding.Length ?? 0;
        if (_vector && dims > 0) await EnsureVectorIndexAsync(dims, ct);

        await using var conn = await data.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Guid id;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO knowledge_documents (id, source_id, external_id, title, url, content, content_hash, model, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            ON CONFLICT (source_id, external_id) DO UPDATE SET title = excluded.title, url = excluded.url, content = excluded.content,
                content_hash = excluded.content_hash, model = excluded.model, updated_at = excluded.updated_at
            RETURNING id
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue(d.Id);
            cmd.Parameters.AddWithValue(d.SourceId);
            cmd.Parameters.AddWithValue(d.ExternalId);
            cmd.Parameters.AddWithValue(d.Title);
            cmd.Parameters.AddWithValue((object?)d.Url ?? DBNull.Value);
            cmd.Parameters.AddWithValue(d.Content);
            cmd.Parameters.AddWithValue(d.ContentHash);
            cmd.Parameters.AddWithValue(model);
            cmd.Parameters.AddWithValue(d.UpdatedAt);
            id = (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await using (var del = new NpgsqlCommand("DELETE FROM knowledge_chunks WHERE document_id = $1", conn, tx))
        {
            del.Parameters.AddWithValue(id);
            await del.ExecuteNonQueryAsync(ct);
        }

        var insert = _vector
            ? "INSERT INTO knowledge_chunks (id, document_id, source_id, seq, heading, text, model, dims, embedding, vec) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$9::vector) ON CONFLICT (id) DO NOTHING"
            : "INSERT INTO knowledge_chunks (id, document_id, source_id, seq, heading, text, model, dims, embedding) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9) ON CONFLICT (id) DO NOTHING";
        var batch = new NpgsqlBatch(conn, tx);
        foreach (var c in chunks)
        {
            var b = new NpgsqlBatchCommand(insert);
            b.Parameters.AddWithValue(c.Id);
            b.Parameters.AddWithValue(id);
            b.Parameters.AddWithValue(d.SourceId);
            b.Parameters.AddWithValue(c.Seq);
            b.Parameters.AddWithValue(c.Heading);
            b.Parameters.AddWithValue(c.Text);
            b.Parameters.AddWithValue(model);
            b.Parameters.AddWithValue(c.Embedding.Length);
            b.Parameters.Add(new NpgsqlParameter { Value = c.Embedding, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Real });
            batch.BatchCommands.Add(b);
        }
        if (batch.BatchCommands.Count > 0) await batch.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task EnsureVectorIndexAsync(int dims, CancellationToken ct)
    {
        if (_vectorIndexes.ContainsKey(dims)) return;
        // pgvector's HNSW needs a fixed dimension: one partial expression index per dimension in use.
        await Exec($"CREATE INDEX IF NOT EXISTS knowledge_chunks_vec_{dims} ON knowledge_chunks USING hnsw ((vec::vector({dims})) vector_cosine_ops) WHERE dims = {dims}", ct);
        _vectorIndexes[dims] = true;
    }

    public Task DeleteDocumentsExceptAsync(string sourceId, IReadOnlyCollection<string> keep, CancellationToken ct) =>
        Exec("DELETE FROM knowledge_documents WHERE source_id = $1 AND NOT (external_id = ANY($2))", ct, sourceId, keep.ToArray());

    public Task DeleteDocumentAsync(string sourceId, Guid documentId, CancellationToken ct) =>
        Exec("DELETE FROM knowledge_documents WHERE source_id = $1 AND id = $2", ct, sourceId, documentId);

    private const string HitSelect = """
        SELECT c.id, c.source_id, s.name, d.id, d.title, d.url, d.updated_at, c.heading, c.text, d.content_hash
        FROM knowledge_chunks c
        JOIN knowledge_documents d ON d.id = c.document_id
        JOIN knowledge_sources s ON s.id = c.source_id
        """;

    /// <summary>Production retrieval: the last stage of the traced search (#158), so the inspector shows exactly this.</summary>
    public async Task<List<KnowledgeHit>> SearchAsync(string query, float[]? vector, string? model, string[] readerTokens, int k, string? sourceId, CancellationToken ct) =>
        [.. (await SearchTracedAsync(query, vector, model, readerTokens, k, sourceId, explain: false, ct)).Final];

    public async Task<RetrievalTrace> SearchTracedAsync(string query, float[]? vector, string? model, string[] readers, int k, string? sourceId, bool explain,
        CancellationToken ct)
    {
        const int Candidates = 40;
        await using var conn = await data.OpenConnectionAsync(ct);

        // Access first: the sources this caller may read. The ranking queries then touch only chunks of those sources, which lets
        // the HNSW index drive the vector query (a join filter would make the planner scan and sort every chunk).
        var readable = new List<string>();
        await using (var cmd = new NpgsqlCommand("SELECT id FROM knowledge_sources WHERE readers && $1 AND ($2::text IS NULL OR id = $2)", conn))
        {
            cmd.Parameters.AddWithValue(readers);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)sourceId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) readable.Add(r.GetString(0));
        }
        var terms = KnowledgeText.Terms(query);
        var tsquery = string.Join(" | ", terms.Select(t => "'" + t.Replace("'", "''") + "'"));
        var hidden = new List<HiddenSource>();
        var otherModel = 0;
        if (explain)
        {
            // Counts only: how many chunks of sources the reader may not read match the words, and which of the readable ones were
            // embedded with another model. Never their content.
            if (terms.Count > 0)
            {
                await using var cmd = new NpgsqlCommand("""
                    SELECT s.id, s.name, count(*) FROM knowledge_chunks c JOIN knowledge_sources s ON s.id = c.source_id, to_tsquery('simple', $1) q
                    WHERE c.tsv @@ q AND NOT (s.readers && $2) AND ($3::text IS NULL OR s.id = $3) GROUP BY s.id, s.name ORDER BY count(*) DESC
                    """, conn);
                cmd.Parameters.AddWithValue(tsquery);
                cmd.Parameters.AddWithValue(readers);
                cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)sourceId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) hidden.Add(new HiddenSource(r.GetString(0), r.GetString(1), (int)r.GetInt64(2)));
            }
            if (readable.Count > 0)
            {
                await using var cmd = new NpgsqlCommand("SELECT count(*) FROM knowledge_chunks WHERE source_id = ANY($1) AND model IS DISTINCT FROM $2", conn);
                cmd.Parameters.AddWithValue(readable.ToArray());
                cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)model ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
                otherModel = (int)(long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
            }
        }
        if (readable.Count == 0) return new RetrievalTrace(model, vector?.Length ?? 0, [], [], [], [], terms, otherModel, hidden);
        var ids = readable.ToArray();

        var byVector = new List<KnowledgeHit>();
        if (vector is { Length: > 0 } && model is not null)
        {
            var dims = vector.Length;
            string sql;
            if (_vector)
            {
                // Iterative scan: when the source filter removes candidates, HNSW keeps searching instead of returning too few.
                await using (var set = new NpgsqlCommand("SET hnsw.iterative_scan = relaxed_order", conn)) await set.ExecuteNonQueryAsync(ct);
                sql = $"""
                    WITH nn AS (SELECT c.id, c.vec::vector({dims}) <=> $1::real[]::vector({dims}) AS dist FROM knowledge_chunks c
                                WHERE c.dims = {dims} AND c.model = $2 AND c.source_id = ANY($3)
                                ORDER BY c.vec::vector({dims}) <=> $1::real[]::vector({dims}) LIMIT {Candidates})
                    {HitSelect.Replace("d.content_hash", "d.content_hash, 1 - nn.dist")} JOIN nn ON nn.id = c.id ORDER BY nn.dist
                    """;
            }
            else
                sql = HitSelect.Replace("d.content_hash", "d.content_hash, lots_dot(c.embedding, $1)")
                      + $" WHERE c.dims = {dims} AND c.model = $2 AND c.source_id = ANY($3) ORDER BY lots_dot(c.embedding, $1) DESC LIMIT {Candidates}";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.Add(new NpgsqlParameter { Value = vector, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Real });
            cmd.Parameters.AddWithValue(model);
            cmd.Parameters.AddWithValue(ids);
            byVector = await ReadHits(cmd, ct);
        }

        var byText = new List<KnowledgeHit>();
        if (terms.Count > 0)
        {
            // Any of the words (OR), ranked by how many and how close: a question's filler words must not make it match nothing.
            await using var cmd = new NpgsqlCommand(HitSelect.Replace("d.content_hash", "d.content_hash, ts_rank_cd(c.tsv, q)") + $"""
                , to_tsquery('simple', $1) q
                WHERE c.tsv @@ q AND c.source_id = ANY($2)
                ORDER BY ts_rank_cd(c.tsv, q) DESC LIMIT {Candidates}
                """, conn);
            cmd.Parameters.AddWithValue(tsquery);
            cmd.Parameters.AddWithValue(ids);
            byText = await ReadHits(cmd, ct);
        }

        static RankedHit Ranked(KnowledgeHit h, int i) => new(h.ChunkId, h.SourceId, h.Title, h.Heading, i + 1, Math.Round(h.Score, 5));
        return new RetrievalTrace(model, vector?.Length ?? 0, [.. readable.Order()], byVector.Select(Ranked).ToList(), byText.Select(Ranked).ToList(),
            KnowledgeText.Fuse(byVector.Select(h => h with { Score = 0 }).ToList(), byText.Select(h => h with { Score = 0 }).ToList(), k), terms, otherModel, hidden);
    }

    public async Task<List<ChunkInfo>> ChunksAsync(Guid documentId, CancellationToken ct)
    {
        await using var cmd = data.CreateCommand("""
            SELECT id, seq, heading, text, model, dims, sqrt(coalesce(lots_dot(embedding, embedding), 0)), 'NaN'::real = ANY(embedding)
            FROM knowledge_chunks WHERE document_id = $1 ORDER BY seq
            """);
        cmd.Parameters.AddWithValue(documentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ChunkInfo>();
        string? previous = null;
        while (await r.ReadAsync(ct))
        {
            var text = r.GetString(3);
            var norm = r.IsDBNull(6) ? 0 : r.GetDouble(6);
            var nan = !r.IsDBNull(7) && r.GetBoolean(7);
            list.Add(new ChunkInfo(r.GetString(0), r.GetInt32(1), r.GetString(2), text, text.Length, KnowledgeInspection.Tokens(text), r.GetString(4), r.GetInt32(5),
                Math.Round(norm, 5), KnowledgeInspection.Flags(text, norm, nan, previous)));
            previous = text;
        }
        return list;
    }

    public async Task<List<ChunkMeta>> ChunkMetaAsync(int limit, CancellationToken ct)
    {
        await using var cmd = data.CreateCommand("""
            SELECT c.id, c.source_id, d.id, d.title, d.updated_at, c.seq, c.model, c.dims, sqrt(coalesce(lots_dot(c.embedding, c.embedding), 0)),
                   'NaN'::real = ANY(c.embedding), length(c.text), md5(c.text)
            FROM knowledge_chunks c JOIN knowledge_documents d ON d.id = c.document_id LIMIT $1
            """);
        cmd.Parameters.AddWithValue(limit);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ChunkMeta>();
        while (await r.ReadAsync(ct))
            list.Add(new ChunkMeta(r.GetString(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), r.GetInt32(5), r.GetString(6),
                r.GetInt32(7), r.IsDBNull(8) ? 0 : r.GetDouble(8), !r.IsDBNull(9) && r.GetBoolean(9), r.GetInt32(10), r.GetString(11)));
        return list;
    }

    public async Task<List<VectorSample>> SampleVectorsAsync(string model, int limit, CancellationToken ct)
    {
        await using var cmd = data.CreateCommand("""
            SELECT c.id, c.source_id, d.title, c.heading, c.embedding FROM knowledge_chunks c JOIN knowledge_documents d ON d.id = c.document_id
            WHERE c.model = $1 ORDER BY c.id LIMIT $2
            """);
        cmd.Parameters.AddWithValue(model);
        cmd.Parameters.AddWithValue(limit);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<VectorSample>();
        while (await r.ReadAsync(ct)) list.Add(new VectorSample(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<float[]>(4)));
        return list;
    }

    public async Task<float[]?> ChunkVectorAsync(string chunkId, CancellationToken ct)
    {
        await using var cmd = data.CreateCommand("SELECT embedding FROM knowledge_chunks WHERE id = $1");
        cmd.Parameters.AddWithValue(chunkId);
        return await cmd.ExecuteScalarAsync(ct) as float[];
    }

    public async Task<KnowledgeHit?> ChunkAsync(string chunkId, string[] readers, CancellationToken ct)
    {
        await using var cmd = data.CreateCommand(HitSelect + " WHERE c.id = $1 AND s.readers && $2");
        cmd.Parameters.AddWithValue(chunkId);
        cmd.Parameters.AddWithValue(readers);
        return (await ReadHits(cmd, ct)).FirstOrDefault();
    }

    private static async Task<List<KnowledgeHit>> ReadHits(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<KnowledgeHit>();
        while (await r.ReadAsync(ct))
            list.Add(new KnowledgeHit(r.GetString(0), r.GetString(1), r.GetString(2), r.GetGuid(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.GetFieldValue<DateTimeOffset>(6), r.GetString(7), r.GetString(8), r.GetString(9),
                r.FieldCount > 10 && !r.IsDBNull(10) ? Convert.ToDouble(r.GetValue(10)) : 0, null, null)); // the ranking's raw score (#158)
        return list;
    }

    private async Task Exec(string sql, CancellationToken ct, params object?[] args)
    {
        await using var cmd = data.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.AddWithValue(a ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
