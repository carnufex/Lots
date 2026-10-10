using FastEndpoints;
using Lots.Shell.Core.Mcp;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Features.Conversations;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Integrations;

public sealed record ConnectionDto(DateTimeOffset ConnectedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt, string? Scope);

/// <summary>
/// One server's credentials: what kind, where its secrets come from (references, never values), live status for service accounts
/// (admins only) and, for user-connected servers, the caller's own connection.
/// </summary>
public sealed record CredentialDto(string Server, string Profiles, string Auth, string? Type, IReadOnlyList<string> SecretReferences,
    CredentialStatus? Status, bool UserConnected, ConnectionDto? MyConnection);

public sealed class ListCredentialsEndpoint(ProfileRegistry profiles, CredentialStatusRegistry status, LotsDbContext db, ICurrentPrincipal who, IConfiguration config)
    : EndpointWithoutRequest<List<CredentialDto>>
{
    public override void Configure() => Get("/integrations/credentials");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var admin = ConversationViews.IsAdmin(me, config);
        var mine = await db.UserCredentials.AsNoTracking().Where(c => c.UserId == me.UserId).ToListAsync(ct);
        var list = new List<CredentialDto>();
        foreach (var s in profiles.Servers)
        {
            var userConnected = s.Auth == AuthStrategies.UserConnected;
            // End users see the servers they connect themselves; how service accounts work is for admins (principle 3).
            if (!admin && !userConnected) continue;
            if (!admin && !profiles.All.Any(p => p.Servers.Any(x => x.Name == s.Name) && PolicyEngine.VisibleTools(me, p).Count > 0)) continue;
            var c = s.Credentials;
            var refs = new[] { c?.TokenEnv, c?.PasswordEnv, c?.ClientSecretEnv }.Where(r => !string.IsNullOrEmpty(r)).Select(r => SecretReference.Describe(r!)).ToList();
            var connection = mine.FirstOrDefault(m => m.Server == s.Name);
            list.Add(new CredentialDto(s.Name, string.Join(", ", profiles.All.Where(p => p.Servers.Any(x => x.Name == s.Name)).Select(p => p.Name)),
                s.Auth, c?.Type, admin ? refs : [], admin && !userConnected ? status.Get(s.Name) : null, userConnected,
                connection is null ? null : new ConnectionDto(connection.ConnectedAt, connection.ExpiresAt, connection.LastUsedAt, connection.Scope)));
        }
        await Send.OkAsync(list, ct);
    }
}

public sealed record ConnectRequest(string Server);

public sealed record ConnectResponse(string AuthorizeUrl);

/// <summary>Starts connecting the caller's own account at a user-connected server: returns the provider URL to open.</summary>
public sealed class StartConnectionEndpoint(UserConnections connections, ICurrentPrincipal who, IConfiguration config) : Endpoint<ConnectRequest, ConnectResponse>
{
    public override void Configure() => Post("/integrations/connections/{Server}");

    public override async Task HandleAsync(ConnectRequest req, CancellationToken ct)
    {
        try
        {
            var redirect = CallbackUrl(HttpContext, config);
            await Send.OkAsync(new ConnectResponse(connections.Start(who.Get(HttpContext).UserId, req.Server, redirect)), ct);
        }
        catch (InvalidOperationException ex)
        {
            AddError(ex.Message);
            await Send.ErrorsAsync(404, ct);
        }
    }

    /// <summary>The callback the provider redirects to; register exactly this as the client's redirect URI.</summary>
    public static string CallbackUrl(HttpContext http, IConfiguration config) =>
        (config["PublicUrl"] is { Length: > 0 } url ? url.TrimEnd('/') : $"{http.Request.Scheme}://{http.Request.Host}") + "/integrations/connections/callback";
}

public sealed record CallbackRequest(string? Code, string? State, string? Error);

/// <summary>
/// Where the provider sends the browser back. Anonymous by necessity (a top-level redirect carries no bearer token); the encrypted,
/// expiring state is what proves who started the connection.
/// </summary>
public sealed class ConnectionCallbackEndpoint(UserConnections connections, ILogger<ConnectionCallbackEndpoint> logger) : Endpoint<CallbackRequest>
{
    public override void Configure()
    {
        Get("/integrations/connections/callback");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CallbackRequest req, CancellationToken ct)
    {
        string target;
        if (!string.IsNullOrEmpty(req.Error) || string.IsNullOrEmpty(req.Code) || string.IsNullOrEmpty(req.State))
            target = "/#/integrations/credentials?error=" + Uri.EscapeDataString(req.Error ?? "missing code");
        else
            try
            {
                var server = await connections.CompleteAsync(req.Code, req.State, ct);
                target = "/#/integrations/credentials?connected=" + Uri.EscapeDataString(server);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                logger.LogWarning("Connecting an account failed: {Error}", ex.Message);
                target = "/#/integrations/credentials?error=" + Uri.EscapeDataString(ex.Message);
            }
        await Send.RedirectAsync(target, allowRemoteRedirects: false);
    }
}

public sealed class DisconnectEndpoint(UserConnections connections, ICurrentPrincipal who) : Endpoint<ConnectRequest>
{
    public override void Configure() => Delete("/integrations/connections/{Server}");

    public override async Task HandleAsync(ConnectRequest req, CancellationToken ct)
    {
        if (await connections.DisconnectAsync(who.Get(HttpContext).UserId, req.Server, ct)) await Send.NoContentAsync(ct);
        else await Send.NotFoundAsync(ct);
    }
}
