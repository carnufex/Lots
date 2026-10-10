using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Lots.Evals;

/// <param name="Expect">The context the question belongs to, or "ask" when the router should let the user choose.</param>
public sealed record RoutingCase(string Question, string Expect, string? Note = null);

public sealed record RoutingSet(string? Dataset, int Version, List<RoutingCase> Cases);

public sealed record RoutingResult(RoutingCase Case, string Got, string Mode, double Margin, long LatencyMs, bool Passed, bool Skipped, string Method = "-");

/// <summary>
/// Routing evals (#150): question -> expected context, scored against <c>POST /route</c> (the same decision a run makes). Cases whose
/// context this stack does not offer the caller are skipped, so one dataset serves stacks with different profiles. Accuracy and p95
/// latency are reported; <c>--min-accuracy</c> is the regression gate.
/// </summary>
public static class RoutingEval
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(HttpClient http, Func<string, string?> arg)
    {
        var file = arg("--file") ?? "evals/routing.json";
        var set = JsonSerializer.Deserialize<RoutingSet>(await File.ReadAllTextAsync(file), Json)
                  ?? throw new InvalidOperationException($"{file} is empty");
        var contexts = (await http.GetFromJsonAsync<JsonElement>("/me/capabilities", Json)).GetProperty("contexts")
            .EnumerateArray().Select(c => c.GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Console.WriteLine($"Routing eval {set.Dataset ?? Path.GetFileNameWithoutExtension(file)} v{set.Version}: contexts on this stack: {string.Join(", ", contexts)}");

        var results = new List<RoutingResult>();
        foreach (var c in set.Cases)
        {
            if (c.Expect != "ask" && !contexts.Contains(c.Expect))
            {
                results.Add(new(c, "-", "-", 0, 0, false, true));
                continue;
            }
            var res = await http.PostAsJsonAsync("/route", new { prompt = c.Question }, Json);
            res.EnsureSuccessStatusCode();
            var d = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
            var mode = d.GetProperty("mode").GetString()!;
            var got = mode == "ask" ? "ask" : d.GetProperty("profile").GetString() ?? "none";
            var r = new RoutingResult(c, got, mode, d.GetProperty("margin").GetDouble(), d.GetProperty("latencyMs").GetInt64(),
                string.Equals(got, c.Expect, StringComparison.OrdinalIgnoreCase), false, d.GetProperty("method").GetString() ?? "-");
            results.Add(r);
            Console.WriteLine($"{(r.Passed ? "ok  " : "FAIL")} {c.Question} -> {got} ({mode}, {r.Method}, margin {r.Margin:0.###}, {r.LatencyMs} ms){(r.Passed ? "" : $" expected {c.Expect}")}");
        }

        var scored = results.Where(r => !r.Skipped).ToList();
        var accuracy = scored.Count == 0 ? 0 : (double)scored.Count(r => r.Passed) / scored.Count;
        var p95 = Percentile(scored.Select(r => r.LatencyMs).ToList(), 0.95);
        Console.WriteLine($"accuracy {accuracy:P0} ({scored.Count(r => r.Passed)}/{scored.Count}), {results.Count - scored.Count} skipped, p95 {p95} ms");
        await File.WriteAllTextAsync(arg("--report") ?? "evals/report-routing.md", Report(set, results, accuracy, p95));

        var min = double.TryParse(arg("--min-accuracy"), System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 0;
        var budget = long.TryParse(arg("--max-p95-ms"), out var b) ? b : long.MaxValue;
        if (accuracy < min) Console.WriteLine($"GATE: accuracy {accuracy:P0} is below {min:P0}");
        if (p95 > budget) Console.WriteLine($"GATE: p95 {p95} ms is over the {budget} ms budget");
        return accuracy < min || p95 > budget ? 1 : 0;
    }

    public static long Percentile(IReadOnlyList<long> values, double q)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToList();
        return sorted[(int)Math.Clamp(Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1)];
    }

    private static string Report(RoutingSet set, List<RoutingResult> results, double accuracy, long p95)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Routing eval: {set.Dataset} v{set.Version}").AppendLine();
        sb.AppendLine($"Accuracy **{accuracy:P0}**, p95 routing latency **{p95} ms**, {results.Count(r => r.Skipped)} skipped (context not on this stack).").AppendLine();
        sb.AppendLine("| | Question | Expected | Got | Mode | Method | Margin | ms |").AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in results)
            sb.AppendLine($"| {(r.Skipped ? "skip" : r.Passed ? "ok" : "FAIL")} | {r.Case.Question.Replace("|", "\\|")} | {r.Case.Expect} | {r.Got} | {r.Mode} | {r.Method} | {r.Margin:0.###} | {r.LatencyMs} |");
        return sb.ToString();
    }
}
