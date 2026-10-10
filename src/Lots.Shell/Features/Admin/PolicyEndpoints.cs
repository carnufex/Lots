using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Tools;
using Lots.Shell.Features.Integrations;

namespace Lots.Shell.Features.Admin;

public sealed record SimulateRequest(string Profile, List<string>? Roles, string Tool, string? User = null);

public sealed record SimulateResponse(
    string Decision, string Reason, string? Rule, bool CanApprove, IReadOnlyList<string> ApproverRoles, IReadOnlyList<string> VisibleTools);

/// <summary>
/// "Why would this be allowed or denied?" (#70): the decision the policy engine makes for these roles, profile and tool, the rule
/// behind it, and what else those roles would see. Same engine as every real call. Admins and auditors.
/// </summary>
public sealed class SimulatePolicyEndpoint(ProfileRegistry profiles, IConfiguration config, ICurrentPrincipal who) : Endpoint<SimulateRequest, SimulateResponse>
{
    public override void Configure() => Post("/admin/v1/policy/simulate");

    public override async Task HandleAsync(SimulateRequest req, CancellationToken ct)
    {
        if (!IntegrationAccess.IsAuditor(who.Get(HttpContext), config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        if (profiles.Find(req.Profile) is not { } profile)
        {
            AddError(x => x.Profile, $"Unknown profile '{req.Profile}'.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        var principal = new Principal(req.User ?? "simulated-user", (req.Roles ?? []).ToArray());
        var result = PolicyEngine.Decide(principal, profile, req.Tool);
        var risk = profile.Tools.FirstOrDefault(t => t.Name == req.Tool)?.Risk;
        await Send.OkAsync(new SimulateResponse(result.Decision.ToString(), result.Reason, result.Rule, PolicyEngine.CanApprove(principal, profile, req.Tool),
            risk is { } r ? profile.Roles.Where(x => x.MayApprove.Contains(r)).Select(x => x.Name).ToList() : [],
            PolicyEngine.VisibleTools(principal, profile)), ct);
    }
}

public sealed record RoleGrant(string Role, IReadOnlyList<string> Allow, IReadOnlyList<string> RequireApproval, IReadOnlyList<string> Approve);

public sealed record ToolRiskDto(string Name, string Risk);

public sealed record ProfilePolicy(string Profile, int Version, string ManagedBy, IReadOnlyList<RoleGrant> Roles, IReadOnlyList<ToolRiskDto> Tools,
    IReadOnlyList<PolicyTestResult> Tests);

/// <summary>Role grants and approval rules of every profile in one view, with the result of each profile's policy tests.</summary>
public sealed class PolicyOverviewEndpoint(ProfileRegistry profiles, IConfiguration config, ICurrentPrincipal who) : EndpointWithoutRequest<List<ProfilePolicy>>
{
    public override void Configure() => Get("/admin/v1/policy");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!IntegrationAccess.IsAuditor(who.Get(HttpContext), config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        static List<string> S(IEnumerable<ToolRisk> r) => r.Select(x => x.ToString()).ToList();
        await Send.OkAsync(profiles.All.OrderBy(p => p.Name).Select(p => new ProfilePolicy(p.Name, p.Version, profiles.ManagedBy(p.Name) ?? "file",
            p.Roles.Select(r => new RoleGrant(r.Name, S(r.Allow), S(r.RequireApproval), S(r.MayApprove))).ToList(),
            p.Tools.Select(t => new ToolRiskDto(t.Name, t.Risk.ToString())).ToList(),
            PolicyTests.Run(p).ToList())).ToList(), ct);
    }
}
