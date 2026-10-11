namespace Lots.Shell.Core.Knowledge;

/// <summary>One entry of a ranking before fusion: its rank and the raw score of that ranking (cosine/dot or text rank).</summary>
public sealed record RankedHit(string ChunkId, string SourceId, string Title, string Heading, int Rank, double Raw);

/// <summary>Sources whose chunks match the query's words but that the reader may not read (#158): counted, never shown.</summary>
public sealed record HiddenSource(string SourceId, string Name, int Matches);

/// <summary>
/// The whole retrieval (#158), stage by stage: which sources were searched, both rankings with raw scores, and the fused result.
/// <see cref="Final"/> is exactly what <c>search_knowledge</c> returns: production search is this trace's last stage.
/// </summary>
public sealed record RetrievalTrace(
    string? Model, int Dims, IReadOnlyList<string> SearchedSources, IReadOnlyList<RankedHit> ByVector, IReadOnlyList<RankedHit> ByText,
    IReadOnlyList<KnowledgeHit> Final, IReadOnlyList<string> Terms, int OtherModelChunks, IReadOnlyList<HiddenSource> Hidden);

/// <summary>A stored chunk as the inspector shows it, with what is wrong with it.</summary>
public sealed record ChunkInfo(string Id, int Seq, string Heading, string Text, int Chars, int Tokens, string Model, int Dims, double Norm,
    IReadOnlyList<string> Flags);

/// <summary>Metadata of every chunk for health checks; the vector is reduced to its norm and validity.</summary>
public sealed record ChunkMeta(string Id, string SourceId, Guid DocumentId, string Title, DateTimeOffset UpdatedAt, int Seq, string Model, int Dims,
    double Norm, bool HasNaN, int Chars, string TextHash);

public sealed record VectorSample(string ChunkId, string SourceId, string Title, string Heading, float[] Vector);

public sealed record HealthIssue(string Kind, string SourceId, string? DocumentId, string? ChunkId, string Detail);

public sealed record SourceHealth(string Id, string Name, string Status, string? Error, int Documents, int Chunks,
    IReadOnlyDictionary<string, int> Models, int OnCurrentModel, int NotIndexed);

public sealed record KnowledgeHealth(string? CurrentModel, int? CurrentDims, IReadOnlyList<SourceHealth> Sources, IReadOnlyList<HealthIssue> Issues,
    int Chunks, int Documents);

/// <summary>The checks the inspector runs (#158), on data any store can provide.</summary>
public static class KnowledgeInspection
{
    public const int OversizedChars = 4_000;
    public const int StaleDays = 365;

    public static int Tokens(string text) => Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));

    public static double Norm(float[] v)
    {
        double s = 0;
        foreach (var x in v) s += x * x;
        return Math.Sqrt(s);
    }

    public static IReadOnlyList<string> Flags(string text, double norm, bool nan, string? previousText)
    {
        var flags = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) flags.Add("empty");
        if (text.Length > OversizedChars) flags.Add("oversized");
        if (nan) flags.Add("nan_vector");
        else if (norm == 0) flags.Add("zero_vector");
        // Overlap: the chunk starts with text the previous one ended with (chunkers that overlap, or a duplicated paragraph).
        if (previousText is { Length: > 40 } && text.Length > 40 && previousText.EndsWith(text[..40], StringComparison.Ordinal)) flags.Add("overlaps_previous");
        return flags;
    }

    /// <summary>Health of the index against the current embedding model: mismatches, broken vectors, duplicates, stale and unindexed documents.</summary>
    public static KnowledgeHealth Check(IReadOnlyList<KnowledgeSource> sources, IReadOnlyList<ChunkMeta> chunks,
        IReadOnlyDictionary<string, List<DocumentState>> documents, string? currentModel, int? currentDims, DateTimeOffset now)
    {
        var issues = new List<HealthIssue>();
        foreach (var c in chunks)
        {
            if (currentModel is not null && c.Model != currentModel)
                issues.Add(new("model_mismatch", c.SourceId, c.DocumentId.ToString(), c.Id, $"embedded with '{c.Model}', the query model is '{currentModel}': vector search cannot find it"));
            else if (currentDims is { } d && c.Dims != d)
                issues.Add(new("dimension_mismatch", c.SourceId, c.DocumentId.ToString(), c.Id, $"{c.Dims} dimensions, the model gives {d}"));
            if (c.HasNaN) issues.Add(new("nan_vector", c.SourceId, c.DocumentId.ToString(), c.Id, "the vector contains NaN"));
            else if (c.Norm == 0) issues.Add(new("zero_vector", c.SourceId, c.DocumentId.ToString(), c.Id, "the vector is all zeros"));
            if (c.Chars == 0) issues.Add(new("empty_chunk", c.SourceId, c.DocumentId.ToString(), c.Id, "the chunk has no text"));
            if (c.Chars > OversizedChars) issues.Add(new("oversized_chunk", c.SourceId, c.DocumentId.ToString(), c.Id, $"{c.Chars} characters"));
        }
        foreach (var dup in chunks.Where(c => c.Chars > 80).GroupBy(c => c.TextHash).Where(g => g.Select(c => c.DocumentId).Distinct().Count() > 1))
        {
            var first = dup.First();
            issues.Add(new("near_duplicate", first.SourceId, first.DocumentId.ToString(), first.Id,
                $"the same text is in {dup.Select(c => c.DocumentId).Distinct().Count()} documents: {string.Join(", ", dup.Select(c => c.Title).Distinct().Take(4))}"));
        }
        foreach (var doc in chunks.GroupBy(c => c.DocumentId).Select(g => g.First()).Where(c => (now - c.UpdatedAt).TotalDays > StaleDays))
            issues.Add(new("stale_document", doc.SourceId, doc.DocumentId.ToString(), null, $"'{doc.Title}' was last updated {doc.UpdatedAt:yyyy-MM-dd}"));

        var health = new List<SourceHealth>();
        foreach (var s in sources)
        {
            var mine = chunks.Where(c => c.SourceId == s.Id).ToList();
            var docs = documents.GetValueOrDefault(s.Id) ?? [];
            var notIndexed = docs.Count(d => d.Chunks == 0);
            foreach (var d in docs.Where(d => d.Chunks == 0))
                issues.Add(new("not_indexed", s.Id, d.Id.ToString(), null, $"'{d.ExternalId}' has no chunks: it cannot be found"));
            if (s.Status == SourceStatus.Failed) issues.Add(new("source_failed", s.Id, null, null, s.Error ?? "indexing failed"));
            health.Add(new SourceHealth(s.Id, s.Name, s.Status, s.Error, docs.Count, mine.Count,
                mine.GroupBy(c => c.Model).ToDictionary(g => g.Key, g => g.Count()), mine.Count(c => c.Model == currentModel), notIndexed));
        }
        return new KnowledgeHealth(currentModel, currentDims, health, issues, chunks.Count, documents.Values.Sum(d => d.Count));
    }

    /// <summary>
    /// The top two principal components of the vectors (power iteration on the covariance, no dependency), and each vector's coordinates
    /// in them. Deterministic for the same input.
    /// </summary>
    public static (double[][] Axes, double[] Mean) Pca(IReadOnlyList<float[]> vectors, int components = 2)
    {
        var n = vectors.Count;
        var d = vectors[0].Length;
        var mean = new double[d];
        foreach (var v in vectors) for (var i = 0; i < d; i++) mean[i] += v[i] / (double)n;
        var axes = new List<double[]>();
        for (var c = 0; c < components; c++)
        {
            var axis = Enumerable.Range(0, d).Select(i => Math.Sin(i * 7.31 + c * 3.17 + 1)).ToArray(); // fixed start: deterministic
            for (var iter = 0; iter < 60; iter++)
            {
                var next = new double[d];
                foreach (var v in vectors)
                {
                    double dot = 0;
                    for (var i = 0; i < d; i++) dot += (v[i] - mean[i]) * axis[i];
                    for (var i = 0; i < d; i++) next[i] += dot * (v[i] - mean[i]);
                }
                foreach (var prev in axes) // keep it orthogonal to the components found already
                {
                    double p = 0;
                    for (var i = 0; i < d; i++) p += next[i] * prev[i];
                    for (var i = 0; i < d; i++) next[i] -= p * prev[i];
                }
                var len = Math.Sqrt(next.Sum(x => x * x));
                if (len == 0) break;
                for (var i = 0; i < d; i++) axis[i] = next[i] / len;
            }
            axes.Add(axis);
        }
        return (axes.ToArray(), mean);
    }

    public static (double X, double Y) Project(float[] v, double[][] axes, double[] mean)
    {
        double x = 0, y = 0;
        for (var i = 0; i < Math.Min(v.Length, mean.Length); i++)
        {
            var c = v[i] - mean[i];
            x += c * axes[0][i];
            if (axes.Length > 1) y += c * axes[1][i];
        }
        return (x, y);
    }

    /// <summary>Why a search found nothing, or less than expected, in words a person can act on.</summary>
    public static string? Explain(RetrievalTrace t, IReadOnlyList<KnowledgeSource> readable)
    {
        if (t.Final.Count > 0 && t.Hidden.Count == 0) return null;
        var why = new List<string>();
        if (t.SearchedSources.Count == 0) why.Add("you may not read any knowledge source (or the chosen source)");
        if (t.Hidden.Count > 0)
            why.Add($"{t.Hidden.Sum(h => h.Matches)} chunk(s) matching the words are in sources you may not read: {string.Join(", ", t.Hidden.Select(h => h.Name))}");
        var unindexed = readable.Where(s => s.Status != SourceStatus.Ready).ToList();
        if (unindexed.Count > 0) why.Add($"not indexed yet: {string.Join(", ", unindexed.Select(s => $"{s.Name} ({s.Status})"))}");
        if (t.OtherModelChunks > 0 && t.ByVector.Count == 0)
            why.Add($"{t.OtherModelChunks} chunk(s) were embedded with another model than the query ({t.Model}): re-index them");
        if (t.Model is null) why.Add("no embedding model: only word matches are searched");
        if (t.Terms.Count == 0) why.Add("the query has no searchable words");
        if (t.Final.Count == 0 && why.Count == 0) why.Add("no chunk shares a word with the query and vector search returned nothing");
        return why.Count == 0 ? null : string.Join("; ", why) + ".";
    }
}
