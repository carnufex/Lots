using FastEndpoints;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Features.Conversations;

namespace Lots.Shell.Features.Models;

public sealed record AliasDto(string Name, IReadOnlyList<ModelTarget> Targets, string? FastReasoningEffort, IReadOnlyList<string> UsedBy);

public sealed record ModelsDto(IReadOnlyList<ModelEndpointHealth> Endpoints, IReadOnlyList<AliasDto> Aliases);

/// <summary>Configured model endpoints with live health, and the aliases with their fallback chain and who uses them. Admins only.</summary>
public sealed class ListModelsEndpoint(ModelCatalog catalog, RoutingModelClient router, ProfileRegistry profiles, ICurrentPrincipal who, IConfiguration config)
    : EndpointWithoutRequest<ModelsDto>
{
    public override void Configure()
    {
        Get("/models");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!ConversationViews.IsAdmin(who.Get(HttpContext), config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var health = await router.HealthAsync(ct);
        var aliases = catalog.Aliases.Select(a => new AliasDto(a.Key, a.Value.Targets, a.Value.FastReasoningEffort,
            profiles.All.Where(p => catalog.Resolve(p.Model) == a.Key).Select(p => "profile:" + p.Name)
                .Concat(a.Key == ModelCatalog.Voice ? ["voice runs"] : []).ToList())).OrderBy(a => a.Name).ToList();
        await Send.OkAsync(new ModelsDto(health, aliases), ct);
    }
}
