using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Lots.Evals;

public sealed record EvalCase(string Id, string Question, List<string>? ExpectedTools = null, List<string>? ExpectedFacts = null);

public sealed record RunOutcome(string Status, string? FinalAnswer, string? Error, IReadOnlyList<string> ToolsCalled, long ModelLatencyMs, int Tokens);

public sealed record EvalResult(EvalCase Case, bool Passed, IReadOnlyList<string> Failures, RunOutcome Outcome);

public static class Scoring
{
    /// <summary>Pass = run completed with a non-empty answer, every expected tool was called, every expected fact is in the answer.</summary>
    public static EvalResult Score(EvalCase c, RunOutcome o)
    {
        var failures = new List<string>();
        if (o.Status != "Completed")
            failures.Add($"run ended as {o.Status}" + (o.Error is null ? "" : $": {o.Error}"));
        if (o.Status == "Completed" && string.IsNullOrWhiteSpace(o.FinalAnswer))
            failures.Add("run completed with an empty answer");
        foreach (var tool in c.ExpectedTools ?? [])
            if (!o.ToolsCalled.Contains(tool))
                failures.Add($"expected tool call not made: {tool}");
        foreach (var fact in c.ExpectedFacts ?? [])
            if (o.FinalAnswer is null || !o.FinalAnswer.Contains(fact, StringComparison.OrdinalIgnoreCase))
                failures.Add($"answer is missing fact: {fact}");
        return new EvalResult(c, failures.Count == 0, failures, o);
    }

    public static string Report(IReadOnlyList<EvalResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Eval report").AppendLine();
        sb.AppendLine($"**{results.Count(r => r.Passed)}/{results.Count} passed**").AppendLine();
        sb.AppendLine("| Case | Result | Tools called | Tokens | Model latency (ms) |").AppendLine("|---|---|---|---|---|");
        foreach (var r in results)
            sb.AppendLine($"| {r.Case.Id} | {(r.Passed ? "PASS" : "FAIL")} | {string.Join(", ", r.Outcome.ToolsCalled)} | {r.Outcome.Tokens} | {r.Outcome.ModelLatencyMs} |");
        foreach (var r in results.Where(r => !r.Passed))
        {
            sb.AppendLine().AppendLine($"## {r.Case.Id}: {r.Case.Question}");
            foreach (var f in r.Failures) sb.AppendLine($"- {f}");
        }
        return sb.ToString();
    }
}

public static class EvalCli
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Usage: Lots.Evals [--url http://localhost:8088] [--file evals/homelab.json] [--report evals/report.md]</summary>
    public static async Task<int> Main(string[] args)
    {
        var url = Arg(args, "--url") ?? "http://localhost:8088";
        var file = Arg(args, "--file") ?? "evals/homelab.json";
        var reportPath = Arg(args, "--report") ?? "evals/report.md";

        var cases = JsonSerializer.Deserialize<List<EvalCase>>(await File.ReadAllTextAsync(file), Json)
                    ?? throw new InvalidOperationException("No eval cases found.");
        using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(30) };

        var results = new List<EvalResult>();
        foreach (var c in cases)
        {
            Console.Write($"{c.Id} ... ");
            var result = Scoring.Score(c, await RunAsync(http, c.Question));
            results.Add(result);
            Console.WriteLine(result.Passed ? "PASS" : "FAIL");
        }

        var report = Scoring.Report(results);
        await File.WriteAllTextAsync(reportPath, report);
        Console.WriteLine();
        Console.WriteLine(report);
        return results.All(r => r.Passed) ? 0 : 1;
    }

    private static async Task<RunOutcome> RunAsync(HttpClient http, string question)
    {
        var started = await (await http.PostAsJsonAsync("/runs", new { prompt = question }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = started.GetProperty("id").GetGuid();

        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            var run = await http.GetFromJsonAsync<JsonElement>($"/runs/{id}");
            var status = run.GetProperty("status").GetString()!;
            if (status is "Completed" or "Failed")
            {
                var steps = run.GetProperty("steps").EnumerateArray().ToList();
                return new RunOutcome(
                    status,
                    run.GetProperty("finalAnswer").GetString(),
                    run.GetProperty("error").GetString(),
                    steps.Where(s => s.GetProperty("kind").GetString() == "ToolCall").Select(s => s.GetProperty("name").GetString()!).ToList(),
                    steps.Sum(s => s.GetProperty("latencyMs").GetInt64()),
                    steps.Sum(s => (s.GetProperty("promptTokens").ValueKind == JsonValueKind.Number ? s.GetProperty("promptTokens").GetInt32() : 0)
                                   + (s.GetProperty("completionTokens").ValueKind == JsonValueKind.Number ? s.GetProperty("completionTokens").GetInt32() : 0)));
            }
            await Task.Delay(1000);
        }
        return new RunOutcome("Timeout", null, "Run did not finish within 5 minutes.", [], 0, 0);
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
