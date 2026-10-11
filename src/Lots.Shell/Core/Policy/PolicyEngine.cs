using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Tools;

namespace Lots.Shell.Core.Policy;

/// <summary>Who a run acts for. Policy is evaluated against this, never against anything the model says.</summary>
public sealed record Principal(string UserId, IReadOnlyList<string> Roles)
{
    /// <summary>Set while an admin views Lots as other roles (#156): <see cref="Roles"/> are then the previewed ones.</summary>
    public PreviewInfo? Preview { get; init; }

    /// <summary>Only read-class tools for this principal, whatever the roles grant: multi-context fan-out (#151) and its supervisor.</summary>
    public bool ReadOnly { get; init; }
}

/// <summary>
/// A role preview (#156): the admin's real roles, whether write-class tools may run, and when it ends. Rights are the intersection of the
/// previewed and the real roles: a preview can never grant more than the admin has.
/// </summary>
public sealed record PreviewInfo(IReadOnlyList<string> RealRoles, bool AllowWrites, DateTimeOffset Expires);

public enum Decision { Allow, RequireApproval, Deny }

/// <param name="Rule">The rule that decided, for explanations ("role operator allows read"); null for undeclared tools.</param>
public sealed record PolicyResult(Decision Decision, string Reason, string? Rule = null);

/// <summary>
/// Per-call policy. Deny by default: a tool must be declared in the profile, and one of the principal's
/// roles must grant its risk class.
/// </summary>
public static class PolicyEngine
{
    public static PolicyResult Decide(Principal principal, Profile profile, string toolName)
    {
        if (principal.ReadOnly)
        {
            var d = Decide(principal with { ReadOnly = false }, profile, toolName);
            var risk = profile.Tools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal))?.Risk;
            return d.Decision != Decision.Deny && risk != ToolRisk.Read
                ? new(Decision.Deny, $"denied: this run may only read ({risk} tool)", "read-only run (#151)")
                : d;
        }
        if (principal.Preview is { } preview)
        {
            // Both the previewed roles and the real ones must allow the call; the stricter decision wins (never an elevation).
            var asPreviewed = Decide(principal with { Preview = null }, profile, toolName);
            var asActor = Decide(new Principal(principal.UserId, preview.RealRoles), profile, toolName);
            if (asPreviewed.Decision == Decision.Deny) return asPreviewed with { Reason = "preview: " + asPreviewed.Reason };
            if (asActor.Decision == Decision.Deny) return asActor with { Reason = "preview: your own roles do not allow it: " + asActor.Reason };
            var risk = profile.Tools.First(t => string.Equals(t.Name, toolName, StringComparison.Ordinal)).Risk;
            if (!preview.AllowWrites && risk != ToolRisk.Read)
                return new(Decision.Deny, $"preview: {risk} tools are blocked while viewing as other roles", "role preview (#156)");
            return asPreviewed.Decision == Decision.RequireApproval || asActor.Decision == Decision.RequireApproval
                ? (asPreviewed.Decision == Decision.RequireApproval ? asPreviewed : asActor)
                : asPreviewed;
        }

        var tool = profile.Tools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
        if (tool is null)
            return new(Decision.Deny, $"tool '{toolName}' is not declared in profile '{profile.Name}'");

        var grants = profile.Roles
            .Where(r => principal.Roles.Contains(r.Name, StringComparer.OrdinalIgnoreCase))
            .Where(r => r.Allow.Contains(tool.Risk))
            .ToList();

        if (grants.Count == 0)
            return new(Decision.Deny, $"no role of '{principal.UserId}' grants {tool.Risk} tools in profile '{profile.Name}'");

        // The most permissive applicable role wins: if any role allows the call without approval, it is allowed.
        var free = grants.FirstOrDefault(r => !r.RequireApproval.Contains(tool.Risk));
        return free is not null
            ? new(Decision.Allow, "allowed", $"role '{free.Name}' allows {tool.Risk} tools")
            : new(Decision.RequireApproval, $"{tool.Risk} tools need approval",
                $"role{(grants.Count > 1 ? "s" : "")} {string.Join(", ", grants.Select(g => $"'{g.Name}'"))} allow {tool.Risk} tools only with approval");
    }

    /// <summary>True if one of the principal's roles may approve calls of this tool's risk class.</summary>
    public static bool CanApprove(Principal principal, Profile profile, string toolName)
    {
        if (principal.Preview is not null) return false; // nobody approves anything while viewing as other roles (#156)
        var tool = profile.Tools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
        return tool is not null && profile.Roles
            .Where(r => principal.Roles.Contains(r.Name, StringComparer.OrdinalIgnoreCase))
            .Any(r => r.MayApprove.Contains(tool.Risk));
    }

    /// <summary>The tools a principal may see at all (allowed or allowed-with-approval).</summary>
    /// <remarks>In a role preview the previewed tool list is shown even when writes are blocked: the calls are refused at the choke point.</remarks>
    public static IReadOnlyList<string> VisibleTools(Principal principal, Profile profile)
    {
        var seen = principal.Preview is { AllowWrites: false } p ? principal with { Preview = p with { AllowWrites = true } } : principal;
        return profile.Tools.Where(t => Decide(seen, profile, t.Name).Decision != Decision.Deny).Select(t => t.Name).ToList();
    }
}
