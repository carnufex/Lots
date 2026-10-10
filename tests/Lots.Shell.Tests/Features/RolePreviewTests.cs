using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Runs;
using Lots.Shell.Core.Tools;
using Lots.Shell.Features.Admin;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Lots.Shell.Tests.Features;

/// <summary>#156: "View as" narrows an admin's rights to other roles; it can never widen them, and writes and approvals are off by default.</summary>
public class RolePreviewPolicyTests
{
    private static readonly Profile P = ProfileParser.Parse(
        "name: p\nversion: 1\ntools:\n  - { name: read_it, risk: read }\n  - { name: write_it, risk: write }\n  - { name: wipe, risk: destructive }\n" +
        "roles:\n  - { name: operator, allow: [read] }\n  - { name: admin, allow: [read, write], requireApproval: [write], approve: [write] }\n" +
        "  - { name: root, allow: [read, write, destructive], approve: [write, destructive] }\n");

    private static Principal As(string[] roles, string[] real, bool writes = false) =>
        new("u", roles) { Preview = new PreviewInfo(real, writes, DateTimeOffset.MaxValue) };

    [Fact]
    public void A_preview_never_grants_more_than_the_admin_has()
    {
        // root may wipe, but the admin previewing as root may not: both role sets must allow the call.
        Assert.Equal(Decision.Allow, PolicyEngine.Decide(new Principal("u", ["root"]), P, "wipe").Decision);
        var d = PolicyEngine.Decide(As(["root"], ["admin"], writes: true), P, "wipe");
        Assert.Equal(Decision.Deny, d.Decision);
        Assert.Contains("your own roles", d.Reason);
    }

    [Fact]
    public void Previewed_roles_restrict_and_writes_are_blocked_unless_asked_for()
    {
        Assert.Equal(Decision.Allow, PolicyEngine.Decide(As(["operator"], ["admin"]), P, "read_it").Decision);
        Assert.Equal(Decision.Deny, PolicyEngine.Decide(As(["operator"], ["admin"], writes: true), P, "write_it").Decision);

        var blocked = PolicyEngine.Decide(As(["admin"], ["admin"]), P, "write_it");
        Assert.Equal(Decision.Deny, blocked.Decision);
        Assert.Contains("blocked while viewing", blocked.Reason);
        // With writes allowed the stricter decision of the two role sets still applies: root allows freely, admin needs approval.
        Assert.Equal(Decision.RequireApproval, PolicyEngine.Decide(As(["root"], ["admin", "root"], writes: true) with { Roles = ["admin"] }, P, "write_it").Decision);
        Assert.Equal(Decision.RequireApproval, PolicyEngine.Decide(As(["root"], ["admin"], writes: true), P, "write_it").Decision);
    }

    [Fact]
    public void Nobody_approves_in_a_preview_but_the_previewed_tool_list_is_shown()
    {
        Assert.True(PolicyEngine.CanApprove(new Principal("u", ["admin"]), P, "write_it"));
        Assert.False(PolicyEngine.CanApprove(As(["admin"], ["admin"]), P, "write_it"));
        Assert.Equal(["read_it"], PolicyEngine.VisibleTools(As(["operator"], ["admin"]), P));
        Assert.Equal(["read_it", "write_it"], PolicyEngine.VisibleTools(As(["admin"], ["admin"]), P));
    }

    [Fact]
    public void The_matrix_is_exactly_what_the_engine_decides_per_role()
    {
        var m = PolicyMatrixEndpoint.Build(P);
        foreach (var role in m.Roles)
        foreach (var tool in P.Tools)
            Assert.Equal(PolicyEngine.Decide(new Principal("x", [role]), P, tool.Name).Decision.ToString(), m.Cells[role][tool.Name].Decision);
        Assert.Equal("Deny", m.Cells["operator"]["write_it"].Decision);
        Assert.Equal("RequireApproval", m.Cells["admin"]["write_it"].Decision);
    }
}

public class PreviewTokenTests
{
    private static readonly IConfiguration Config = new ConfigurationBuilder().Build();

    [Fact]
    public void Tokens_are_bound_to_the_user_expire_and_need_a_real_admin()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var tokens = new PreviewTokens(new EphemeralDataProtectionProvider(), clock);
        var admin = new Principal("ada", ["admin"]);
        var (token, _) = tokens.Issue(admin, ["operator"], false, 10);

        var p = tokens.Read(token, admin, Config)!;
        Assert.Equal(["operator"], p.Roles);
        Assert.Equal(["admin"], p.Preview!.RealRoles);

        Assert.Null(tokens.Read(token, new Principal("bob", ["admin"]), Config)); // someone else's token
        Assert.Null(tokens.Read(token, new Principal("ada", ["operator"]), Config)); // no longer an admin
        Assert.Null(tokens.Read(token + "x", admin, Config)); // tampered
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Null(tokens.Read(token, admin, Config)); // expired
    }

    [Fact]
    public void A_preview_lasts_at_most_an_hour()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (_, expires) = new PreviewTokens(new EphemeralDataProtectionProvider(), clock).Issue(new Principal("ada", ["admin"]), ["operator"], false, 600);
        Assert.Equal(clock.GetUtcNow().AddMinutes(PreviewTokens.MaxMinutes), expires);
    }
}

public class RolePreviewApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _app;

    public RolePreviewApiTests(WebApplicationFactory<Program> factory)
    {
        var db = Guid.NewGuid().ToString();
        _app = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false", ["ConnectionStrings:Lots"] = "Host=none", ["Auth:Mode"] = "Dev", ["Auth:Dev:AllowHeaders"] = "true",
                ["Outcomes:Enabled"] = "false", ["Mining:Enabled"] = "false",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(db));
                s.AddSingleton(TestProfiles.Registry(("list", ToolRisk.Read), ("restart", ToolRisk.Write)));
            });
        });
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, string roles, object? body = null, string? preview = null)
    {
        using var req = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("X-Dev-User", "claude-test-preview");
        req.Headers.Add("X-Dev-Roles", roles);
        if (preview is not null) req.Headers.Add(ClaimsCurrentPrincipal.PreviewHeader, preview);
        return await _app.CreateClient().SendAsync(req);
    }

    [Fact]
    public async Task Only_admins_start_a_preview()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Post, "/me/preview", "operator", new { roles = new[] { "admin" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/me/preview", "admin", new { roles = Array.Empty<string>() })).StatusCode);
    }

    [Fact]
    public async Task In_a_preview_the_menu_shrinks_and_admin_endpoints_refuse()
    {
        var start = await Send(HttpMethod.Post, "/me/preview", "admin", new { roles = new[] { "operator" }, minutes = 5 });
        var token = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        var caps = await (await Send(HttpMethod.Get, "/me/capabilities", "admin", preview: token)).Content.ReadFromJsonAsync<JsonElement>();
        var pages = caps.GetProperty("pages").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.DoesNotContain("policy", pages);
        Assert.DoesNotContain("approvals", pages);
        Assert.True(caps.GetProperty("canPreview").GetBoolean());
        Assert.Equal("operator", caps.GetProperty("preview").GetProperty("roles")[0].GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "/admin/v1/identity", "admin", preview: token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/admin/v1/identity", "admin")).StatusCode);
        // A token only works for the admin who started it.
        var other = await (await Send(HttpMethod.Get, "/me/capabilities", "operator", preview: token)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, other.GetProperty("preview").ValueKind);
    }

    [Fact]
    public async Task The_matrix_is_for_admins_and_auditors()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, $"/admin/v1/policy/matrix?profile={TestProfiles.Name}", "operator")).StatusCode);
        var m = await (await Send(HttpMethod.Get, $"/admin/v1/policy/matrix?profile={TestProfiles.Name}", "auditor")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Allow", m.GetProperty("cells").GetProperty("operator").GetProperty("list").GetProperty("decision").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/admin/v1/policy/matrix?profile=nope", "admin")).StatusCode);
    }
}

public class RolePreviewRunTests
{
    private sealed class Model(params Func<IReadOnlyList<ChatMessage>, ChatMessage>[] replies) : IModelClient
    {
        private int _calls;

        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct) =>
            Task.FromResult(new ModelResponse(replies[_calls++](messages), "stop", new ModelUsage(1, 1), TimeSpan.FromMilliseconds(1)));
    }

    private sealed class Tools : IToolSource
    {
        public List<string> Called { get; } = [];

        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>([new ToolDescriptor("restart", "restart", JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone())]);

        public Task<string?> ServerOfAsync(string toolName, CancellationToken ct) => Task.FromResult<string?>("sample");

        public Task<string> CallAsync(string name, string argumentsJson, CancellationToken ct)
        {
            Called.Add(name);
            return Task.FromResult("ok");
        }
    }

    [Fact]
    public async Task A_write_in_a_preview_run_is_refused_and_audited_as_a_preview()
    {
        var db = new LotsDbContext(new DbContextOptionsBuilder<LotsDbContext>().UseInMemoryDatabase(nameof(A_write_in_a_preview_run_is_refused_and_audited_as_a_preview)).Options);
        var registry = TestProfiles.Registry(("restart", ToolRisk.Write));
        var tools = new Tools();
        var run = new RunRecord
        {
            Id = Guid.NewGuid(), Prompt = "restart it", CreatedAt = DateTimeOffset.UtcNow, Profile = TestProfiles.Name,
            UserId = "claude-test-preview", Roles = "admin", PreviewRealRoles = "admin",
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        var model = new Model(_ => new ChatMessage("assistant", null, [new ToolCall("c1", "restart", "{}")]), _ => new ChatMessage("assistant", "refused"));

        await new AgentRunner(db, model, new ToolInvoker([tools], registry), registry, Options.Create(new AgentOptions()), TimeProvider.System)
            .ExecuteAsync(run.Id, default);

        Assert.Empty(tools.Called);
        Assert.Equal(RunStatus.Completed, (await db.Runs.SingleAsync()).Status); // denied, not paused for an approval
        var audit = await db.AuditLog.SingleAsync();
        Assert.Equal(AuditDecision.Denied, audit.Decision);
        Assert.Equal("as admin (actor's roles admin)", audit.Preview);
    }
}
