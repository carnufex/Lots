using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lots.Evals;

/// <summary>A retrieval case: a question and the documents (title prefix or substring) that answer it.</summary>
public sealed record RetrievalCase(string Id, string Question, List<string> Expected, List<string>? Facts = null, string? Source = null);

public sealed record RetrievedTitle(string Title, string ChunkId);

/// <summary>Answer-level result: which passages the answer cited, whether they were right, whether it is grounded.</summary>
public sealed record AnswerCheck(string Status, string? Answer, IReadOnlyList<string> CitedTitles, double? CitationPrecision, bool FactsPresent, bool? Grounded);

public sealed record RetrievalResult(RetrievalCase Case, int? Rank, IReadOnlyList<string> Top, AnswerCheck? Answer);

public sealed record RetrievalSummary(int Cases, double RecallAt1, double RecallAt3, double RecallAt5, double Mrr, double? CitationPrecision, double? FactRate, double? GroundedRate);

public static partial class RetrievalScoring
{
    public static bool Matches(string title, IEnumerable<string> expected) =>
        expected.Any(e => title.StartsWith(e, StringComparison.OrdinalIgnoreCase) || title.Contains(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>1-based rank of the first retrieved passage from an expected document, or null when none was retrieved.</summary>
    public static int? RankOf(IReadOnlyList<string> titles, IReadOnlyList<string> expected)
    {
        for (var i = 0; i < titles.Count; i++)
            if (Matches(titles[i], expected)) return i + 1;
        return null;
    }

    public static RetrievalSummary Summarise(IReadOnlyList<RetrievalResult> r)
    {
        double At(int k) => r.Count == 0 ? 0 : (double)r.Count(x => x.Rank is { } n && n <= k) / r.Count;
        var answers = r.Where(x => x.Answer is not null).Select(x => x.Answer!).ToList();
        var precision = answers.Where(a => a.CitationPrecision is not null).Select(a => a.CitationPrecision!.Value).ToList();
        var grounded = answers.Where(a => a.Grounded is not null).ToList();
        return new RetrievalSummary(r.Count, At(1), At(3), At(5), r.Count == 0 ? 0 : r.Average(x => x.Rank is { } n ? 1.0 / n : 0),
            precision.Count == 0 ? null : precision.Average(),
            answers.Count == 0 ? null : (double)answers.Count(a => a.FactsPresent) / answers.Count,
            grounded.Count == 0 ? null : (double)grounded.Count(a => a.Grounded == true) / grounded.Count);
    }

    /// <summary>[k1] -> chunk id from the "Sources: k1=..., k2=..." line of search_knowledge results (later searches win).</summary>
    public static Dictionary<string, string> CitationMap(IEnumerable<string> searchResults)
    {
        var map = new Dictionary<string, string>();
        foreach (var result in searchResults)
            if (SourcesLine().Match(result) is { Success: true } line)
                foreach (Match m in Citation().Matches(line.Groups[1].Value))
                    map["k" + m.Groups[1].Value] = m.Groups[2].Value;
        return map;
    }

    public static List<string> Cited(string answer) => CitedRef().Matches(answer).Select(m => m.Groups[1].Value).Distinct().ToList();

    public static string Report(RetrievalSummary s, IReadOnlyList<RetrievalResult> results)
    {
        string P(double? v) => v is null ? "–" : $"{v.Value:P0}";
        var sb = new StringBuilder("# Retrieval eval report\n\n");
        sb.AppendLine($"Cases: {s.Cases} · recall@1 {P(s.RecallAt1)} · recall@3 {P(s.RecallAt3)} · **recall@5 {P(s.RecallAt5)}** · MRR {s.Mrr:F2}");
        if (s.CitationPrecision is not null || s.FactRate is not null)
            sb.AppendLine($"Answers: citation precision {P(s.CitationPrecision)} · facts present {P(s.FactRate)} · grounded (judge) {P(s.GroundedRate)}");
        sb.AppendLine().AppendLine("| Case | Rank | Top results | Cited | Facts | Grounded |").AppendLine("|---|---|---|---|---|---|");
        foreach (var r in results)
            sb.AppendLine($"| {r.Case.Id} | {r.Rank?.ToString() ?? "miss"} | {string.Join("; ", r.Top.Take(3)).Replace("|", "/")} | " +
                          $"{(r.Answer is null ? "" : string.Join("; ", r.Answer.CitedTitles).Replace("|", "/"))} | " +
                          $"{(r.Answer is null ? "" : r.Answer.FactsPresent ? "yes" : "no")} | {(r.Answer?.Grounded is { } g ? (g ? "yes" : "no") : "")} |");
        return sb.ToString();
    }

    [GeneratedRegex(@"^Sources: (.*)$", RegexOptions.Multiline)] private static partial Regex SourcesLine();
    [GeneratedRegex(@"k(\d+)=([0-9a-f]+)")] private static partial Regex Citation();
    [GeneratedRegex(@"\[(k\d+)\]")] private static partial Regex CitedRef();
}

public static class RetrievalCli
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Retrieval evals (#57): <c>--mode retrieval --file evals/knowledge.json [--answers] [--profile homelab] [--min-recall 0.8]
    /// [--judge-url http://ollama:11434/v1 --judge-model qwen3.5]</c>. Searches with the caller's identity, exactly like the agent's tool.
    /// </summary>
    public static async Task<int> RunAsync(HttpClient http, string[] args, Func<string, string?> arg)
    {
        var file = arg("--file") ?? "evals/knowledge.json";
        var reportPath = arg("--report") ?? "evals/report-knowledge.md";
        var withAnswers = args.Contains("--answers");
        var profile = arg("--profile") ?? "homelab";
        var minRecall = double.TryParse(arg("--min-recall"), System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 0.8;
        var judge = Judge.FromArgs(arg);

        var cases = JsonSerializer.Deserialize<List<RetrievalCase>>(await File.ReadAllTextAsync(file), Json) ?? [];
        var results = new List<RetrievalResult>();
        foreach (var c in cases)
        {
            Console.Write($"{c.Id} ... ");
            var url = $"/knowledge/search?q={Uri.EscapeDataString(c.Question)}&k=10" + (c.Source is null ? "" : $"&source={Uri.EscapeDataString(c.Source)}");
            var hits = await http.GetFromJsonAsync<List<JsonElement>>(url) ?? [];
            var titles = hits.Select(h => h.GetProperty("title").GetString()!).ToList();
            var rank = RetrievalScoring.RankOf(titles, c.Expected);
            AnswerCheck? answer = withAnswers ? await AnswerAsync(http, c, profile, judge) : null;
            results.Add(new RetrievalResult(c, rank, titles, answer));
            Console.WriteLine(rank is null ? "miss" : $"rank {rank}");
        }

        var summary = RetrievalScoring.Summarise(results);
        var report = RetrievalScoring.Report(summary, results);
        await File.WriteAllTextAsync(reportPath, report);
        Console.WriteLine();
        Console.WriteLine(report);
        if (summary.RecallAt5 < minRecall)
        {
            Console.Error.WriteLine($"recall@5 {summary.RecallAt5:P0} is below the gate {minRecall:P0}");
            return 1;
        }
        return 0;
    }

    private static async Task<AnswerCheck> AnswerAsync(HttpClient http, RetrievalCase c, string profile, Judge? judge)
    {
        var started = await (await http.PostAsJsonAsync("/runs", new { prompt = c.Question, profile })).Content.ReadFromJsonAsync<JsonElement>();
        var id = started.GetProperty("id").GetGuid();
        var deadline = DateTime.UtcNow.AddMinutes(5);
        JsonElement run = default;
        while (DateTime.UtcNow < deadline)
        {
            run = await http.GetFromJsonAsync<JsonElement>($"/runs/{id}");
            if (run.GetProperty("status").GetString() is "Completed" or "Failed" or "Cancelled") break;
            await Task.Delay(1000);
        }
        var status = run.GetProperty("status").GetString()!;
        var answer = run.GetProperty("finalAnswer").GetString();
        if (answer is null) return new AnswerCheck(status, null, [], null, false, null);

        var searches = run.GetProperty("steps").EnumerateArray()
            .Where(s => s.GetProperty("kind").GetString() == "ToolCall" && s.GetProperty("name").GetString() == "search_knowledge")
            .Select(s => s.GetProperty("result").GetString() ?? "");
        var map = RetrievalScoring.CitationMap(searches);
        var cited = new List<(string Title, string Text)>();
        foreach (var k in RetrievalScoring.Cited(answer))
            if (map.TryGetValue(k, out var chunk))
            {
                var res = await http.GetAsync($"/knowledge/chunks/{chunk}");
                if (!res.IsSuccessStatusCode) continue;
                var hit = await res.Content.ReadFromJsonAsync<JsonElement>();
                cited.Add((hit.GetProperty("title").GetString()!, hit.GetProperty("text").GetString()!));
            }

        double? precision = cited.Count == 0 ? 0 : (double)cited.Count(x => RetrievalScoring.Matches(x.Title, c.Expected)) / cited.Count;
        var facts = (c.Facts ?? []).All(f => answer.Contains(f, StringComparison.OrdinalIgnoreCase));
        var grounded = judge is null ? null : await judge.GroundedAsync(c.Question, answer, cited.Select(x => x.Text).ToList());
        return new AnswerCheck(status, answer, cited.Select(x => x.Title).Distinct().ToList(), precision, facts, grounded);
    }
}
