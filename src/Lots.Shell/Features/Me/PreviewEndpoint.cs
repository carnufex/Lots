using FastEndpoints;
using Lots.Shell.Core.Policy;

namespace Lots.Shell.Features.Me;

public sealed record StartPreviewRequest(List<string> Roles, int Minutes = 30, bool AllowWrites = false);

public sealed record PreviewDto(string Token, IReadOnlyList<string> Roles, DateTimeOffset Expires, bool AllowWrites, string Header);

/// <summary>
/// "View as" (#156): an admin gets a short-lived token that makes their requests carry other roles. The preview only ever narrows: every
/// tool call is decided for the previewed and the real roles, write-class tools are refused unless asked for, and approving is off.
/// Leaving the preview is dropping the header; the token expires by itself after at most an hour.
/// </summary>
public sealed class StartPreviewEndpoint(ICurrentPrincipal who, PreviewTokens tokens, IConfiguration config) : Endpoint<StartPreviewRequest, PreviewDto>
{
    public override void Configure() => Post("/me/preview");

    public override async Task HandleAsync(StartPreviewRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        // Starting a new preview from inside one uses the real roles, never the previewed ones.
        var real = me.Preview is { } p ? new Principal(me.UserId, p.RealRoles) : me;
        if (!PreviewTokens.IsAdmin(real, config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var roles = (req.Roles ?? []).Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (roles.Count is 0 or > 20 || roles.Any(r => r.Length > 64))
        {
            AddError(x => x.Roles, "Give 1-20 roles of at most 64 characters.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        var (token, expires) = tokens.Issue(real, roles, req.AllowWrites, req.Minutes);
        await Send.OkAsync(new PreviewDto(token, roles, expires, req.AllowWrites, ClaimsCurrentPrincipal.PreviewHeader), ct);
    }
}
