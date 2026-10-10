using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Lots.Evals;

/// <summary>
/// A versioned eval dataset (#115). Bump <see cref="Version"/> when cases change meaning, so a comparison across versions is flagged
/// instead of silently reading as a regression. The legacy form (a bare JSON array of cases) is still accepted: its name is the file
/// name and its version 1.
/// </summary>
public sealed record EvalDataset(string Name, int Version, string? Profile, IReadOnlyList<EvalCase> Cases);

public static class Datasets
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record DatasetFile(string? Dataset, int Version = 1, string? Profile = null, List<EvalCase>? Cases = null);

    public static EvalDataset Parse(string json, string fileName)
    {
        using var doc = JsonDocument.Parse(json);
        EvalDataset set;
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            set = new(Path.GetFileNameWithoutExtension(fileName), 1, null, doc.RootElement.Deserialize<List<EvalCase>>(Json) ?? []);
        else
        {
            var f = doc.RootElement.Deserialize<DatasetFile>(Json) ?? throw new InvalidDataException("Empty dataset.");
            set = new(f.Dataset ?? Path.GetFileNameWithoutExtension(fileName), f.Version, f.Profile,
                (f.Cases ?? []).Select(c => c.Profile is null && f.Profile is not null ? c with { Profile = f.Profile } : c).ToList());
        }

        if (set.Cases.Count == 0) throw new InvalidDataException($"Dataset '{set.Name}' has no cases.");
        if (set.Cases.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Question)) is { } bad)
            throw new InvalidDataException($"Dataset '{set.Name}' has a case without id or question ({bad.Id}).");
        if (set.Cases.GroupBy(c => c.Id).FirstOrDefault(g => g.Count() > 1) is { } dup)
            throw new InvalidDataException($"Dataset '{set.Name}' has the case id '{dup.Key}' twice.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(set.Name, "^[A-Za-z0-9._-]+$"))
            throw new InvalidDataException($"Dataset name '{set.Name}' may only use letters, digits, '.', '_' and '-'.");
        return set;
    }
}

/// <param name="Metrics">Per check (tools, arguments, facts, forbidden, refusal, judge), the share of attempts it held in.</param>
public sealed record EvalSummary(int Cases, int Passed, double PassRate, long P50Ms, long P95Ms, int Tokens, double Cost,
    IReadOnlyDictionary<string, double>? Metrics = null);

/// <param name="MedianMs">Median wall-clock time of the case's attempts, from start to finished run.</param>
public sealed record EvalCaseRecord(string Id, int Attempts, int Passes, bool Passed, IReadOnlyList<string> Failures,
    IReadOnlyList<string> Tools, long MedianMs, int Tokens, double Cost);

/// <summary>One stored eval run: what ran, against which models, and how every case did. Files under <c>evals/history/&lt;dataset&gt;/</c>.</summary>
/// <param name="ModelAlias">The model alias the runs asked for (#119); null = the profile's own.</param>
/// <param name="Effort">The reasoning effort the runs asked for; null = the model's default.</param>
public sealed record EvalRunRecord(string Dataset, int Version, DateTimeOffset StartedAt, string Target, IReadOnlyList<string> Models,
    int Repeat, EvalSummary Summary, IReadOnlyList<EvalCaseRecord> Cases, string? Label = null, string? ModelAlias = null, string? Effort = null);

public sealed record EvalDiff(EvalRunRecord Previous, bool VersionChanged, double PassRateDelta, long P50DeltaMs,
    IReadOnlyList<string> Regressions, IReadOnlyList<string> Fixed, IReadOnlyList<string> Added, IReadOnlyList<string> Removed);

public static class EvalHistory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Folds the attempts of every case into a record. A case passes when at least <paramref name="casePass"/> of its attempts pass
    /// (model replies vary, so <c>--repeat</c> asks the same question several times).
    /// </summary>
    public static EvalRunRecord Build(EvalDataset set, IReadOnlyList<EvalResult> attempts, DateTimeOffset startedAt, string target,
        int repeat, double casePass = 0.5, string? label = null)
    {
        var cases = set.Cases.Select(c =>
        {
            var mine = attempts.Where(a => a.Case.Id == c.Id).ToList();
            var passes = mine.Count(a => a.Passed);
            return new EvalCaseRecord(c.Id, mine.Count, passes, mine.Count > 0 && (double)passes / mine.Count >= casePass,
                mine.SelectMany(a => a.Failures).Distinct().ToList(),
                mine.SelectMany(a => a.Outcome.ToolsCalled).Distinct().ToList(),
                Percentile(mine.Select(a => a.Outcome.WallMs), 50), mine.Sum(a => a.Outcome.Tokens), Math.Round(mine.Sum(a => a.Outcome.Cost), 4));
        }).ToList();
        var walls = attempts.Select(a => a.Outcome.WallMs).ToList();
        var summary = new EvalSummary(cases.Count, cases.Count(c => c.Passed), cases.Count == 0 ? 0 : Math.Round((double)cases.Count(c => c.Passed) / cases.Count, 4),
            Percentile(walls, 50), Percentile(walls, 95), attempts.Sum(a => a.Outcome.Tokens), Math.Round(attempts.Sum(a => a.Outcome.Cost), 4),
            Scoring.Metrics(attempts));
        var models = attempts.SelectMany(a => a.Outcome.Models ?? []).Distinct().Order().ToList();
        return new EvalRunRecord(set.Name, set.Version, startedAt, target, models, repeat, summary, cases, label);
    }

    /// <summary>Nearest-rank percentile; 0 for no values.</summary>
    public static long Percentile(IEnumerable<long> values, int p)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0) return 0;
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    public static EvalDiff Compare(EvalRunRecord previous, EvalRunRecord current)
    {
        var before = previous.Cases.ToDictionary(c => c.Id);
        var now = current.Cases.ToDictionary(c => c.Id);
        return new EvalDiff(previous, previous.Version != current.Version,
            Math.Round(current.Summary.PassRate - previous.Summary.PassRate, 4), current.Summary.P50Ms - previous.Summary.P50Ms,
            now.Values.Where(c => !c.Passed && before.TryGetValue(c.Id, out var b) && b.Passed).Select(c => c.Id).ToList(),
            now.Values.Where(c => c.Passed && before.TryGetValue(c.Id, out var b) && !b.Passed).Select(c => c.Id).ToList(),
            now.Keys.Where(id => !before.ContainsKey(id)).ToList(),
            before.Keys.Where(id => !now.ContainsKey(id)).ToList());
    }

    /// <summary>Why the gate fails, empty when it passes: the pass rate is below the floor or more cases regressed than allowed.</summary>
    public static IReadOnlyList<string> Gate(EvalRunRecord current, EvalDiff? diff, double minPass, int maxRegressions)
    {
        var why = new List<string>();
        if (current.Summary.PassRate < minPass)
            why.Add($"pass rate {current.Summary.PassRate:P0} is below {minPass:P0}");
        if (diff is not null && diff.Regressions.Count > maxRegressions)
            why.Add($"{diff.Regressions.Count} case(s) regressed since {diff.Previous.StartedAt:u}: {string.Join(", ", diff.Regressions)}");
        return why;
    }

    public static string Save(string root, EvalRunRecord record)
    {
        var dir = Path.Combine(root, record.Dataset);
        Directory.CreateDirectory(dir);
        var name = record.StartedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                   + (record.Label is { Length: > 0 } l ? "-" + string.Concat(l.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-')) : "");
        var path = Path.Combine(dir, name + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(record, Json));
        return path;
    }

    /// <summary>Stored runs of a dataset, oldest first. Unreadable files are skipped, not fatal: history is advisory.</summary>
    public static IReadOnlyList<EvalRunRecord> Load(string root, string dataset)
    {
        var dir = Path.Combine(root, dataset);
        if (!Directory.Exists(dir)) return [];
        var list = new List<EvalRunRecord>();
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<EvalRunRecord>(File.ReadAllText(f), Json) is { } r) list.Add(r);
            }
            catch (JsonException)
            {
                Console.Error.WriteLine($"skipping unreadable eval history file {f}");
            }
        }
        return list.OrderBy(r => r.StartedAt).ToList();
    }

    public static string Section(EvalRunRecord r, EvalDiff? diff)
    {
        var sb = new StringBuilder();
        sb.AppendLine().AppendLine($"## Summary: {r.Dataset} v{r.Version}").AppendLine();
        sb.AppendLine($"Pass rate **{r.Summary.PassRate:P0}** ({r.Summary.Passed}/{r.Summary.Cases}" + (r.Repeat > 1 ? $", {r.Repeat} attempts per case" : "") + ")" +
                      $" · p50 {r.Summary.P50Ms} ms · p95 {r.Summary.P95Ms} ms · {r.Summary.Tokens} tokens" +
                      (r.Summary.Cost > 0 ? $" · cost {r.Summary.Cost.ToString(CultureInfo.InvariantCulture)}" : "") +
                      (r.Models.Count > 0 ? $" · models {string.Join(", ", r.Models)}" : ""));
        if (diff is null)
        {
            sb.AppendLine().AppendLine("No earlier run of this dataset to compare with.");
            return sb.ToString();
        }
        sb.AppendLine().AppendLine($"### Compared with {diff.Previous.StartedAt:u}" + (diff.Previous.Label is { } l ? $" ({l})" : "")).AppendLine();
        if (diff.VersionChanged) sb.AppendLine($"- dataset version changed: v{diff.Previous.Version} → v{r.Version}; only shared case ids are compared");
        sb.AppendLine($"- pass rate {Signed(diff.PassRateDelta * 100)} points, p50 {Signed(diff.P50DeltaMs)} ms");
        if (!diff.Previous.Models.SequenceEqual(r.Models)) sb.AppendLine($"- models: {string.Join(", ", diff.Previous.Models)} → {string.Join(", ", r.Models)}");
        Line(sb, "**regressed**", diff.Regressions);
        Line(sb, "fixed", diff.Fixed);
        Line(sb, "new", diff.Added);
        Line(sb, "removed", diff.Removed);
        return sb.ToString();
    }

    /// <summary>Pass rate and latency over the stored runs of one dataset (<c>--mode history</c>).</summary>
    public static string Trend(string dataset, IReadOnlyList<EvalRunRecord> runs, int last = 20)
    {
        var sb = new StringBuilder($"# Eval history: {dataset}\n\n");
        if (runs.Count == 0) return sb.AppendLine("No stored runs.").ToString();
        sb.AppendLine("| When | Version | Label | Models | Pass rate | p50 (ms) | p95 (ms) | Tokens | Regressed |").AppendLine("|---|---|---|---|---|---|---|---|---|");
        var shown = runs.TakeLast(last).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            var r = shown[i];
            var prevIndex = runs.Count - shown.Count + i - 1;
            var regressed = prevIndex >= 0 ? Compare(runs[prevIndex], r).Regressions.Count : 0;
            sb.AppendLine($"| {r.StartedAt:u} | v{r.Version} | {r.Label} | {string.Join(", ", r.Models)} | {r.Summary.PassRate:P0} | {r.Summary.P50Ms} | {r.Summary.P95Ms} | {r.Summary.Tokens} | {regressed} |");
        }
        return sb.ToString();
    }

    private static void Line(StringBuilder sb, string what, IReadOnlyList<string> ids)
    {
        if (ids.Count > 0) sb.AppendLine($"- {what}: {string.Join(", ", ids)}");
    }

    private static string Signed(double v) => (v > 0 ? "+" : "") + Math.Round(v, 1).ToString(CultureInfo.InvariantCulture);
}
