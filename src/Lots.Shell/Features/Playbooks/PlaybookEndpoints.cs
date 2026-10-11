using FastEndpoints;
using Lots.Shell.Core.Config;
using Lots.Shell.Core.Playbooks;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Playbooks;

public sealed record PlaybookDto(string Name, int Version, string Profile, string Description, IReadOnlyList<PlaybookStep> Steps, IReadOnlyList<string> Outputs,
    string ManagedBy, string Spec);

/// <summary>
/// The playbooks the caller can start (#161): those whose context one of their roles grants. Applied like every resource
/// (<c>kind: Playbook</c> via the admin API, lotsctl or GitOps); read-only here.
/// </summary>
public sealed class ListPlaybooksEndpoint(LotsDbContext db, ProfileRegistry profiles, ICurrentPrincipal who) : EndpointWithoutRequest<List<PlaybookDto>>
{
    public override void Configure() => Get("/playbooks");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var usable = Core.Routing.ContextRouter.Usable(me, profiles).Select(p => p.Name).ToHashSet();
        var rows = await db.ConfigResources.AsNoTracking().Where(r => r.Kind == ResourceKinds.Playbook).ToDictionaryAsync(r => r.Name, ct);
        await Send.OkAsync((await PlaybookCatalog.LoadAsync(db, profiles, ct)).Where(p => usable.Contains(p.Profile))
            .Select(p => new PlaybookDto(p.Name, p.Version, p.Profile, p.Description, p.Steps, p.Outputs,
                rows.TryGetValue(p.Name, out var r) ? r.ManagedBy : "api", rows.TryGetValue(p.Name, out var r2) ? r2.Spec : "")).ToList(), ct);
    }
}
