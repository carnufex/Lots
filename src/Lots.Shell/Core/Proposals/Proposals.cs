using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lots.Shell.Core.Outcomes;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Core.Proposals;

public enum ProposalState { Draft, Evaluated, PrOpened, Merged, Rejected }

/// <summary>
/// A proposed change to a profile (#144, ADR 0019 point 4): what to change, why (evidence), the diff, its eval delta, and where it
/// went (a pull request in the config repository). It never touches the live shell: config as code applies it after a person merges.
/// </summary>
public sealed class ProposalRecord
{
    public Guid Id { get; set; }
    public required string Profile { get; set; }
    public int BaseVersion { get; set; }
    public required string Title { get; set; }
    public required string Rationale { get; set; }
    /// <summary>Run ids and notes the proposal rests on, as JSON.</summary>
    public string? EvidenceJson { get; set; }
    public required string BaseYaml { get; set; }
    public required string ProposedYaml { get; set; }
    public required string Diff { get; set; }
    public ProposalState State { get; set; }
    /// <summary>Eval results of the current vs the proposed profile, from <c>Lots.Evals --mode proposal</c>.</summary>
    public string? EvaluationJson { get; set; }
    public bool? Regresses { get; set; }
    public string? PrUrl { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Note { get; set; }
}

/// <param name="Instructions">New instructions for the model.</param>
/// <param name="Description">New description shown to users.</param>
/// <param name="Model">Another configured model alias for the profile's runs.</param>
/// <param name="DetectConflicts">Turn conflict detection between retrieved passages on or off.</param>
public sealed record ProposedChanges(string? Instructions = null, string? Description = null, string? Model = null, bool? DetectConflicts = null);

public sealed record ProposalEvidence(IReadOnlyList<Guid>? RunIds = null, string? Notes = null);

public sealed class ProposalException(string message) : Exception(message);

/// <summary>Builds and validates proposals. The hard limit: only instructions, description, model alias and conflict detection.</summary>
public static class ProposalBuilder
{
    public static string CurrentYaml(string profile, ProfileRegistry profiles, LotsDbContext db) =>
        profiles.FileSpec(profile)
        ?? db.ConfigResources.AsNoTracking().Where(r => r.Kind == Config.ResourceKinds.Profile && r.Name == profile).Select(r => r.Spec).FirstOrDefault()
        ?? throw new ProposalException($"Profile '{profile}' has no source text here (not loaded from a file or the admin API).");

    /// <summary>
    /// The profile with only the allowed keys changed, as text: comments and layout of everything else stay as they are, so the pull
    /// request shows just the change. The version goes up by one.
    /// </summary>
    public static string Apply(string yaml, ProposedChanges c, int nextVersion)
    {
        var text = yaml.Replace("\r\n", "\n");
        if (c.Instructions is { } instructions)
            text = SetBlock(text, "instructions", instructions.Trim());
        if (c.Description is { } description)
            text = SetScalar(text, "description", Quote(description.Trim()));
        if (c.Model is { } model)
            text = SetScalar(text, "model", Quote(model.Trim()));
        if (c.DetectConflicts is { } detect)
            text = SetScalar(text, "detectConflicts", detect ? "true" : "false");
        return SetScalar(text, "version", nextVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Rejects a proposed profile that differs from the current one in anything but the allowed keys.</summary>
    public static void Validate(Profile current, Profile proposed)
    {
        var problems = new List<string>();
        void Same<T>(string what, T a, T b, Func<T, string> show)
        {
            if (show(a) != show(b)) problems.Add(what);
        }
        string Json(object? o) => JsonSerializer.Serialize(o);
        Same("roles", current.Roles, proposed.Roles, Json);
        Same("tools and risk classes", current.Tools, proposed.Tools, Json);
        Same("servers and credentials", current.Servers, proposed.Servers, Json);
        Same("approval rules", current.ApprovalRules, proposed.ApprovalRules, Json);
        Same("policy tests", current.PolicyTests, proposed.PolicyTests, Json);
        Same("sensitivity", current.Sensitivity, proposed.Sensitivity, v => v.ToString());
        Same("personal data masking", current.PiiKinds, proposed.PiiKinds, Json);
        Same("delegates", current.Delegates, proposed.Delegates, Json);
        Same("telemetry", current.TelemetryContent, proposed.TelemetryContent, v => v?.ToString() ?? "");
        Same("name", current.Name, proposed.Name, s => s);
        if (problems.Count > 0)
            throw new ProposalException("A proposal may change instructions, description, model and conflict detection only; it changes " + string.Join(", ", problems) + ".");
    }

    public static ProposalRecord Create(string profileName, ProposedChanges changes, string title, string rationale, ProposalEvidence? evidence, string user,
        ProfileRegistry profiles, LotsDbContext db, ModelCatalogLike models, DateTimeOffset now)
    {
        var current = profiles.Find(profileName) ?? throw new ProposalException($"Unknown profile '{profileName}'.");
        if (changes is { Instructions: null, Description: null, Model: null, DetectConflicts: null }) throw new ProposalException("The proposal changes nothing.");
        if (changes.Model is { } m && !models.Has(m)) throw new ProposalException($"Unknown model alias '{m}'.");
        if (changes.Instructions is { Length: > 20_000 }) throw new ProposalException("Instructions are limited to 20 000 characters.");
        var baseYaml = CurrentYaml(profileName, profiles, db);
        var proposedYaml = Apply(baseYaml, changes, current.Version + 1);
        Profile proposed;
        try { proposed = ProfileParser.Parse(proposedYaml, profileName + " (proposed)"); }
        catch (ProfileException ex) { throw new ProposalException("The proposed profile is not valid: " + string.Join("; ", ex.Errors)); }
        Validate(current, proposed);
        var diff = Config.Diff.Unified(baseYaml, proposedYaml);
        return new ProposalRecord
        {
            Id = Guid.NewGuid(), Profile = profileName, BaseVersion = current.Version, Title = title.Trim(), Rationale = rationale.Trim(),
            EvidenceJson = evidence is null ? null : JsonSerializer.Serialize(evidence), BaseYaml = baseYaml, ProposedYaml = proposedYaml, Diff = diff,
            State = ProposalState.Draft, CreatedBy = user, CreatedAt = now,
        };
    }

    private static string Quote(string s) => s.Length > 0 && !s.Contains('"') && !s.Contains('\n') ? $"\"{s}\"" : JsonSerializer.Serialize(s);

    /// <summary>Replaces (or adds) a top-level <c>key: value</c> line.</summary>
    private static string SetScalar(string text, string key, string value)
    {
        var lines = text.Split('\n').ToList();
        var i = lines.FindIndex(l => l.StartsWith(key + ":", StringComparison.Ordinal));
        if (i >= 0)
        {
            var end = BlockEnd(lines, i);
            lines.RemoveRange(i, end - i);
            lines.Insert(i, $"{key}: {value}");
        }
        else
        {
            var at = lines.FindIndex(l => l.StartsWith("version:", StringComparison.Ordinal));
            lines.Insert(at >= 0 ? at + 1 : 0, $"{key}: {value}");
        }
        return string.Join('\n', lines);
    }

    /// <summary>Replaces (or adds) a top-level literal block <c>key: |</c>.</summary>
    private static string SetBlock(string text, string key, string value)
    {
        var lines = text.Split('\n').ToList();
        var block = new List<string> { $"{key}: |" };
        block.AddRange(value.Replace("\r\n", "\n").Split('\n').Select(l => l.Length == 0 ? "" : "  " + l));
        var i = lines.FindIndex(l => l.StartsWith(key + ":", StringComparison.Ordinal));
        if (i >= 0)
        {
            var end = BlockEnd(lines, i);
            lines.RemoveRange(i, end - i);
            lines.InsertRange(i, block);
        }
        else
        {
            var at = lines.FindIndex(l => l.StartsWith("description:", StringComparison.Ordinal));
            lines.InsertRange(at >= 0 ? at + 1 : lines.Count, block);
        }
        return string.Join('\n', lines);
    }

    /// <summary>The index after a top-level key's value: its own line plus the indented (or blank) lines that follow.</summary>
    private static int BlockEnd(List<string> lines, int start)
    {
        var end = start + 1;
        while (end < lines.Count && (lines[end].Length == 0 || lines[end].StartsWith(' ')) && !(lines[end].Length == 0 && NextIsTopLevel(lines, end)))
            end++;
        return end;
    }

    private static bool NextIsTopLevel(List<string> lines, int blank)
    {
        for (var j = blank; j < lines.Count; j++)
            if (lines[j].Length > 0) return !lines[j].StartsWith(' ');
        return true;
    }
}

/// <summary>What the builder needs to know about models, without the whole catalog.</summary>
public sealed class ModelCatalogLike(Models.ModelCatalog? catalog)
{
    public bool Has(string alias) => catalog?.Aliases.ContainsKey(alias) == true;
}

public sealed class ProposalGitOptions
{
    public const string Section = "Proposals:Git";
    /// <summary>github or gitea. Empty: proposals are not pushed; a person applies the diff.</summary>
    public string? Provider { get; set; }
    /// <summary>API base, e.g. https://api.github.com or https://gitea.example.com/api/v1.</summary>
    public string? ApiUrl { get; set; }
    /// <summary>owner/repo of the configuration repository.</summary>
    public string? Repo { get; set; }
    public string BaseBranch { get; set; } = "main";
    /// <summary>Where a profile lives in the repository; {profile} is replaced.</summary>
    public string PathTemplate { get; set; } = "profiles/{profile}.yaml";
    /// <summary>Environment variable with a token that may push branches and open pull requests (never in config).</summary>
    public string TokenEnv { get; set; } = "LOTS_PROPOSALS_GIT_TOKEN";
}

/// <summary>Opens a pull request for a proposal (GitHub and Gitea speak the same contents and pulls API).</summary>
public sealed class ProposalGit(IHttpClientFactory http, Microsoft.Extensions.Options.IOptions<ProposalGitOptions> options)
{
    public bool Configured => !string.IsNullOrWhiteSpace(options.Value.Provider) && !string.IsNullOrWhiteSpace(options.Value.Repo)
                              && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(options.Value.TokenEnv));

    public async Task<string> OpenPullRequestAsync(ProposalRecord p, CancellationToken ct)
    {
        var o = options.Value;
        var api = (o.ApiUrl ?? (o.Provider == "github" ? "https://api.github.com" : throw new ProposalException("Proposals:Git:ApiUrl is required for gitea."))).TrimEnd('/');
        using var client = http.CreateClient(nameof(ProposalGit));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(o.Provider == "github" ? "Bearer" : "token", Environment.GetEnvironmentVariable(o.TokenEnv));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("lots-proposals");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        var path = o.PathTemplate.Replace("{profile}", p.Profile);
        var branch = $"lots/proposal-{p.Id.ToString("N")[..8]}";

        var file = await client.GetFromJsonAsync<JsonElement>($"{api}/repos/{o.Repo}/contents/{path}?ref={o.BaseBranch}", ct);
        var sha = file.GetProperty("sha").GetString();
        var current = Encoding.UTF8.GetString(Convert.FromBase64String(file.GetProperty("content").GetString()!.Replace("\n", "")));
        if (current.Replace("\r\n", "\n").Trim() != p.BaseYaml.Replace("\r\n", "\n").Trim())
            throw new ProposalException($"{path} on {o.BaseBranch} differs from the profile the proposal was made from; make a new proposal.");

        var baseRef = o.Provider == "github"
            ? (await client.GetFromJsonAsync<JsonElement>($"{api}/repos/{o.Repo}/git/ref/heads/{o.BaseBranch}", ct)).GetProperty("object").GetProperty("sha").GetString()
            : null;
        var created = o.Provider == "github"
            ? await client.PostAsJsonAsync($"{api}/repos/{o.Repo}/git/refs", new { @ref = "refs/heads/" + branch, sha = baseRef }, ct)
            : await client.PostAsJsonAsync($"{api}/repos/{o.Repo}/branches", new { new_branch_name = branch, old_branch_name = o.BaseBranch }, ct);
        created.EnsureSuccessStatusCode();

        var message = $"Proposal: {p.Title} ({p.Profile} v{p.BaseVersion} -> v{p.BaseVersion + 1})";
        var put = await client.PutAsJsonAsync($"{api}/repos/{o.Repo}/contents/{path}", new
        {
            message, branch, sha, content = Convert.ToBase64String(Encoding.UTF8.GetBytes(p.ProposedYaml.TrimEnd() + "\n")),
        }, ct);
        put.EnsureSuccessStatusCode();

        var body = new StringBuilder($"{p.Rationale}\n\n");
        if (p.EvaluationJson is not null) body.Append($"**Eval delta** (current vs proposed):\n\n```json\n{p.EvaluationJson}\n```\n\n");
        if (p.Regresses == true) body.Append("**Warning: the proposed profile regresses at least one eval case.**\n\n");
        body.Append($"Drafted by Lots (proposal {p.Id}) from evidence in the shell's run outcomes. Review before merging: a merge applies it through GitOps.");
        var pr = await client.PostAsJsonAsync($"{api}/repos/{o.Repo}/pulls", new { title = message, head = branch, @base = o.BaseBranch, body = body.ToString() }, ct);
        pr.EnsureSuccessStatusCode();
        return (await pr.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("html_url").GetString()!;
    }
}

public sealed record FollowUpSide(int Version, int Runs, double SuccessRate, long P95WallMs, IReadOnlyDictionary<string, int> Problems);

public sealed record FollowUp(string Verdict, FollowUpSide Before, FollowUpSide? After, string Explanation, string? RevertDiff);

/// <summary>After a merge: did the new version do better than the one it replaced (#144)? Needs enough runs on both sides.</summary>
public static class ProposalFollowUp
{
    public static async Task<FollowUp> CheckAsync(LotsDbContext db, ProposalRecord p, ProfileRegistry profiles, int minRuns, CancellationToken ct)
    {
        var rows = await db.RunOutcomes.AsNoTracking().Where(o => o.Profile == p.Profile && o.ProfileVersion >= p.BaseVersion).ToListAsync(ct);
        FollowUpSide Side(int version, List<RunOutcomeRecord> runs)
        {
            var agg = Features.Insights.OutcomesEndpoint.Aggregate($"v{version}", runs);
            return new FollowUpSide(version, agg.Runs, agg.SuccessRate, agg.P95WallMs, agg.Problems);
        }
        var before = Side(p.BaseVersion, rows.Where(o => o.ProfileVersion == p.BaseVersion).ToList());
        var applied = profiles.Find(p.Profile) is { } live && live.Version > p.BaseVersion && live.Instructions.Trim() == (ProfileParser.Parse(p.ProposedYaml).Instructions.Trim())
            ? live.Version : (int?)null;
        if (applied is null)
            return new FollowUp("not-applied", before, null, "The proposed version is not live yet (not merged, or changed again before applying).", null);
        var after = Side(applied.Value, rows.Where(o => o.ProfileVersion == applied).ToList());
        if (before.Runs < minRuns || after.Runs < minRuns)
            return new FollowUp("too-early", before, after, $"Needs at least {minRuns} runs on each version to compare ({before.Runs} before, {after.Runs} after).", null);
        var delta = after.SuccessRate - before.SuccessRate;
        var verdict = delta >= 0.05 ? "improved" : delta <= -0.05 ? "regressed" : "no-effect";
        var revert = verdict == "regressed" ? Config.Diff.Unified(p.ProposedYaml, p.BaseYaml) : null;
        return new FollowUp(verdict, before, after,
            $"Success rate {before.SuccessRate:P0} -> {after.SuccessRate:P0}, p95 {before.P95WallMs} -> {after.P95WallMs} ms." +
            (verdict == "regressed" ? " Suggested: revert with the diff below." : ""), revert);
    }
}
