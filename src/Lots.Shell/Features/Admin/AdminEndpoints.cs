using System.Text;
using FastEndpoints;
using Lots.Shell.Core.Config;
using Lots.Shell.Core.Knowledge;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Features.Conversations;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Admin;

/// <summary>Admin API (#66) base: admins only (Auth:AdminRoles). lotsctl uses it with a service token whose identity has the admin role.</summary>
public abstract class AdminEndpoint<TReq, TRes>(IConfiguration config, ICurrentPrincipal who) : Endpoint<TReq, TRes> where TReq : notnull
{
    protected Principal Me => who.Get(HttpContext);

    protected async Task<bool> AllowedAsync(CancellationToken ct)
    {
        if (ConversationViews.IsAdmin(Me, config)) return true;
        await Send.ForbiddenAsync(ct);
        return false;
    }
}

public sealed record ApplyRequest(string Yaml, bool DryRun = false, string ManagedBy = Core.Config.ManagedBy.Api, bool Prune = false);

/// <summary>
/// Declarative apply: validate all resources, show the diff, write them as new versions (or nothing when any is invalid).
/// <c>dryRun</c> only validates and diffs. <c>managedBy: gitops</c> marks them read-only for the UI; <c>prune</c> (gitops only)
/// deletes Git-managed resources that are no longer in the set.
/// </summary>
public sealed class ApplyEndpoint(ConfigService service, IConfiguration config, ICurrentPrincipal who) : AdminEndpoint<ApplyRequest, ApplyOutcome>(config, who)
{
    public override void Configure() => Post("/admin/v1/apply");

    public override async Task HandleAsync(ApplyRequest req, CancellationToken ct)
    {
        if (!await AllowedAsync(ct)) return;
        if (req.ManagedBy is not (Core.Config.ManagedBy.Api or Core.Config.ManagedBy.GitOps))
        {
            AddError(x => x.ManagedBy, "managedBy must be api or gitops.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        var errors = new List<string>();
        var docs = ConfigService.Split(req.Yaml ?? "", errors);
        if (errors.Count > 0)
        {
            await Send.ResponseAsync(new ApplyOutcome(false, req.DryRun, errors.Select(e => new ApplyResult("?", "?", "invalid", 0, null, [e])).ToList()), 422, ct);
            return;
        }
        var outcome = await service.ApplyAsync(docs, Me.UserId, req.ManagedBy, req.DryRun, req.Prune, ct);
        await Send.ResponseAsync(outcome, outcome.HasErrors ? 422 : 200, ct);
    }
}

public sealed record ResourceDto(string Kind, string Name, int? Version, string ManagedBy, DateTimeOffset? AppliedAt, string? AppliedBy, bool Editable);

/// <summary>Every resource the shell knows, with who manages it: file (image/ConfigMap), config (appsettings/env), api or gitops.</summary>
public sealed class ListResourcesEndpoint(LotsDbContext db, ProfileRegistry profiles, IKnowledgeStore knowledge, ModelCatalog catalog, IConfiguration config, ICurrentPrincipal who)
    : AdminEndpoint<EmptyRequest, List<ResourceDto>>(config, who)
{
    public override void Configure() => Get("/admin/v1/resources");

    public override async Task HandleAsync(EmptyRequest _, CancellationToken ct)
    {
        if (!await AllowedAsync(ct)) return;
        var stored = await db.ConfigResources.AsNoTracking().ToListAsync(ct);
        var list = new List<ResourceDto>();
        foreach (var p in profiles.All.OrderBy(p => p.Name))
        {
            var row = stored.FirstOrDefault(s => s.Kind == ResourceKinds.Profile && s.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            var by = profiles.ManagedBy(p.Name) ?? ManagedBy.File;
            list.Add(new ResourceDto(ResourceKinds.Profile, p.Name, p.Version, by, row?.AppliedAt, row?.AppliedBy, by == ManagedBy.Api));
        }
        foreach (var s in await knowledge.ListSourcesAsync(ct))
        {
            var row = stored.FirstOrDefault(r => r.Kind == ResourceKinds.KnowledgeSource && r.Name == s.Id);
            list.Add(new ResourceDto(ResourceKinds.KnowledgeSource, s.Id, row?.Version, s.ManagedBy, row?.AppliedAt, row?.AppliedBy, s.ManagedBy == ManagedBy.Api));
        }
        foreach (var a in catalog.Aliases.Keys.Order())
            list.Add(new ResourceDto("ModelAlias", a, null, ManagedBy.Config, null, null, false));
        await Send.OkAsync(list, ct);
    }
}

public sealed record ResourceRequest(string Kind, string Name);

public sealed record ResourceSpecDto(string Kind, string Name, string ManagedBy, int? Version, string Spec);

public sealed class GetResourceEndpoint(LotsDbContext db, ProfileRegistry profiles, IConfiguration config, ICurrentPrincipal who)
    : AdminEndpoint<ResourceRequest, ResourceSpecDto>(config, who)
{
    public override void Configure() => Get("/admin/v1/resources/{Kind}/{Name}");

    public override async Task HandleAsync(ResourceRequest req, CancellationToken ct)
    {
        if (!await AllowedAsync(ct)) return;
        if (req.Kind == ResourceKinds.Profile && profiles.FileSpec(req.Name) is { } text)
        {
            await Send.OkAsync(new ResourceSpecDto(req.Kind, req.Name, ManagedBy.File, profiles.Find(req.Name)?.Version, text), ct);
            return;
        }
        var row = await db.ConfigResources.AsNoTracking().SingleOrDefaultAsync(r => r.Kind == req.Kind && r.Name == req.Name, ct);
        if (row is null) await Send.NotFoundAsync(ct);
        else await Send.OkAsync(new ResourceSpecDto(row.Kind, row.Name, row.ManagedBy, row.Version, row.Spec), ct);
    }
}

public sealed record VersionDto(Guid Id, int Version, string Action, string ManagedBy, string AppliedBy, DateTimeOffset AppliedAt, string Diff);

/// <summary>Version history of one resource, newest first, each with its diff to the version before.</summary>
public sealed class ResourceVersionsEndpoint(LotsDbContext db, IConfiguration config, ICurrentPrincipal who) : AdminEndpoint<ResourceRequest, List<VersionDto>>(config, who)
{
    public override void Configure() => Get("/admin/v1/resources/{Kind}/{Name}/versions");

    public override async Task HandleAsync(ResourceRequest req, CancellationToken ct)
    {
        if (!await AllowedAsync(ct)) return;
        var versions = await db.ConfigVersions.AsNoTracking().Where(v => v.Kind == req.Kind && v.Name == req.Name).OrderBy(v => v.AppliedAt).ToListAsync(ct);
        var list = new List<VersionDto>();
        string previous = "";
        foreach (var v in versions)
        {
            list.Add(new VersionDto(v.Id, v.Version, v.Action, v.ManagedBy, v.AppliedBy, v.AppliedAt, Diff.Unified(previous, v.Spec ?? "")));
            previous = v.Spec ?? "";
        }
        list.Reverse();
        await Send.OkAsync(list, ct);
    }
}

public sealed record DeleteResourceRequest(string Kind, string Name, string? ManagedBy = null);

public sealed class DeleteResourceEndpoint(ConfigService service, IConfiguration config, ICurrentPrincipal who) : AdminEndpoint<DeleteResourceRequest, ApplyResult>(config, who)
{
    public override void Configure() => Delete("/admin/v1/resources/{Kind}/{Name}");

    public override async Task HandleAsync(DeleteResourceRequest req, CancellationToken ct)
    {
        if (!await AllowedAsync(ct)) return;
        var result = await service.DeleteAsync(req.Kind, req.Name, Me.UserId, req.ManagedBy ?? ManagedBy.Api, ct);
        if (result is null) await Send.NotFoundAsync(ct);
        else await Send.ResponseAsync(result, result.Errors.Count > 0 ? 409 : 200, ct);
    }
}

public sealed record ChangeDto(string Kind, string Name, int Version, string Action, string ManagedBy, string AppliedBy, DateTimeOffset AppliedAt);

/// <summary>Who changed which configuration when (the admin audit trail).</summary>
public sealed class ChangesEndpoint(LotsDbContext db, IConfiguration config, ICurrentPrincipal who) : AdminEndpoint<EmptyRequest, List<ChangeDto>>(config, who)
{
    public override void Configure() => Get("/admin/v1/changes");

    public override async Task HandleAsync(EmptyRequest _, CancellationToken ct)
    {
        if (!await AllowedAsync(ct)) return;
        var rows = await db.ConfigVersions.AsNoTracking().OrderByDescending(v => v.AppliedAt).Take(500).ToListAsync(ct);
        await Send.OkAsync(rows.Select(v => new ChangeDto(v.Kind, v.Name, v.Version, v.Action, v.ManagedBy, v.AppliedBy, v.AppliedAt)).ToList(), ct);
    }
}

/// <summary>Everything applicable as one multi-document YAML stream: the starting point for a GitOps repository.</summary>
public sealed class ExportEndpoint(LotsDbContext db, ProfileRegistry profiles, IKnowledgeStore knowledge, IConfiguration config, ICurrentPrincipal who)
    : AdminEndpoint<EmptyRequest, string>(config, who)
{
    public override void Configure() => Get("/admin/v1/export");

    public override async Task HandleAsync(EmptyRequest _, CancellationToken ct)
    {
        if (!await AllowedAsync(ct)) return;
        var sb = new StringBuilder();
        void Doc(string header, string body)
        {
            if (sb.Length > 0) sb.Append("---\n");
            sb.Append(header).Append(body.TrimEnd()).Append('\n');
        }
        var stored = await db.ConfigResources.AsNoTracking().OrderBy(r => r.Kind).ThenBy(r => r.Name).ToListAsync(ct);
        foreach (var p in profiles.All.OrderBy(p => p.Name))
            if (profiles.FileSpec(p.Name) is { } text) Doc($"# Profile {p.Name} (from a file)\nkind: Profile\n", text);
        foreach (var r in stored) Doc($"# {r.Kind} {r.Name} ({r.ManagedBy}, v{r.Version})\n", r.Spec.Contains("kind:") ? r.Spec : $"kind: {r.Kind}\n{r.Spec}");
        foreach (var s in (await knowledge.ListSourcesAsync(ct)).Where(s => stored.All(r => r.Kind != ResourceKinds.KnowledgeSource || r.Name != s.Id) && !s.Readers.Any(x => x.StartsWith("user:", StringComparison.Ordinal))))
            Doc($"# KnowledgeSource {s.Id} ({s.ManagedBy})\n",
                $"kind: KnowledgeSource\nid: {s.Id}\nname: {Quote(s.Name)}\nsourceKind: {s.Kind}\n" + (s.Location is null ? "" : $"location: {Quote(s.Location)}\n") +
                "readers:\n" + string.Concat(s.Readers.Select(x => $"  - {Quote(x)}\n")));
        await Send.StringAsync(sb.ToString(), contentType: "application/yaml", cancellation: ct);
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
}

public sealed record GitOpsDto(GitOpsState State, IReadOnlyList<ApplyResult> Drift);

/// <summary>GitOps status (#68): last sync and drift, i.e. what applying the Git directory now would change.</summary>
public sealed class GitOpsStatusEndpoint(GitOpsStatus status, ConfigService service, Microsoft.Extensions.Options.IOptions<GitOpsOptions> options, IConfiguration config, ICurrentPrincipal who)
    : AdminEndpoint<EmptyRequest, GitOpsDto>(config, who)
{
    public override void Configure() => Get("/admin/v1/gitops");

    public override async Task HandleAsync(EmptyRequest _, CancellationToken ct)
    {
        if (!await AllowedAsync(ct)) return;
        var drift = new List<ApplyResult>();
        if (options.Value.Path is { Length: > 0 } path)
        {
            var (docs, _, errors) = GitOpsSyncWorker.Read(path);
            if (errors.Count == 0)
                drift = (await service.ApplyAsync(docs, Me.UserId, ManagedBy.GitOps, dryRun: true, prune: options.Value.Prune, ct)).Results
                    .Where(r => r.Action != "unchanged").ToList();
        }
        await Send.OkAsync(new GitOpsDto(status.Current, drift), ct);
    }
}

public sealed class GitOpsSyncNowEndpoint(GitOpsSyncWorker worker, GitOpsStatus status, IConfiguration config, ICurrentPrincipal who) : AdminEndpoint<EmptyRequest, GitOpsState>(config, who)
{
    public override void Configure() => Post("/admin/v1/gitops/sync");

    public override async Task HandleAsync(EmptyRequest _, CancellationToken ct)
    {
        if (!await AllowedAsync(ct)) return;
        await worker.SyncOnceAsync(ct);
        await Send.OkAsync(status.Current, ct);
    }
}
