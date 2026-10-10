using Lots.Shell.Core.Profiles;

namespace Lots.Shell.Tests.Features;

/// <summary>An unknown key in a policy-carrying resource is an error: a typo must never silently weaken policy.</summary>
public class StrictYamlTests
{
    private const string Profile = """
        kind: Profile
        name: t
        version: 1
        servers: [{ name: s, url: "http://s:8080/mcp" }]
        tools: [{ name: close_ticket, risk: write }]
        roles:
          - name: support
            allow: [read, write]
            {0}: [write]
        """;

    [Fact]
    public void A_misspelled_approval_key_rejects_the_profile_and_names_the_fix()
    {
        var ex = Assert.Throws<ProfileException>(() => ProfileParser.Parse(Profile.Replace("{0}", "requireAproval"), "t.yaml"));

        var error = Assert.Single(ex.Errors);
        Assert.Contains("unknown key 'requireAproval'", error);
        Assert.Contains("did you mean 'requireApproval'", error);
        Assert.Contains("line 9", error);
    }

    [Fact]
    public void The_correct_key_still_parses_and_requires_the_approval()
    {
        var p = ProfileParser.Parse(Profile.Replace("{0}", "requireApproval"));

        Assert.Contains(Lots.Shell.Core.Tools.ToolRisk.Write, p.Roles.Single().RequireApproval);
    }

    [Fact]
    public void Unknown_top_level_keys_are_rejected_with_the_valid_ones()
    {
        var ex = Assert.Throws<ProfileException>(() => ProfileParser.Parse("name: t\nversion: 1\nsecretBackdoor: true\n"));

        Assert.Contains("unknown key 'secretBackdoor'", Assert.Single(ex.Errors));
        Assert.Contains("valid here:", ex.Errors[0]);
    }

    [Fact]
    public void Schedules_are_strict_too()
    {
        var errors = new List<string>();
        var registry = TestProfiles.Registry();

        Lots.Shell.Core.Schedules.ScheduleParser.Parse("kind: Schedule\nname: s\nprofile: test\nprompt: hi\nidentity: { user: svc-x, role: [operator] }\ncron: \"0 7 * * *\"\n",
            "s", registry, errors);

        Assert.Contains(errors, e => e.Contains("unknown key 'role'") && e.Contains("did you mean 'roles'"));
    }
}
