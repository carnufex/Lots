using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Tools;

namespace Lots.Shell.Core.Policy;

/// <summary>Who a run acts for. Policy is evaluated against this, never against anything the model says.</summary>
public sealed record Principal(string UserId, IReadOnlyList<string> Roles);

public enum Decision { Allow, RequireApproval, Deny }

public sealed record PolicyResult(Decision Decision, string Reason);

/// <summary>
/// Per-call policy. Deny by default: a tool must be declared in the profile, and one of the principal's
/// roles must grant its risk class.
/// </summary>
public static class PolicyEngine
{
    public static PolicyResult Decide(Principal principal, Profile profile, string toolName)
    {
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
        return grants.Any(r => !r.RequireApproval.Contains(tool.Risk))
            ? new(Decision.Allow, "allowed")
            : new(Decision.RequireApproval, $"{tool.Risk} tools need approval");
    }

    /// <summary>True if one of the principal's roles may approve calls of this tool's risk class.</summary>
    public static bool CanApprove(Principal principal, Profile profile, string toolName)
    {
        var tool = profile.Tools.FirstOrDefault(t => string.Equals(t.Name, toolName, StringComparison.Ordinal));
        return tool is not null && profile.Roles
            .Where(r => principal.Roles.Contains(r.Name, StringComparer.OrdinalIgnoreCase))
            .Any(r => r.MayApprove.Contains(tool.Risk));
    }

    /// <summary>The tools a principal may see at all (allowed or allowed-with-approval).</summary>
    public static IReadOnlyList<string> VisibleTools(Principal principal, Profile profile) =>
        profile.Tools.Where(t => Decide(principal, profile, t.Name).Decision != Decision.Deny).Select(t => t.Name).ToList();
}
