using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Tools;

namespace Lots.Shell.Tests.Features;

public class PolicyEngineTests
{
    private static readonly Profile Profile = TestProfiles.Registry(
        ("list", ToolRisk.Read), ("update", ToolRisk.Write), ("drop", ToolRisk.Destructive)).Find(TestProfiles.Name)!;

    private static Principal User(params string[] roles) => new("u1", roles);

    [Fact]
    public void Operator_may_read()
    {
        Assert.Equal(Decision.Allow, PolicyEngine.Decide(User("operator"), Profile, "list").Decision);
    }

    [Fact]
    public void Operator_may_not_write_or_destroy()
    {
        Assert.Equal(Decision.Deny, PolicyEngine.Decide(User("operator"), Profile, "update").Decision);
        Assert.Equal(Decision.Deny, PolicyEngine.Decide(User("operator"), Profile, "drop").Decision);
    }

    [Fact]
    public void Admin_writes_with_approval_but_reads_freely_and_cannot_destroy()
    {
        Assert.Equal(Decision.Allow, PolicyEngine.Decide(User("admin"), Profile, "list").Decision);
        Assert.Equal(Decision.RequireApproval, PolicyEngine.Decide(User("admin"), Profile, "update").Decision);
        Assert.Equal(Decision.Deny, PolicyEngine.Decide(User("admin"), Profile, "drop").Decision);
    }

    [Fact]
    public void Undeclared_tool_is_denied_even_for_admin()
    {
        var result = PolicyEngine.Decide(User("admin"), Profile, "mystery");

        Assert.Equal(Decision.Deny, result.Decision);
        Assert.Contains("not declared", result.Reason);
    }

    [Fact]
    public void User_without_matching_role_gets_nothing()
    {
        Assert.Equal(Decision.Deny, PolicyEngine.Decide(User(), Profile, "list").Decision);
        Assert.Equal(Decision.Deny, PolicyEngine.Decide(User("stranger"), Profile, "list").Decision);
    }

    [Fact]
    public void Role_names_match_case_insensitively()
    {
        Assert.Equal(Decision.Allow, PolicyEngine.Decide(User("Operator"), Profile, "list").Decision);
    }

    [Fact]
    public void The_most_permissive_role_wins_when_roles_combine()
    {
        var profile = new Profile("p", 1, "", "", [],
            [new ProfileTool("update", ToolRisk.Write)],
            [
                new ProfileRole("careful", [ToolRisk.Write], [ToolRisk.Write]),
                new ProfileRole("trusted", [ToolRisk.Write], []),
            ]);

        Assert.Equal(Decision.Allow, PolicyEngine.Decide(User("careful", "trusted"), profile, "update").Decision);
        Assert.Equal(Decision.RequireApproval, PolicyEngine.Decide(User("careful"), profile, "update").Decision);
    }

    [Fact]
    public void Different_roles_see_different_tools()
    {
        Assert.Equal(["list"], PolicyEngine.VisibleTools(User("operator"), Profile));
        Assert.Equal(["list", "update"], PolicyEngine.VisibleTools(User("admin"), Profile));
        Assert.Empty(PolicyEngine.VisibleTools(User(), Profile));
    }
}

public class ProfileParserTests
{
    private const string Valid = """
        name: demo
        version: 2
        description: Demo
        instructions: |
          Be helpful.
        servers:
          - name: s1
            url: http://s1:8080/mcp
        tools:
          - name: list
            risk: read
          - name: update
            risk: Write
        roles:
          - name: operator
            allow: [read]
          - name: admin
            allow: [read, write]
            requireApproval: [write]
        """;

    [Fact]
    public void Parses_a_valid_manifest()
    {
        var p = ProfileParser.Parse(Valid);

        Assert.Equal("demo", p.Name);
        Assert.Equal(2, p.Version);
        Assert.Equal("Be helpful.", p.Instructions);
        Assert.Equal(ToolRisk.Write, p.Tools.Single(t => t.Name == "update").Risk);
        Assert.Equal([ToolRisk.Write], p.Roles.Single(r => r.Name == "admin").RequireApproval);
        Assert.Equal("http://s1:8080/mcp", p.Servers.Single().Url);
    }

    [Fact]
    public void Reports_all_problems_at_once()
    {
        const string bad = """
            version: 0
            servers:
              - name: s1
                url: not-a-url
            tools:
              - name: a
                risk: sudo
              - name: b
                risk: read
              - name: b
                risk: read
            roles:
              - name: r
                allow: [read]
                requireApproval: [write]
            """;

        var ex = Assert.Throws<ProfileException>(() => ProfileParser.Parse(bad, "bad.yaml"));

        Assert.Contains(ex.Errors, e => e.Contains("name is required"));
        Assert.Contains(ex.Errors, e => e.Contains("version"));
        Assert.Contains(ex.Errors, e => e.Contains("absolute http(s) url"));
        Assert.Contains(ex.Errors, e => e.Contains("unknown risk 'sudo'"));
        Assert.Contains(ex.Errors, e => e.Contains("duplicate tool 'b'"));
        Assert.Contains(ex.Errors, e => e.Contains("requires approval for 'Write' but does not allow it"));
        Assert.All(ex.Errors, e => Assert.StartsWith("bad.yaml:", e));
    }

    [Fact]
    public void Directory_loading_fails_on_any_invalid_file_and_on_an_empty_directory()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        Assert.Throws<ProfileException>(() => ProfileRegistry.LoadDirectory(dir)); // empty

        File.WriteAllText(Path.Combine(dir, "ok.yaml"), Valid);
        Assert.Equal("demo", ProfileRegistry.LoadDirectory(dir).Find("DEMO")!.Name);

        File.WriteAllText(Path.Combine(dir, "broken.yaml"), "name: x\nversion: 1\ntools:\n  - name: t\n    risk: nope\n");
        var ex = Assert.Throws<ProfileException>(() => ProfileRegistry.LoadDirectory(dir));
        Assert.Contains(ex.Errors, e => e.StartsWith("broken.yaml:"));
    }

    [Fact]
    public void The_shipped_homelab_profile_is_valid()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "profiles");

        var registry = ProfileRegistry.LoadDirectory(Path.GetFullPath(path));

        var homelab = registry.Find("homelab")!;
        Assert.Equal(["get_container_logs", "list_containers"], homelab.Tools.Select(t => t.Name).Order());
        Assert.Equal(Decision.Allow, PolicyEngine.Decide(new Principal("u", ["operator"]), homelab, "list_containers").Decision);
    }
}
