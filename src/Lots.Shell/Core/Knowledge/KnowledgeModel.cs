using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Lots.Shell.Core.Policy;

namespace Lots.Shell.Core.Knowledge;

public static class SourceKinds
{
    /// <summary>Documents are added through the API (or the Knowledge page) and kept in the database.</summary>
    public const string Upload = "upload";
    /// <summary>Files under a directory the shell can read (a mounted volume, a Git checkout synced by a sidecar).</summary>
    public const string Directory = "directory";
    /// <summary>One or more web pages, one URL per line.</summary>
    public const string Url = "url";
    public static readonly string[] All = [Upload, Directory, Url];
}

public static class SourceStatus
{
    public const string Queued = "queued";
    public const string Indexing = "indexing";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

/// <summary>
/// A knowledge source. <see cref="Readers"/> decides who may retrieve from it: <c>*</c> (everyone), <c>role:&lt;name&gt;</c> or
/// <c>user:&lt;id&gt;</c>. A personal source has only its owner as reader. <see cref="ManagedBy"/> = config: defined in configuration
/// (config as code) and read-only through the API.
/// </summary>
public sealed record KnowledgeSource(
    string Id, string Name, string Kind, string? Location, IReadOnlyList<string> Readers, string Owner,
    string Status = SourceStatus.Queued, string? Error = null, DateTimeOffset? IndexedAt = null,
    int Documents = 0, int Chunks = 0, string? EmbedModel = null, string ManagedBy = "api", DateTimeOffset CreatedAt = default,
    string Sensitivity = "internal");

public sealed record KnowledgeDocument(
    Guid Id, string SourceId, string ExternalId, string Title, string? Url, string Content, string ContentHash, DateTimeOffset UpdatedAt);

/// <summary>What the indexer needs to know about a stored document to decide whether to re-embed it.</summary>
public sealed record DocumentState(Guid Id, string ExternalId, string ContentHash, string? Model, int Chunks);

public sealed record StoredChunk(string Id, int Seq, string Heading, string Text, float[] Embedding);

/// <summary>A retrieved chunk with the source data needed to cite it.</summary>
public sealed record KnowledgeHit(
    string ChunkId, string SourceId, string SourceName, Guid DocumentId, string Title, string? Url, DateTimeOffset UpdatedAt, string Heading,
    string Text, string ContentHash, double Score, int? VectorRank, int? TextRank);

public sealed record StoreInfo(string Backend, bool VectorExtension, string? Note);

public static class KnowledgeAccess
{
    /// <summary>Reader tokens a principal matches.</summary>
    public static string[] TokensOf(Principal p) =>
        ["*", "user:" + p.UserId, .. p.Roles.Select(r => "role:" + r.ToLowerInvariant())];

    public static bool CanRead(KnowledgeSource s, Principal p) => s.Readers.Intersect(TokensOf(p), StringComparer.OrdinalIgnoreCase).Any();

    /// <summary>Readers as given, normalised; rejects anything that is not one of the three forms.</summary>
    public static List<string> Normalise(IEnumerable<string> readers, Action<string> error)
    {
        var result = new List<string>();
        foreach (var r in readers.Select(r => r.Trim()).Where(r => r.Length > 0))
        {
            if (r == "*" || r.StartsWith("user:", StringComparison.Ordinal)) result.Add(r);
            else if (r.StartsWith("role:", StringComparison.OrdinalIgnoreCase)) result.Add("role:" + r[5..].ToLowerInvariant());
            else error($"reader '{r}' must be *, role:<name> or user:<id>");
        }
        return result.Distinct().ToList();
    }
}

public static partial class KnowledgeText
{
    /// <summary>Bumped when chunking changes, so every document is re-chunked once.</summary>
    public const string ChunkerVersion = "c1";

    public static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ChunkerVersion + "\n" + content)))[..32].ToLowerInvariant();

    /// <summary>Stable id of a chunk: the same document position and content give the same id across re-indexing.</summary>
    public static string ChunkId(string sourceId, string externalId, int seq, string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceId}\n{externalId}\n{seq}\n{text}")))[..24].ToLowerInvariant();

    /// <summary>Distinct search words of a query (letters and digits, at least two characters).</summary>
    public static List<string> Terms(string query) =>
        Word().Matches(query.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length >= 2).Distinct().Take(32).ToList();

    [GeneratedRegex(@"[\p{L}\p{N}]+")] private static partial Regex Word();

    /// <summary>Reciprocal-rank fusion (k = 60) of the vector and text rankings.</summary>
    public static List<KnowledgeHit> Fuse(IReadOnlyList<KnowledgeHit> byVector, IReadOnlyList<KnowledgeHit> byText, int k)
    {
        const double K = 60;
        var all = new Dictionary<string, (KnowledgeHit Hit, double Score, int? V, int? T)>();
        for (var i = 0; i < byVector.Count; i++)
            all[byVector[i].ChunkId] = (byVector[i], 1 / (K + i + 1), i + 1, null);
        for (var i = 0; i < byText.Count; i++)
        {
            var h = byText[i];
            all[h.ChunkId] = all.TryGetValue(h.ChunkId, out var e)
                ? (e.Hit, e.Score + 1 / (K + i + 1), e.V, i + 1)
                : (h, 1 / (K + i + 1), null, i + 1);
        }
        return all.Values.OrderByDescending(e => e.Score).Take(k)
            .Select(e => e.Hit with { Score = Math.Round(e.Score, 5), VectorRank = e.V, TextRank = e.T }).ToList();
    }
}

/// <summary>Persistence for knowledge (ADR 0016). Search always filters by the caller's reader tokens.</summary>
public interface IKnowledgeStore
{
    Task<StoreInfo> InitialiseAsync(CancellationToken ct);
    Task<List<KnowledgeSource>> ListSourcesAsync(CancellationToken ct);
    Task<KnowledgeSource?> GetSourceAsync(string id, CancellationToken ct);
    /// <summary>Creates or redefines a source (name, kind, location, readers, owner, managedBy) and queues it for indexing.</summary>
    Task UpsertSourceAsync(KnowledgeSource source, CancellationToken ct);
    Task DeleteSourceAsync(string id, CancellationToken ct);
    Task QueueAsync(string id, CancellationToken ct);
    Task SetStatusAsync(string id, string status, string? error, string? embedModel, CancellationToken ct);

    /// <summary>Takes the oldest queued source not leased by another worker; null when there is none.</summary>
    Task<string?> ClaimQueuedAsync(string owner, TimeSpan ttl, CancellationToken ct);
    Task ReleaseAsync(string id, string owner);

    Task<List<DocumentState>> DocumentsAsync(string sourceId, CancellationToken ct);
    Task<List<KnowledgeDocument>> DocumentContentsAsync(string sourceId, CancellationToken ct);
    /// <summary>Stores a document's text without (re)indexing it; used for uploads.</summary>
    Task<Guid> PutDocumentAsync(string sourceId, string externalId, string title, string? url, string content, CancellationToken ct);
    /// <summary>Replaces a document and all its chunks in one transaction.</summary>
    Task ReplaceDocumentAsync(KnowledgeDocument document, IReadOnlyList<StoredChunk> chunks, string model, CancellationToken ct);
    Task DeleteDocumentsExceptAsync(string sourceId, IReadOnlyCollection<string> keepExternalIds, CancellationToken ct);
    Task DeleteDocumentAsync(string sourceId, Guid documentId, CancellationToken ct);

    /// <summary>Production retrieval: the last stage of <see cref="SearchTracedAsync"/>, so the inspector cannot drift from it (#158).</summary>
    Task<List<KnowledgeHit>> SearchAsync(string query, float[]? vector, string? model, string[] readerTokens, int k, string? sourceId, CancellationToken ct);

    /// <summary>
    /// The same retrieval with every stage (#158). <paramref name="explain"/> additionally counts word matches in sources the reader may not
    /// read and chunks embedded with another model (never their content).
    /// </summary>
    Task<RetrievalTrace> SearchTracedAsync(string query, float[]? vector, string? model, string[] readerTokens, int k, string? sourceId, bool explain, CancellationToken ct);
    Task<KnowledgeHit?> ChunkAsync(string chunkId, string[] readerTokens, CancellationToken ct);

    /// <summary>Inspector (#158): every chunk of a document with its embedding facts. Not reader-filtered: callers check the inspector role.</summary>
    Task<List<ChunkInfo>> ChunksAsync(Guid documentId, CancellationToken ct);
    Task<List<ChunkMeta>> ChunkMetaAsync(int limit, CancellationToken ct);
    Task<List<VectorSample>> SampleVectorsAsync(string model, int limit, CancellationToken ct);
    Task<float[]?> ChunkVectorAsync(string chunkId, CancellationToken ct);
}
