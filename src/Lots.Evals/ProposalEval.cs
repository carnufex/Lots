using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lots.Evals;

public sealed record ProposalSide(string Profile, double PassRate, int Passed, int Cases, long P50Ms, IReadOnlyDictionary<string, double> Metrics);

public sealed record ProposalDelta(ProposalSide Current, ProposalSide Proposed, IReadOnlyList<string> Regressions, IReadOnlyList<string> Improvements)
{
    /// <summary>A proposal regresses when any case that passed now fails, or the pass rate drops.</summary>
    public bool Regresses => Regressions.Count > 0 || Proposed.PassRate < Current.PassRate;
}

/// <summary>
/// Evaluates an improvement proposal (#144): the dataset runs against the current profile and against the proposed one, which is
/// applied for the duration under its own name (<c>&lt;profile&gt;-proposal-&lt;id&gt;</c>) and removed afterwards, so the live profile
/// never changes. The delta goes back to the shell, which shows it with the proposal and in its pull request.
/// <c>--mode proposal --proposal &lt;id&gt; --file evals/homelab.json [--repeat 2]</c>; the identity needs admin rights (it applies a profile).
/// </summary>
public static partial class ProposalEval
{
    public static ProposalDelta Compare(string currentProfile, IReadOnlyList<EvalResult> current, string proposedProfile, IReadOnlyList<EvalResult> proposed)
    {
        static Dictionary<string, bool> ByCase(IReadOnlyList<EvalResult> r) =>
            r.GroupBy(x => x.Case.Id).ToDictionary(g => g.Key, g => g.Count(x => x.Passed) * 2 >= g.Count());
        var a = ByCase(current);
        var b = ByCase(proposed);
        ProposalSide Side(string profile, IReadOnlyList<EvalResult> results, Dictionary<string, bool> cases) => new(profile,
            cases.Count == 0 ? 0 : Math.Round((double)cases.Values.Count(p => p) / cases.Count, 4), cases.Values.Count(p => p), cases.Count,
            EvalHistory.Percentile(results.Select(x => x.Outcome.WallMs), 50), Scoring.Metrics(results));
        return new ProposalDelta(Side(currentProfile, current, a), Side(proposedProfile, proposed, b),
            a.Where(kv => kv.Value && b.TryGetValue(kv.Key, out var p) && !p).Select(kv => kv.Key).ToList(),
            a.Where(kv => !kv.Value && b.TryGetValue(kv.Key, out var p) && p).Select(kv => kv.Key).ToList());
    }

    /// <summary>The proposed profile under a temporary name, so it can live next to the real one.</summary>
    public static string Rename(string yaml, string name) => NameLine().Replace(yaml.Replace("\r\n", "\n"), $"name: {name}", 1);

    public static async Task<int> RunAsync(HttpClient http, Func<string, string?> arg, bool devHeaders, string devRoles)
    {
        var id = arg("--proposal") ?? throw new InvalidOperationException("--proposal <id> is required");
        var file = arg("--file") ?? throw new InvalidOperationException("--file <dataset> is required");
        var repeat = Math.Max(1, int.TryParse(arg("--repeat"), out var r) ? r : 1);
        var info = await http.GetFromJsonAsync<JsonElement>($"/insights/proposals/{id}");
        var profile = info.GetProperty("proposal").GetProperty("profile").GetString()!;
        var temp = $"{profile}-proposal-{id.Replace("-", "")[..8]}";
        var yaml = Rename(info.GetProperty("proposedYaml").GetString()!, temp);
        var set = Datasets.Parse(await File.ReadAllTextAsync(file), file);
        var judge = Judge.FromArgs(arg);

        var apply = await http.PostAsJsonAsync("/admin/v1/apply", new { yaml, managedBy = "api" });
        if (!apply.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"Could not apply the proposed profile as {temp}: {(int)apply.StatusCode} {await apply.Content.ReadAsStringAsync()}");
            return 2;
        }
        try
        {
            var current = await RunSideAsync(http, set, profile, repeat, judge, devHeaders, devRoles);
            var proposed = await RunSideAsync(http, set, temp, repeat, judge, devHeaders, devRoles);
            var delta = Compare(profile, current, temp, proposed);
            Console.WriteLine(Report(delta));
            var post = await http.PostAsJsonAsync($"/insights/proposals/{id}/evaluation", new { evaluation = delta, regresses = delta.Regresses });
            if (!post.IsSuccessStatusCode) Console.Error.WriteLine($"Could not record the evaluation: {(int)post.StatusCode}");
            return delta.Regresses ? 1 : 0;
        }
        finally
        {
            await http.DeleteAsync($"/admin/v1/resources/Profile/{temp}");
        }
    }

    private static async Task<List<EvalResult>> RunSideAsync(HttpClient http, EvalDataset set, string profile, int repeat, Judge? judge, bool devHeaders, string devRoles)
    {
        var results = new List<EvalResult>();
        foreach (var c in set.Cases)
            for (var i = 0; i < repeat; i++)
            {
                var res = Scoring.Score(c, await EvalCli.RunAsync(http, c.Question, profile, devHeaders ? c.Roles ?? devRoles : null, null, null));
                if (judge is not null && c.Judge is { Length: > 0 } criteria)
                    res = Scoring.WithJudge(res, await judge.GradeAsync(c.Question, criteria, res.Outcome.FinalAnswer));
                results.Add(res);
                Console.WriteLine($"{profile} {c.Id}: {(res.Passed ? "PASS" : "FAIL")}");
            }
        return results;
    }

    public static string Report(ProposalDelta d)
    {
        var sb = new StringBuilder("# Proposal evaluation\n\n| | Pass rate | Passed | p50 (ms) |\n|---|---|---|---|\n");
        sb.AppendLine($"| current ({d.Current.Profile}) | {d.Current.PassRate:P0} | {d.Current.Passed}/{d.Current.Cases} | {d.Current.P50Ms} |");
        sb.AppendLine($"| proposed | {d.Proposed.PassRate:P0} | {d.Proposed.Passed}/{d.Proposed.Cases} | {d.Proposed.P50Ms} |");
        if (d.Regressions.Count > 0) sb.AppendLine().AppendLine("**Regressed:** " + string.Join(", ", d.Regressions));
        if (d.Improvements.Count > 0) sb.AppendLine().AppendLine("Fixed: " + string.Join(", ", d.Improvements));
        sb.AppendLine().AppendLine(d.Regresses ? "Verdict: the proposal regresses; do not merge as is." : "Verdict: no regression.");
        return sb.ToString();
    }

    [GeneratedRegex(@"(?m)^name:.*$")] private static partial Regex NameLine();
}
