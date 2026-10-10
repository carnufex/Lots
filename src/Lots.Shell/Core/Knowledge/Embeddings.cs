using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Lots.Shell.Core.Models;

namespace Lots.Shell.Core.Knowledge;

/// <summary>Vectors for texts, in input order, unit length (so cosine similarity is a dot product).</summary>
public sealed record EmbeddingResult(IReadOnlyList<float[]> Vectors, string Model, int Dimensions, int Tokens, TimeSpan Latency);

/// <summary>Provider-neutral embeddings (ADR 0016): OpenAI-compatible <c>/v1/embeddings</c> on the model alias <c>embed</c>.</summary>
public interface IEmbeddingModel
{
    /// <summary>False when no <c>embed</c> alias is configured: knowledge is then unavailable.</summary>
    bool Configured { get; }
    string Model { get; }
    Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
}

public sealed class OpenAiEmbeddingModel(ModelCatalog catalog, IHttpClientFactory http, ILogger<OpenAiEmbeddingModel> logger) : IEmbeddingModel
{
    public const string Alias = "embed";
    private const int BatchSize = 32;

    public bool Configured => catalog.Aliases.ContainsKey(Alias) && catalog.PrimaryModel(Alias).Length > 0;
    public string Model => Configured ? catalog.PrimaryModel(Alias) : "";

    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (!Configured) throw new InvalidOperationException("No embedding model is configured (Models:Aliases:embed).");
        var sw = Stopwatch.StartNew();
        var vectors = new List<float[]>(texts.Count);
        var tokens = 0;
        string? model = null;
        for (var i = 0; i < texts.Count; i += BatchSize)
        {
            var batch = texts.Skip(i).Take(BatchSize).ToList();
            var (v, used, t) = await BatchAsync(batch, ct);
            vectors.AddRange(v);
            tokens += t;
            if (model is not null && model != used)
                throw new InvalidOperationException($"Embedding model changed mid-request ({model} -> {used}); an index never mixes models.");
            model = used;
        }
        return new EmbeddingResult(vectors, model ?? Model, vectors.FirstOrDefault()?.Length ?? 0, tokens, sw.Elapsed);
    }

    /// <summary>
    /// Tries the alias' targets in order like chat calls do. The result names the model that answered: the indexer re-embeds a
    /// source whose stored chunks came from another model, and search only compares vectors of the same model.
    /// </summary>
    private async Task<(List<float[]> Vectors, string Model, int Tokens)> BatchAsync(List<string> batch, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var target in catalog.Aliases[Alias].Targets)
        {
            try
            {
                var client = http.CreateClient(RoutingModelClient.HttpClientName(target.Endpoint));
                using var res = await client.PostAsJsonAsync("embeddings", new JsonObject
                {
                    ["model"] = target.Model,
                    ["input"] = new JsonArray(batch.Select(b => (JsonNode)b).ToArray()),
                }, ct);
                var text = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode)
                    throw new HttpRequestException($"Embedding endpoint returned {(int)res.StatusCode}: {text[..Math.Min(text.Length, 300)]}", null, res.StatusCode);
                var root = JsonNode.Parse(text)!;
                var data = root["data"]!.AsArray().OrderBy(d => d!["index"]?.GetValue<int>() ?? 0)
                    .Select(d => Normalize(d!["embedding"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray())).ToList();
                if (data.Count != batch.Count) throw new InvalidOperationException($"Expected {batch.Count} embeddings, got {data.Count}.");
                return (data, target.Model, root["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0);
            }
            catch (Exception ex) when (RoutingModelClient.Retriable(ex, ct))
            {
                last = ex;
                logger.LogWarning("Embedding endpoint {Endpoint} failed: {Error}", target.Endpoint, ex.Message);
            }
        }
        throw new HttpRequestException($"All embedding endpoints failed: {last?.Message}", last);
    }

    public static float[] Normalize(float[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        if (norm == 0) return v;
        for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
        return v;
    }
}
