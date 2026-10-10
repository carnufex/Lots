using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;

namespace Lots.Shell.Features.Me;

/// <param name="Pages">The pages the caller may open (route names of the web UI).</param>
/// <param name="Contexts">The profiles (contexts) the caller can use: those where one of their roles grants something.</param>
/// <param name="Preview">Set while viewing as other roles (#156): the previewed roles, the real ones and when it ends.</param>
/// <param name="CanPreview">The caller is an admin by their real roles and may start a role preview.</param>
public sealed record CapabilitiesDto(IReadOnlyList<string> Pages, IReadOnlyList<string> Contexts, bool Admin, bool Audit, bool Reviewer, bool Insights, bool Approve,
    PreviewState? Preview = null, bool CanPreview = false, IReadOnlyList<string>? Roles = null, IReadOnlyList<string>? KnownRoles = null);

public sealed record PreviewState(IReadOnlyList<string> Roles, IReadOnlyList<string> RealRoles, DateTimeOffset Expires, bool AllowWrites);

/// <summary>
/// What the caller may use (#157), from the same checks the endpoints themselves make, so menu and access cannot drift apart. Hiding is
/// only UX: every endpoint keeps its own authorisation.
/// </summary>
public static class Capabilities
{
    /// <summary>Pages for every signed-in user; inside them, admin-only tabs check their own rights (e.g. Integrations: servers).</summary>
    public static readonly IReadOnlyList<string> Everyone = ["chat", "history", "runs", "usage", "voice", "knowledge", "integrations", "transcription"];

    public static CapabilitiesDto For(Principal me, IConfiguration config, ProfileRegistry profiles)
    {
        var admin = Conversations.ConversationViews.IsAdmin(me, config);
        var audit = Audit.AuditFilter.Allowed(me, config);
        var reviewer = Feedback.FeedbackViews.IsReviewer(me, config);
        var insights = Insights.InsightsAccess.Allowed(me, config);
        // Nobody approves while previewing (#156), so the approvals page goes too.
        var approve = me.Preview is null
            && profiles.All.Any(p => p.Roles.Any(r => r.MayApprove.Count > 0 && me.Roles.Contains(r.Name, StringComparer.OrdinalIgnoreCase)));
        var pages = new List<string>(Everyone);
        if (approve || admin) pages.Add("approvals");
        if (audit) pages.Add("audit");
        if (admin) pages.AddRange(["models", "profiles", "policy", "identity"]);
        if (reviewer) pages.Add("feedback");
        if (insights) pages.Add("insights");
        var contexts = profiles.All
            .Where(p => p.Roles.Any(r => me.Roles.Contains(r.Name, StringComparer.OrdinalIgnoreCase) && r.Allow.Count > 0))
            .Select(p => p.Name).Order().ToList();
        var real = me.Preview is { } p ? new Principal(me.UserId, p.RealRoles) : me;
        return new CapabilitiesDto(pages, contexts, admin, audit, reviewer, insights, approve,
            me.Preview is { } pv ? new PreviewState(me.Roles, pv.RealRoles, pv.Expires, pv.AllowWrites) : null,
            PreviewTokens.IsAdmin(real, config), me.Roles, PreviewTokens.IsAdmin(real, config) ? KnownRoles(config, profiles) : null);
    }

    /// <summary>Every role something in this deployment checks: profile roles and the configured shell roles. For pickers, not for policy.</summary>
    public static IReadOnlyList<string> KnownRoles(IConfiguration config, ProfileRegistry profiles)
    {
        static IEnumerable<string> Csv(string? s) => (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return profiles.All.SelectMany(p => p.Roles.Select(r => r.Name))
            .Concat(Csv(config["Auth:AdminRoles"] ?? "admin")).Concat(Csv(config["Auth:AuditRoles"] ?? "admin,auditor"))
            .Concat(config.GetSection("Insights:Roles").Get<string[]>() ?? ["self-improve"])
            .Concat(config.GetSection("Models:ChooseRoles").Get<string[]>() ?? ["evaluator"])
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public sealed class CapabilitiesEndpoint(ICurrentPrincipal who, IConfiguration config, ProfileRegistry profiles) : EndpointWithoutRequest<CapabilitiesDto>
{
    public override void Configure() => Get("/me/capabilities");

    public override Task HandleAsync(CancellationToken ct) => Send.OkAsync(Capabilities.For(who.Get(HttpContext), config, profiles), ct);
}
