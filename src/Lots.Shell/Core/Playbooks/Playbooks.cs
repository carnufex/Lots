using System.Text.Json;
using Lots.Shell.Core.Config;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Core.Playbooks;

/// <summary>When a step is done (#161): a tool of the step returned without an error, optionally containing a text.</summary>
public sealed record StepCheck(string Tool, string? Contains = null);

public sealed record PlaybookStep(string Name, string Instruction, IReadOnlyList<string> Tools, bool RequireApproval, StepCheck? Check);

/// <summary>
/// A playbook (ADR 0022, #161): a declared, versioned flow inside one profile. The shell enforces it: while a run follows it, only the
/// current step's tools are visible and callable (always a subset of what the profile and roles grant), and the run moves on only when
/// the step's check passes.
/// </summary>
public sealed record PlaybookSpec(string Name, int Version, string Profile, string Description, IReadOnlyList<PlaybookStep> Steps, IReadOnlyList<string> Outputs);

public static class PlaybookParser
{
    public const string CompleteStep = "complete_step";

    public static PlaybookSpec? Parse(string yaml, string name, ProfileRegistry profiles, List<string> errors)
    {
        Doc? d;
        try { d = StrictYaml.Deserializer.Deserialize<Doc>(yaml); }
        catch (Exception ex) { errors.Add(StrictYaml.Explain(ex, typeof(Doc))); return null; }
        if (d is null) { errors.Add("empty"); return null; }
        if (d.Version is null or < 1) errors.Add("version must be 1 or higher");
        var profile = profiles.Find(d.Profile ?? "");
        if (profile is null) errors.Add($"profile '{d.Profile}' does not exist");
        if (string.IsNullOrWhiteSpace(d.Description)) errors.Add("description is required: it says when to use the playbook");
        var steps = new List<PlaybookStep>();
        foreach (var (s, i) in (d.Steps ?? []).Select((s, i) => (s, i + 1)))
        {
            var label = $"step {i} ({s.Name})";
            if (string.IsNullOrWhiteSpace(s.Name)) errors.Add($"step {i} needs a name");
            if (string.IsNullOrWhiteSpace(s.Instruction)) errors.Add($"{label} needs an instruction");
            var tools = (s.Tools ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
            // A playbook narrows: every tool must already be declared in its profile (and policy still decides per call).
            foreach (var t in tools.Where(t => profile is not null && profile.Tools.All(pt => pt.Name != t)))
                errors.Add($"{label}: tool '{t}' is not declared in profile '{profile!.Name}'");
            if (tools.Contains(PlaybookParser.CompleteStep)) errors.Add($"{label}: {CompleteStep} is built in, do not list it");
            StepCheck? check = null;
            if (s.Check is { } c)
            {
                if (string.IsNullOrWhiteSpace(c.Tool) || !tools.Contains(c.Tool)) errors.Add($"{label}: check.tool must be one of the step's tools");
                else check = new StepCheck(c.Tool, string.IsNullOrWhiteSpace(c.Contains) ? null : c.Contains);
            }
            steps.Add(new PlaybookStep(s.Name?.Trim() ?? "", s.Instruction?.Trim() ?? "", tools, s.RequireApproval ?? false, check));
        }
        if (steps.Count == 0) errors.Add("a playbook needs at least one step");
        foreach (var dup in steps.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) errors.Add($"duplicate step '{dup.Key}'");
        return errors.Count > 0 ? null : new PlaybookSpec(name, d.Version!.Value, profile!.Name, d.Description!.Trim(), steps, d.Outputs ?? []);
    }

    private sealed class Doc
    {
        public string? Kind { get; set; }
        public string? Name { get; set; }
        public int? Version { get; set; }
        public string? Profile { get; set; }
        public string? Description { get; set; }
        public List<StepDoc>? Steps { get; set; }
        public List<string>? Outputs { get; set; }
    }

    private sealed class StepDoc
    {
        public string? Name { get; set; }
        public string? Instruction { get; set; }
        public List<string>? Tools { get; set; }
        public bool? RequireApproval { get; set; }
        public CheckDoc? Check { get; set; }
    }

    private sealed class CheckDoc { public string? Tool { get; set; } public string? Contains { get; set; } }
}

/// <summary>The playbooks currently applied (config as code; database-managed, like schedules). Invalid ones are skipped.</summary>
public static class PlaybookCatalog
{
    public static async Task<List<PlaybookSpec>> LoadAsync(LotsDbContext db, ProfileRegistry profiles, CancellationToken ct)
    {
        var rows = await db.ConfigResources.AsNoTracking().Where(r => r.Kind == ResourceKinds.Playbook).ToListAsync(ct);
        var list = new List<PlaybookSpec>();
        foreach (var r in rows)
            if (PlaybookParser.Parse(r.Spec, r.Name, profiles, []) is { } spec) list.Add(spec with { Version = Math.Max(spec.Version, r.Version) });
        return list.OrderBy(p => p.Name).ToList();
    }

    /// <summary>A playbook whose description shares enough words with the question to suggest it (the user confirms; never automatic).</summary>
    public static PlaybookSpec? Suggest(IEnumerable<PlaybookSpec> playbooks, string profile, string question)
    {
        var words = Routing.ContextRouter.Words(question);
        return playbooks.Where(p => p.Profile == profile)
            .Select(p => (p, Score: Routing.ContextRouter.Overlap(words, Routing.ContextRouter.Words(p.Name.Replace('-', ' ') + " " + p.Description))))
            .Where(x => x.Score >= 0.34).OrderByDescending(x => x.Score).Select(x => x.p).FirstOrDefault();
    }
}

/// <summary>Where a run is in its playbook (#161): the snapshot taken when the run started, the step, and what is done.</summary>
public sealed record PlaybookState(PlaybookSpec Spec, int Step, int StepSince, IReadOnlyList<string> Completed)
{
    public bool Finished => Step >= Spec.Steps.Count;
    public PlaybookStep? Current => Finished ? null : Spec.Steps[Step];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static PlaybookState? Of(Persistence.RunRecord run) =>
        run.PlaybookJson is null ? null : JsonSerializer.Deserialize<PlaybookState>(run.PlaybookJson, Json);

    public void Save(Persistence.RunRecord run) => run.PlaybookJson = JsonSerializer.Serialize(this, Json);

    /// <summary>The policy gate for the current step: its tools only (and whether it needs approvals), or nothing at all once finished.</summary>
    public Policy.PlaybookGate Gate() => new(Spec.Name, Spec.Version, Current?.Name ?? "done",
        (Current?.Tools ?? []).ToHashSet(StringComparer.Ordinal), Current?.RequireApproval ?? false, Finished);
}
