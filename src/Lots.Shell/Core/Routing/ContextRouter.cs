using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Lots.Shell.Core.Knowledge;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Tools;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Core.Routing;

/// <param name="ReadOnly">Every tool this user may call in the context is read-class.</param>
public sealed record RouteCandidate(string Profile, double Score, bool ReadOnly);

/// <summary>
/// Where a question goes (#150). <paramref name="Mode"/>: <c>only</c> (one usable context, router skipped), <c>auto</c> (clear winner),
/// <c>sticky</c> (the conversation keeps its context), <c>ask</c> (too close or low confidence: the user picks), <c>none</c>
/// (no usable context; the caller falls back to the default profile, which policy then guards as always).
/// </summary>
public sealed record RouteDecision(string? Profile, string Mode, IReadOnlyList<RouteCandidate> Candidates, double Margin, string Reason,
    string Method, long LatencyMs);

public sealed class RoutingOptions
{
    public const string Section = "Routing";
    public bool Enabled { get; set; } = true;
    /// <summary>Thresholds for embedding scores (cosine similarity). Unrelated text scores about 0.45-0.5 with typical models, hence the floor.</summary>
    public RoutingThresholds Embedding { get; set; } = new() { MinScore = 0.55, AskMargin = 0.04, WriteMargin = 0.08, StickMargin = 0.05 };
    /// <summary>Thresholds for the lexical fallback (share of the question's words found in the context).</summary>
    public RoutingThresholds Lexical { get; set; } = new() { MinScore = 0.12, AskMargin = 0.08, WriteMargin = 0.15, StickMargin = 0.10 };
    /// <summary>The embedding call may take this long before routing falls back to words (latency budget).</summary>
    public int EmbeddingTimeoutMs { get; set; } = 1500;
}

public sealed class RoutingThresholds
{
    /// <summary>Below this the router is not confident and asks.</summary>
    public double MinScore { get; set; }
    /// <summary>The winner must lead by this much, or the user picks.</summary>
    public double AskMargin { get; set; }
    /// <summary>A larger lead needed when the winning context lets this user write: no silent guesses for side effects.</summary>
    public double WriteMargin { get; set; }
    /// <summary>A conversation keeps its context unless another one leads by this much.</summary>
    public double StickMargin { get; set; }
}

/// <summary>
/// The router in front of a run (#150). Policy first: only contexts where one of the user's roles grants something are candidates, so
/// routing can never widen access. Then a cheap relevance score (embeddings of the context's description, tool names and routing
/// examples, or a word overlap when no embedding model answers), never the main model and never tools. The input is the user's
/// message only, never tool output (principle 4).
/// </summary>
public sealed partial class ContextRouter(ProfileRegistry profiles, IOptions<RoutingOptions> options, IEmbeddingModel? embeddings = null,
    ILogger<ContextRouter>? logger = null)
{
    private readonly ConcurrentDictionary<(string, int), float[][]> _vectors = new();

    /// <summary>Contexts this user can use at all: some role of theirs grants a risk class.</summary>
    public static IReadOnlyList<Profile> Usable(Principal me, ProfileRegistry profiles) =>
        profiles.All.Where(p => p.Roles.Any(r => r.Allow.Count > 0 && me.Roles.Contains(r.Name, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public static bool ReadOnlyFor(Principal me, Profile p) =>
        PolicyEngine.VisibleTools(me, p).All(t => p.Tools.First(x => x.Name == t).Risk == ToolRisk.Read);

    /// <summary>What the router compares the question with: the context's own words.</summary>
    public static IReadOnlyList<string> Documents(Profile p)
    {
        var tools = string.Join(", ", p.Tools.Select(t => t.Name.Replace('_', ' ')));
        return [$"{p.Name}. {p.Description}. Tools: {tools}", .. p.Routing];
    }

    public async Task<RouteDecision> RouteAsync(Principal me, string question, string? current, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var usable = Usable(me, profiles);
        if (usable.Count == 0) return new(null, "none", [], 0, "no context grants this user anything", "none", watch.ElapsedMilliseconds);
        if (usable.Count == 1)
            return new(usable[0].Name, "only", [new(usable[0].Name, 1, ReadOnlyFor(me, usable[0]))], 1, "the only context this user can use", "none",
                watch.ElapsedMilliseconds);

        var o = options.Value;
        var (scores, method) = await ScoreAsync(usable, question, ct);
        var decision = Decide(me, usable, scores, method, method == "embedding" ? o.Embedding : o.Lexical, current);
        // A cheap second opinion when the embedding is unsure (e.g. a language the embedding model handles poorly): the words.
        if (decision.Mode == "ask" && method == "embedding")
        {
            var words = Words(question);
            var lexical = Decide(me, usable, usable.Select(p => Documents(p).Max(d => Overlap(words, Words(d)))).ToArray(), "lexical", o.Lexical, current);
            if (lexical.Mode is "auto" or "sticky") decision = lexical with { Method = "embedding+lexical", Reason = lexical.Reason + " (words, the embedding was unsure)" };
        }
        return decision with { LatencyMs = watch.ElapsedMilliseconds };
    }

    private static RouteDecision Decide(Principal me, IReadOnlyList<Profile> usable, double[] scores, string method, RoutingThresholds th, string? current)
    {
        var ranked = usable.Select((p, i) => new RouteCandidate(p.Name, Math.Round(scores[i], 4), ReadOnlyFor(me, p)))
            .OrderByDescending(c => c.Score).ThenBy(c => c.Profile, StringComparer.OrdinalIgnoreCase).ToList();
        var best = ranked[0];
        var margin = Math.Round(best.Score - ranked[1].Score, 4);
        RouteDecision Done(string? profile, string mode, string reason) => new(profile, mode, ranked, margin, reason, method, 0);

        // Stickiness: a conversation stays where it is unless another context clearly leads.
        if (current is not null && ranked.FirstOrDefault(c => string.Equals(c.Profile, current, StringComparison.OrdinalIgnoreCase)) is { } here)
        {
            if (here == best || best.Score - here.Score < th.StickMargin || best.Score < th.MinScore)
                return Done(here.Profile, "sticky", "the conversation keeps its context");
        }

        if (best.Score < th.MinScore) return Done(null, "ask", $"low confidence ({best.Score:0.###} < {th.MinScore})");
        var needed = best.ReadOnly ? th.AskMargin : th.WriteMargin;
        if (margin < needed)
            return Done(null, "ask", best.ReadOnly
                ? $"{best.Profile} and {ranked[1].Profile} are too close ({margin:0.###} < {needed})"
                : $"{best.Profile} can change things for you and does not lead clearly ({margin:0.###} < {needed})");
        return Done(best.Profile, "auto", $"{best.Profile} leads by {margin:0.###}");
    }

    /// <summary>Embeds every context's documents ahead of the first question, so routing stays inside its latency budget.</summary>
    public async Task WarmAsync(CancellationToken ct)
    {
        if (embeddings is not { Configured: true }) return;
        foreach (var p in profiles.All.Where(p => !_vectors.ContainsKey((p.Name, p.Version))))
            _vectors[(p.Name, p.Version)] = [.. (await embeddings.EmbedAsync(Documents(p), ct)).Vectors];
    }

    private async Task<(double[] Scores, string Method)> ScoreAsync(IReadOnlyList<Profile> usable, string question, CancellationToken ct)
    {
        if (embeddings is { Configured: true })
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(options.Value.EmbeddingTimeoutMs);
                var missing = usable.Where(p => !_vectors.ContainsKey((p.Name, p.Version))).ToList();
                foreach (var p in missing)
                    _vectors[(p.Name, p.Version)] = [.. (await embeddings.EmbedAsync(Documents(p), cts.Token)).Vectors];
                var q = (await embeddings.EmbedAsync([question], cts.Token)).Vectors[0];
                return (usable.Select(p => _vectors[(p.Name, p.Version)].Max(v => Cosine(q, v))).ToArray(), "embedding");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger?.LogInformation("Routing falls back to words: {Error}", ex.Message);
            }
        }
        var words = Words(question);
        return (usable.Select(p => Documents(p).Max(d => Overlap(words, Words(d)))).ToArray(), "lexical");
    }

    public static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    /// <summary>The share of the question's words (stemmed crudely: a shared 5-letter prefix counts) found in the document.</summary>
    public static double Overlap(IReadOnlyCollection<string> question, IReadOnlyCollection<string> doc)
    {
        if (question.Count == 0) return 0;
        var stems = doc.Select(Stem).ToHashSet();
        return (double)question.Count(w => stems.Contains(Stem(w))) / question.Count;
    }

    private static string Stem(string w) => w.Length > 5 ? w[..5] : w;

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "was", "which", "what", "who", "how", "why", "when", "where", "does", "did", "can", "could", "would",
        "should", "with", "from", "that", "this", "these", "those", "there", "have", "has", "any", "all", "you", "your", "our", "about",
        "into", "show", "tell", "give", "list", "please", "right", "now", "today", "some", "many", "much", "via", "through", "go", "goes",
        "vilka", "vilken", "vilket", "vad", "hur", "som", "och", "för", "med", "det", "den", "har", "kan", "finns", "från", "till", "alla",
        "visa", "berätta", "just", "nu", "mig", "jag", "du", "är", "går", "genom",
    };

    public static IReadOnlyCollection<string> Words(string text) =>
        WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length >= 3 && !Stop.Contains(w)).Distinct().ToList();

    [GeneratedRegex(@"\p{L}[\p{L}\p{N}]*")]
    private static partial Regex WordPattern();
}

/// <summary>Embeds the contexts shortly after startup (best effort): the first routed question then only embeds itself.</summary>
public sealed class RoutingWarmup(ContextRouter router, ILogger<RoutingWarmup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stop);
            await router.WarmAsync(stop);
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            logger.LogInformation("Routing warm-up skipped: {Error}", ex.Message);
        }
        catch (OperationCanceledException) { }
    }
}
