using System.Text;
using Lots.Shell.Core.Knowledge;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lots.Shell.Core.Config;

public static class ResourceKinds
{
    public const string Profile = "Profile";
    public const string KnowledgeSource = "KnowledgeSource";
    /// <summary>A run started by cron or a webhook (#101).</summary>
    public const string Schedule = "Schedule";
    public static readonly string[] Applicable = [Profile, KnowledgeSource, Schedule];
}

public static class ManagedBy
{
    public const string Api = "api";
    public const string GitOps = "gitops";
    public const string File = "file";
    public const string Config = "config";
}

/// <summary>One resource as read from an apply request: kind, name and its YAML text.</summary>
public sealed record ResourceDocument(string Kind, string Name, string Spec);

/// <summary>What apply did (or would do, on a dry run) to one resource. Diff is a unified line diff of the YAML.</summary>
public sealed record ApplyResult(string Kind, string Name, string Action, int Version, string? Diff, IReadOnlyList<string> Errors);

public sealed record ApplyOutcome(bool Applied, bool DryRun, IReadOnlyList<ApplyResult> Results)
{
    public bool HasErrors => Results.Any(r => r.Errors.Count > 0);
}

/// <summary>
/// The declarative admin API's core (#66, principle 7): validate every resource, diff it against what is stored, then write all
/// of them (or nothing) as new versions with who/when, and make them live. Resources managed from Git are read-only for the UI;
/// a GitOps apply may take over a resource created through the API.
/// </summary>
public sealed class ConfigService(LotsDbContext db, ProfileRegistry profiles, ModelCatalog catalog, IKnowledgeStore knowledge, TimeProvider clock)
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();

    /// <summary>Splits a multi-document YAML stream (documents separated by <c>---</c>) into resources.</summary>
    public static List<ResourceDocument> Split(string yaml, List<string> errors)
    {
        var docs = new List<ResourceDocument>();
        var parts = yaml.Replace("\r\n", "\n").Split('\n').Aggregate(new List<StringBuilder> { new() }, (acc, line) =>
        {
            if (line.TrimEnd() == "---") acc.Add(new StringBuilder());
            else acc[^1].Append(line).Append('\n');
            return acc;
        }).Select(b => b.ToString()).Where(s => s.Split('\n').Any(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'))).ToList();

        foreach (var (text, i) in parts.Select((t, i) => (t, i + 1)))
        {
            Dictionary<string, object?>? head;
            try { head = Yaml.Deserialize<Dictionary<string, object?>>(text); }
            catch (Exception ex) { errors.Add($"document {i}: not valid YAML: {ex.Message}"); continue; }
            var kind = head?.GetValueOrDefault("kind")?.ToString() ?? ResourceKinds.Profile;
            var name = (kind == ResourceKinds.KnowledgeSource ? head?.GetValueOrDefault("id") : head?.GetValueOrDefault("name"))?.ToString();
            if (!ResourceKinds.Applicable.Contains(kind)) { errors.Add($"document {i}: unknown kind '{kind}' ({string.Join(", ", ResourceKinds.Applicable)})"); continue; }
            if (string.IsNullOrWhiteSpace(name)) { errors.Add($"document {i}: {kind} needs a {(kind == ResourceKinds.KnowledgeSource ? "id" : "name")}"); continue; }
            docs.Add(new ResourceDocument(kind, name, text.Trim() + "\n"));
        }
        return docs;
    }

    public async Task<ApplyOutcome> ApplyAsync(IReadOnlyList<ResourceDocument> docs, string user, string managedBy, bool dryRun, bool prune, CancellationToken ct)
    {
        var stored = await db.ConfigResources.ToListAsync(ct);
        var results = new List<ApplyResult>();
        var writes = new List<(ResourceDocument Doc, int Version, string Action, object Parsed)>();

        foreach (var dup in docs.GroupBy(d => (d.Kind, d.Name)).Where(g => g.Count() > 1))
            results.Add(new ApplyResult(dup.Key.Kind, dup.Key.Name, "invalid", 0, null, ["appears more than once in this apply"]));

        foreach (var doc in docs.DistinctBy(d => (d.Kind, d.Name)))
        {
            var errors = new List<string>();
            var existing = stored.FirstOrDefault(s => s.Kind == doc.Kind && s.Name.Equals(doc.Name, StringComparison.OrdinalIgnoreCase));
            object? parsed = null;
            var version = 1;

            if (existing is not null && existing.ManagedBy == ManagedBy.GitOps && managedBy != ManagedBy.GitOps)
                errors.Add("managed from Git: change it in the repository");

            if (doc.Kind == ResourceKinds.Profile)
            {
                if (profiles.IsFileManaged(doc.Name)) errors.Add("a profile with this name is loaded from a file and is read-only");
                try
                {
                    var p = ProfileParser.Parse(doc.Spec, doc.Name);
                    if (p.Model is { } alias && !catalog.Aliases.ContainsKey(alias)) errors.Add($"model alias '{alias}' is not configured");
                    version = p.Version;
                    if (existing is not null && Normalise(existing.Spec) != Normalise(doc.Spec) && p.Version <= existing.Version)
                        errors.Add($"content changed but version {p.Version} is not higher than the stored {existing.Version}");
                    parsed = p;
                }
                catch (ProfileException ex) { errors.AddRange(ex.Errors); }
            }
            else if (doc.Kind == ResourceKinds.Schedule)
            {
                parsed = Schedules.ScheduleParser.Parse(doc.Spec, doc.Name, profiles, errors);
                version = existing is null ? 1 : Normalise(existing.Spec) == Normalise(doc.Spec) ? existing.Version : existing.Version + 1;
            }
            else
            {
                var source = ParseSource(doc, errors);
                version = existing is null ? 1 : Normalise(existing.Spec) == Normalise(doc.Spec) ? existing.Version : existing.Version + 1;
                parsed = source;
            }

            var unchanged = existing is not null && Normalise(existing.Spec) == Normalise(doc.Spec) && existing.ManagedBy == managedBy;
            var action = errors.Count > 0 ? "invalid" : existing is null ? "create" : unchanged ? "unchanged" : "update";
            results.Add(new ApplyResult(doc.Kind, doc.Name, action, version, unchanged ? null : Diff.Unified(existing?.Spec ?? "", doc.Spec), errors));
            if (errors.Count == 0 && !unchanged) writes.Add((doc, version, action, parsed!));
        }

        var deletes = new List<ConfigResourceRecord>();
        if (prune && managedBy == ManagedBy.GitOps)
            foreach (var s in stored.Where(s => s.ManagedBy == ManagedBy.GitOps && !docs.Any(d => d.Kind == s.Kind && d.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase))))
            {
                deletes.Add(s);
                results.Add(new ApplyResult(s.Kind, s.Name, "delete", s.Version, Diff.Unified(s.Spec, ""), []));
            }

        var outcome = new ApplyOutcome(false, dryRun, results);
        if (dryRun || outcome.HasErrors || (writes.Count == 0 && deletes.Count == 0)) return outcome;

        var now = clock.GetUtcNow();
        foreach (var (doc, version, action, _) in writes)
        {
            var row = stored.FirstOrDefault(s => s.Kind == doc.Kind && s.Name.Equals(doc.Name, StringComparison.OrdinalIgnoreCase));
            if (row is null) db.ConfigResources.Add(row = new ConfigResourceRecord { Kind = doc.Kind, Name = doc.Name, ManagedBy = managedBy, AppliedBy = user });
            row.ManagedBy = managedBy;
            row.Version = version;
            row.Spec = doc.Spec;
            row.AppliedBy = user;
            row.AppliedAt = now;
            db.ConfigVersions.Add(new ConfigVersionRecord
            {
                Id = Guid.NewGuid(), Kind = doc.Kind, Name = doc.Name, Version = version, Spec = doc.Spec, ManagedBy = managedBy, AppliedBy = user, AppliedAt = now, Action = action,
            });
        }
        foreach (var d in deletes) Remove(d, user, now);
        await db.SaveChangesAsync(ct);

        // Make it live: profiles in this replica now (others pick it up through ConfigSync), knowledge sources in the shared store.
        foreach (var (_, _, _, parsed) in writes)
            if (parsed is KnowledgeSource source) await knowledge.UpsertSourceAsync(source with { ManagedBy = managedBy }, ct);
        foreach (var d in deletes.Where(d => d.Kind == ResourceKinds.KnowledgeSource)) await knowledge.DeleteSourceAsync(d.Name, ct);
        await ReloadProfilesAsync(db, profiles, ct);
        return outcome with { Applied = true };
    }

    public async Task<ApplyResult?> DeleteAsync(string kind, string name, string user, string managedBy, CancellationToken ct)
    {
        var row = await db.ConfigResources.SingleOrDefaultAsync(r => r.Kind == kind && r.Name == name, ct);
        if (row is null) return null;
        if (row.ManagedBy == ManagedBy.GitOps && managedBy != ManagedBy.GitOps)
            return new ApplyResult(kind, name, "invalid", row.Version, null, ["managed from Git: remove it from the repository"]);
        Remove(row, user, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        if (kind == ResourceKinds.KnowledgeSource) await knowledge.DeleteSourceAsync(name, ct);
        await ReloadProfilesAsync(db, profiles, ct);
        return new ApplyResult(kind, name, "delete", row.Version, Diff.Unified(row.Spec, ""), []);
    }

    private void Remove(ConfigResourceRecord row, string user, DateTimeOffset now)
    {
        db.ConfigResources.Remove(row);
        db.ConfigVersions.Add(new ConfigVersionRecord
        {
            Id = Guid.NewGuid(), Kind = row.Kind, Name = row.Name, Version = row.Version, Spec = null, ManagedBy = row.ManagedBy, AppliedBy = user, AppliedAt = now, Action = "delete",
        });
    }

    /// <summary>Loads the database-managed profiles into the registry. A stored profile that no longer parses is skipped, not fatal.</summary>
    public static async Task ReloadProfilesAsync(LotsDbContext db, ProfileRegistry profiles, CancellationToken ct)
    {
        var rows = await db.ConfigResources.AsNoTracking().Where(r => r.Kind == ResourceKinds.Profile).ToListAsync(ct);
        var managed = new List<(Profile, string)>();
        foreach (var r in rows)
            try { managed.Add((ProfileParser.Parse(r.Spec, r.Name), r.ManagedBy)); }
            catch (ProfileException) { /* validated on apply; a later parser change must not take the shell down */ }
        profiles.SetManaged(managed);
    }

    private static KnowledgeSource? ParseSource(ResourceDocument doc, List<string> errors)
    {
        SourceDoc? s;
        try { s = StrictYaml.Deserializer.Deserialize<SourceDoc>(doc.Spec); }
        catch (Exception ex) { errors.Add(StrictYaml.Explain(ex, typeof(SourceDoc))); return null; }
        if (s is null) { errors.Add("empty"); return null; }
        var kind = s.SourceKind ?? SourceKinds.Directory;
        if (!SourceKinds.All.Contains(kind)) errors.Add($"sourceKind must be one of {string.Join(", ", SourceKinds.All)}");
        var readers = KnowledgeAccess.Normalise(s.Readers ?? [], errors.Add);
        if (readers.Count == 0) errors.Add("at least one reader (*, role:<name> or user:<id>) is required");
        if (kind != SourceKinds.Upload && string.IsNullOrWhiteSpace(s.Location)) errors.Add("location is required for directory and url sources");
        return new KnowledgeSource(doc.Name, s.Name ?? doc.Name, kind, s.Location, readers, "admin-api");
    }

    private sealed class SourceDoc
    {
        public string? Kind { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? SourceKind { get; set; }
        public string? Location { get; set; }
        public List<string>? Readers { get; set; }
    }

    private static string Normalise(string spec) => string.Join('\n', spec.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd())).Trim();
}

/// <summary>Minimal line diff (LCS) for small YAML documents, unified style with +/- prefixes.</summary>
public static class Diff
{
    public static string Unified(string before, string after)
    {
        var a = Lines(before);
        var b = Lines(after);
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var sb = new StringBuilder();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y]) { sb.Append("  ").Append(a[x]).Append('\n'); x++; y++; }
            else if (y < b.Length && (x == a.Length || lcs[x, y + 1] > lcs[x + 1, y])) { sb.Append("+ ").Append(b[y]).Append('\n'); y++; }
            else { sb.Append("- ").Append(a[x]).Append('\n'); x++; }
        }
        return sb.ToString();
    }

    private static string[] Lines(string s) => s.Length == 0 ? [] : s.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
}

/// <summary>Keeps every replica's profiles in step with the database: loads them at start and whenever a change is applied elsewhere.</summary>
public sealed class ConfigSyncWorker(IServiceScopeFactory scopes, ProfileRegistry profiles, ILogger<ConfigSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        DateTimeOffset? seen = null;
        var loaded = false;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
                var latest = await db.ConfigVersions.AsNoTracking().OrderByDescending(v => v.AppliedAt).Select(v => (DateTimeOffset?)v.AppliedAt).FirstOrDefaultAsync(stop);
                if (!loaded || latest != seen)
                {
                    await ConfigService.ReloadProfilesAsync(db, profiles, stop);
                    seen = latest;
                    loaded = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                logger.LogWarning("Loading managed configuration failed: {Error}", ex.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(loaded ? 15 : 3), stop).ContinueWith(_ => { });
        }
    }
}
