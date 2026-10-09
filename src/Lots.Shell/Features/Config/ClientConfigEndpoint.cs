using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;

namespace Lots.Shell.Features.Config;

public sealed record OidcClientConfig(string Authority, string ClientId, string Scope);

public sealed record ProfileInfo(string Name, string Description);

public sealed record ClientConfig(string AuthMode, OidcClientConfig? Oidc, IReadOnlyList<ProfileInfo> Profiles);

/// <summary>What the browser app needs before it can log in. Contains no secrets and is public by design.</summary>
public sealed class ClientConfigEndpoint(IConfiguration config, ProfileRegistry profiles) : EndpointWithoutRequest<ClientConfig>
{
    public override void Configure()
    {
        Get("/config");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var oidc = AuthSetup.IsOidc(config)
            ? new OidcClientConfig(
                config["Auth:Oidc:Authority"]!,
                config["Auth:Oidc:ClientId"] ?? "lots",
                config["Auth:Oidc:Scope"] ?? "openid profile email")
            : null;

        await Send.OkAsync(new ClientConfig(
            oidc is null ? "dev" : "oidc", oidc,
            profiles.All.Select(p => new ProfileInfo(p.Name, p.Description)).OrderBy(p => p.Name).ToList()), ct);
    }
}
