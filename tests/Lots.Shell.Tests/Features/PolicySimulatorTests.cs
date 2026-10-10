using System.Net;
using System.Net.Http.Json;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Tools;
using Lots.Shell.Features.Admin;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

public class PolicyTestsAsCode
{
    private const string Base = "name: p\nversion: 1\ntools:\n  - { name: read_it, risk: read }\n  - { name: write_it, risk: write }\n" +
                                "roles:\n  - { name: operator, allow: [read] }\n  - { name: admin, allow: [read, write], requireApproval: [write], approve: [write] }\n";

    [Fact]
    public void Passing_policy_tests_load_and_failing_ones_reject_the_profile()
    {
        var ok = ProfileParser.Parse(Base + "policyTests:\n  - { roles: [operator], tool: read_it, expect: allow }\n  - { roles: [admin], tool: write_it, expect: approval }\n  - { roles: [operator], tool: write_it, expect: deny }\n");
        Assert.All(PolicyTests.Run(ok), r => Assert.True(r.Passed));

        var ex = Assert.Throws<ProfileException>(() => ProfileParser.Parse(Base + "policyTests:\n  - { roles: [operator], tool: write_it, expect: allow }\n"));
        Assert.Contains("expected allow, got Deny", Assert.Single(ex.Errors));
        Assert.Throws<ProfileException>(() => ProfileParser.Parse(Base + "policyTests:\n  - { roles: [operator], tool: read_it, expect: maybe }\n"));
    }

    [Fact]
    public void Decisions_name_the_rule_that_made_them()
    {
        var p = ProfileParser.Parse(Base);
        Assert.Equal("role 'operator' allows Read tools", PolicyEngine.Decide(new Principal("u", ["operator"]), p, "read_it").Rule);
        Assert.Equal("role 'admin' allow Write tools only with approval", PolicyEngine.Decide(new Principal("u", ["admin"]), p, "write_it").Rule);
        Assert.Null(PolicyEngine.Decide(new Principal("u", ["admin"]), p, "nope").Rule);
    }
}

public class PolicySimulatorApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PolicySimulatorApiTests(WebApplicationFactory<Program> factory)
    {
        var dbName = Guid.NewGuid().ToString();
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false", ["Auth:Mode"] = "Dev", ["ConnectionStrings:Lots"] = "Host=none", ["Auth:Dev:AllowHeaders"] = "true",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(dbName));
                s.AddSingleton(TestProfiles.Registry(("list", ToolRisk.Read), ("restart", ToolRisk.Write)));
            });
        });
    }

    private async Task<HttpResponseMessage> Simulate(string roles, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/admin/v1/policy/simulate") { Content = JsonContent.Create(body) };
        req.Headers.Add("X-Dev-User", "eve");
        req.Headers.Add("X-Dev-Roles", roles);
        return await _factory.CreateClient().SendAsync(req);
    }

    [Fact]
    public async Task Auditors_simulate_decisions_with_the_rule_and_operators_cannot()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Simulate("operator", new { profile = TestProfiles.Name, roles = new[] { "operator" }, tool = "list" })).StatusCode);

        var write = (await (await Simulate("auditor", new { profile = TestProfiles.Name, roles = new[] { "admin" }, tool = "restart" })).Content.ReadFromJsonAsync<SimulateResponse>())!;
        Assert.Equal(("RequireApproval", true), (write.Decision, write.CanApprove));
        Assert.Equal(["admin"], write.ApproverRoles);
        Assert.Equal(["list", "restart"], write.VisibleTools);

        var denied = (await (await Simulate("auditor", new { profile = TestProfiles.Name, roles = new[] { "operator" }, tool = "restart" })).Content.ReadFromJsonAsync<SimulateResponse>())!;
        Assert.Equal("Deny", denied.Decision);
        Assert.Contains("grants Write", denied.Reason);
    }
}
