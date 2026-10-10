using System.Net;
using System.Net.Http.Json;
using Lots.Shell.Core.Tools;
using Lots.Shell.Features.Settings;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

/// <summary>#155: account preferences are per user, validated, and only offer contexts the user can actually use.</summary>
public class PreferencesTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _app;

    public PreferencesTests(WebApplicationFactory<Program> factory)
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
                s.AddSingleton(TestProfiles.Registry(("list", ToolRisk.Read)));
            });
        });
    }

    private async Task<HttpResponseMessage> Send(HttpMethod m, string user, string roles, object? body = null)
    {
        using var req = new HttpRequestMessage(m, "/me/preferences") { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("X-Dev-User", user);
        req.Headers.Add("X-Dev-Roles", roles);
        return await _app.CreateClient().SendAsync(req);
    }

    [Fact]
    public async Task Preferences_are_saved_per_user_and_default_to_the_system_theme()
    {
        var fresh = await (await Send(HttpMethod.Get, "claude-test-prefs-a", "operator")).Content.ReadFromJsonAsync<PreferencesDto>();
        Assert.Equal(new PreferencesDto("system", null, null, false), fresh);

        var saved = await Send(HttpMethod.Put, "claude-test-prefs-a", "operator", new PreferencesDto("light", "sv", TestProfiles.Name, true));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(new PreferencesDto("light", "sv", TestProfiles.Name, true),
            await (await Send(HttpMethod.Get, "claude-test-prefs-a", "operator")).Content.ReadFromJsonAsync<PreferencesDto>());
        Assert.Equal("system", (await (await Send(HttpMethod.Get, "claude-test-prefs-b", "operator")).Content.ReadFromJsonAsync<PreferencesDto>())!.Theme);
    }

    [Theory]
    [InlineData("neon", null, null)]
    [InlineData("dark", "de", null)]
    [InlineData("dark", null, "nope")]
    public async Task Invalid_preferences_are_refused(string theme, string? language, string? context) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, "claude-test-prefs-c", "operator", new PreferencesDto(theme, language, context, false))).StatusCode);

    [Fact]
    public async Task A_default_context_must_be_one_the_user_can_use() =>
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, "claude-test-prefs-d", "guest", new PreferencesDto("dark", null, TestProfiles.Name, false))).StatusCode);
}
