using System.Globalization;
using System.Text;

namespace Lots.Evals;

/// <summary>
/// Model comparison (#119): the same dataset over several model aliases and reasoning efforts, side by side. Decision data for
/// which model a profile or the voice path should use (ADR 0007 follow-ups), not a gate.
/// </summary>
public static class ModelComparison
{
    /// <summary>Every alias x effort; a missing list means "as configured" (null).</summary>
    public static IReadOnlyList<(string? Model, string? Effort)> Combos(IReadOnlyList<string> models, IReadOnlyList<string> efforts)
    {
        IReadOnlyList<string?> m = models.Count > 0 ? models.Cast<string?>().ToList() : [null];
        IReadOnlyList<string?> e = efforts.Count > 0 ? efforts.Cast<string?>().ToList() : [null];
        return m.SelectMany(x => e.Select(y => (x, y))).ToList();
    }

    /// <summary>The role that may choose a run's model in the shell's default <c>Models:ChooseRoles</c>; it grants no tools.</summary>
    public const string EvaluatorRole = "evaluator";

    /// <summary>
    /// Roles to run a case with: its own, plus <see cref="EvaluatorRole"/> when a model or effort is chosen, so a refusal case for a
    /// role without access still runs on the model under comparison and still has no tools.
    /// </summary>
    public static string RolesFor(string roles, string? model, string? effort)
    {
        if (model is null && effort is null) return roles;
        var list = roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (!list.Contains(EvaluatorRole, StringComparer.OrdinalIgnoreCase)) list.Add(EvaluatorRole);
        return string.Join(',', list);
    }

    public static string Name(string? model, string? effort) => $"{model ?? "profile model"} · effort {effort ?? "default"}";

    public static string Slug(string? model, string? effort) =>
        string.Concat($"{model ?? "profile"}-{effort ?? "default"}".Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '.' ? ch : '-'));

    public static string Report(EvalDataset set, IReadOnlyList<EvalRunRecord> runs)
    {
        var metricNames = runs.SelectMany(r => r.Summary.Metrics?.Keys ?? []).Distinct().Where(k => k != "completed").Order().ToList();
        var sb = new StringBuilder($"# Model comparison: {set.Name} v{set.Version}\n\n");
        sb.AppendLine($"{set.Cases.Count} cases" + (runs.FirstOrDefault()?.Repeat is > 1 and var n ? $", {n} attempts each" : "") +
                      ". Completed = runs that finished with an answer (reliability); the other metrics are shares of the attempts they apply to.").AppendLine();
        sb.Append("| Model | Effort | Answered by | Pass rate | Completed | ").Append(string.Concat(metricNames.Select(m => $"{Title(m)} | ")))
          .AppendLine("p50 (ms) | p95 (ms) | Tokens | Cost |");
        sb.Append("|---|---|---|---|---|").Append(string.Concat(metricNames.Select(_ => "---|"))).AppendLine("---|---|---|---|");
        foreach (var r in runs)
        {
            var m = r.Summary.Metrics ?? new Dictionary<string, double>();
            sb.Append($"| {r.ModelAlias ?? "(profile)"} | {r.Effort ?? "default"} | {string.Join(", ", r.Models)} | **{r.Summary.PassRate:P0}** | {Pct(m, "completed")} | ")
              .Append(string.Concat(metricNames.Select(k => $"{Pct(m, k)} | ")))
              .AppendLine($"{r.Summary.P50Ms} | {r.Summary.P95Ms} | {r.Summary.Tokens} | {r.Summary.Cost.ToString("0.####", CultureInfo.InvariantCulture)} |");
        }

        if (runs.Count > 1)
        {
            var best = runs.OrderByDescending(r => r.Summary.PassRate).ThenBy(r => r.Summary.P50Ms).First();
            var fastest = runs.Where(r => r.Summary.PassRate >= best.Summary.PassRate - 0.1).OrderBy(r => r.Summary.P50Ms).First();
            sb.AppendLine().AppendLine($"Most accurate: **{Name(best.ModelAlias, best.Effort)}** ({best.Summary.PassRate:P0}, p50 {best.Summary.P50Ms} ms).");
            if (!ReferenceEquals(fastest, best))
                sb.AppendLine($"Fastest within 10 points of it: **{Name(fastest.ModelAlias, fastest.Effort)}** ({fastest.Summary.PassRate:P0}, p50 {fastest.Summary.P50Ms} ms).");

            // Cases where the models disagree are where the choice matters.
            var split = set.Cases.Select(c => (c.Id, Results: runs.Select(r => r.Cases.FirstOrDefault(x => x.Id == c.Id)?.Passed ?? false).ToList()))
                .Where(x => x.Results.Distinct().Count() > 1).ToList();
            if (split.Count > 0)
            {
                sb.AppendLine().AppendLine("## Cases the models disagree on").AppendLine();
                sb.Append("| Case | ").AppendLine(string.Join(" | ", runs.Select(r => Name(r.ModelAlias, r.Effort))) + " |");
                sb.Append("|---|").AppendLine(string.Concat(runs.Select(_ => "---|")));
                foreach (var (id, results) in split)
                    sb.AppendLine($"| {id} | " + string.Join(" | ", results.Select(p => p ? "pass" : "FAIL")) + " |");
            }
        }
        return sb.ToString();
    }

    private static string Pct(IReadOnlyDictionary<string, double> m, string key) => m.TryGetValue(key, out var v) ? v.ToString("P0", CultureInfo.InvariantCulture) : "–";

    private static string Title(string metric) => char.ToUpperInvariant(metric[0]) + metric[1..];
}
