using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

/// <summary>
/// #157: the menu is computed from the same checks as the endpoints. For every page and role set, "the page is shown" must equal "its
/// endpoint answers" (anything but 403), so the two cannot drift apart.
/// </summary>
public class CapabilitiesTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _app;

    public CapabilitiesTests(WebApplicationFactory<Program> factory)
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
                s.AddSingleton(TestProfiles.Registry());
            });
        });
    }

    /// <summary>One endpoint that the page cannot work without.</summary>
    public static readonly Dictionary<string, string> PageEndpoint = new()
    {
        ["account"] = "/me/preferences", ["chat"] = "/conversations", ["history"] = "/conversations", ["runs"] = "/runs", ["usage"] = "/usage", ["voice"] = "/me/settings",
        ["knowledge"] = "/knowledge", ["integrations"] = "/tool-calls", ["audit"] = "/audit", ["models"] = "/models",
        ["profiles"] = "/admin/v1/resources", ["policy"] = "/admin/v1/resources", ["identity"] = "/admin/v1/identity",
        ["feedback"] = "/feedback", ["insights"] = "/insights/outcomes",
    };

    private async Task<HttpStatusCode> GetAs(string url, string roles)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Dev-User", "claude-test-capabilities");
        req.Headers.Add("X-Dev-Roles", roles);
        return (await _app.CreateClient().SendAsync(req)).StatusCode;
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("admin")]
    [InlineData("auditor")]
    [InlineData("self-improve")]
    [InlineData("guest")]
    public async Task Menu_visibility_equals_endpoint_access(string roles)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/me/capabilities");
        req.Headers.Add("X-Dev-User", "claude-test-capabilities");
        req.Headers.Add("X-Dev-Roles", roles);
        var caps = await (await _app.CreateClient().SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        var pages = caps.GetProperty("pages").EnumerateArray().Select(p => p.GetString()!).ToHashSet();

        foreach (var (page, url) in PageEndpoint)
        {
            var allowed = await GetAs(url, roles) != HttpStatusCode.Forbidden;
            Assert.True(pages.Contains(page) == allowed, $"{roles}: page '{page}' shown={pages.Contains(page)} but {url} allowed={allowed}");
        }
    }

    [Fact]
    public async Task An_operator_sees_the_work_pages_but_no_administration()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/me/capabilities");
        req.Headers.Add("X-Dev-User", "claude-test-capabilities");
        req.Headers.Add("X-Dev-Roles", "operator");
        var caps = await (await _app.CreateClient().SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        var pages = caps.GetProperty("pages").EnumerateArray().Select(p => p.GetString()!).ToList();

        Assert.Contains("chat", pages);
        Assert.Contains("history", pages);
        Assert.Contains("knowledge", pages);
        Assert.DoesNotContain("policy", pages);
        Assert.DoesNotContain("identity", pages);
        Assert.DoesNotContain("profiles", pages);
        Assert.DoesNotContain("audit", pages);
        Assert.DoesNotContain("feedback", pages);
        Assert.DoesNotContain("approvals", pages); // operators approve nothing in the test profile
        Assert.Equal([TestProfiles.Name], caps.GetProperty("contexts").EnumerateArray().Select(c => c.GetString()));
    }
}
