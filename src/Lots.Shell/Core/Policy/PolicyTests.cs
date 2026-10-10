using Lots.Shell.Core.Profiles;

namespace Lots.Shell.Core.Policy;

public sealed record PolicyTestResult(PolicyTest Test, bool Passed, Decision Actual, string Description);

/// <summary>Runs a profile's policy tests (#70) against the same engine every tool call goes through.</summary>
public static class PolicyTests
{
    public static IEnumerable<PolicyTestResult> Run(Profile profile)
    {
        foreach (var t in profile.PolicyTests ?? [])
        {
            var actual = PolicyEngine.Decide(new Principal("policy-test", t.Roles.ToArray()), profile, t.Tool).Decision;
            var expected = t.Expect switch { "allow" => Decision.Allow, "approval" => Decision.RequireApproval, _ => Decision.Deny };
            yield return new PolicyTestResult(t, actual == expected, actual,
                $"roles [{string.Join(", ", t.Roles)}] calling {t.Tool}: expected {t.Expect}, got {actual}");
        }
    }
}
