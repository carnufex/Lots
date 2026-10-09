using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;

namespace Lots.Shell.Features.Config;

public sealed record OidcClientConfig(string Authority, string ClientId, string Scope, string RoleClaim, string? RolePrefix);

public sealed record ProfileInfo(string Name, string Description);

public sealed record VoiceConfig(bool Enabled, IReadOnlyList<string> Languages);

public sealed record ClientConfig(string AuthMode, OidcClientConfig? Oidc, IReadOnlyList<ProfileInfo> Profiles, VoiceConfig Voice);

/// <summary>What the browser app needs before it can log in. Contains no secrets and is public by design.</summary>
public sealed class ClientConfigEndpoint(IConfiguration config, ProfileRegistry profiles, Microsoft.Extensions.Options.IOptions<Lots.Shell.Core.Speech.SpeechOptions> speech) : EndpointWithoutRequest<ClientConfig>
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
                config["Auth:Oidc:Scope"] ?? "openid profile email",
                config["Auth:Oidc:RoleClaim"] ?? "roles",
                config["Auth:Oidc:RolePrefix"])
            : null;

        await Send.OkAsync(new ClientConfig(
            oidc is null ? "dev" : "oidc", oidc,
            profiles.All.Select(p => new ProfileInfo(p.Name, p.Description)).OrderBy(p => p.Name).ToList(),
            new VoiceConfig(speech.Value.Enabled, Lots.Shell.Core.Speech.SpeechOptions.Languages)), ct);
    }
}
