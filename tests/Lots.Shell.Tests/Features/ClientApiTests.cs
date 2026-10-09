using System.Net.Http.Json;
using System.Text.Json;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

public class ClientApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ClientApiTests(WebApplicationFactory<Program> factory)
    {
        var dbName = Guid.NewGuid().ToString();
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false",
                ["Agent:RunWorkerEnabled"] = "false",
                ["ConnectionStrings:Lots"] = "Host=none",
                ["Auth:Mode"] = "Dev",
                ["Auth:Dev:AllowHeaders"] = "true",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(dbName));
                s.AddSingleton(TestProfiles.Registry());
            });
        });
    }

    private static HttpRequestMessage As(HttpMethod m, string url, string user, string roles, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("X-Dev-User", user);
        r.Headers.Add("X-Dev-Roles", roles);
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    [Fact]
    public async Task Config_is_public_and_describes_auth_mode_and_profiles()
    {
        var client = _factory.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>("/config");

        Assert.Equal("dev", json.GetProperty("authMode").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("oidc").ValueKind);
        Assert.Equal(TestProfiles.Name, json.GetProperty("profiles")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Run_list_shows_only_own_runs_but_admins_see_everyones()
    {
        var client = _factory.CreateClient();
        await client.SendAsync(As(HttpMethod.Post, "/runs", "alice", "operator", new { prompt = "alice question" }));
        await client.SendAsync(As(HttpMethod.Post, "/runs", "bob", "operator", new { prompt = "bob question" }));

        var alice = await (await client.SendAsync(As(HttpMethod.Get, "/runs", "alice", "operator"))).Content.ReadFromJsonAsync<JsonElement>();
        var admin = await (await client.SendAsync(As(HttpMethod.Get, "/runs", "root", "admin"))).Content.ReadFromJsonAsync<JsonElement>();
        var nobody = await (await client.SendAsync(As(HttpMethod.Get, "/runs", "carol", "operator"))).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(["alice question"], alice.EnumerateArray().Select(r => r.GetProperty("prompt").GetString()!));
        Assert.Equal(2, admin.GetArrayLength());
        Assert.Equal(0, nobody.GetArrayLength());
    }
}
