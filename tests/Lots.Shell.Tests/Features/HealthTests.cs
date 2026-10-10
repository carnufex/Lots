using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Lots.Shell.Core.Profiles;

namespace Lots.Shell.Tests.Features;

public class HealthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Liveness_returns_200_without_database()
    {
        var client = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:MigrateOnStartup"] = "false",
                ["Auth:Mode"] = "Dev",
                    ["Agent:RunWorkerEnabled"] = "false",
                    ["ConnectionStrings:Lots"] = "Host=localhost;Database=none",
                }));
            b.ConfigureServices(s => s.AddSingleton(TestProfiles.Registry()));
        }).CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Metrics_are_exposed_anonymously_without_user_identities()
    {
        var client = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false", ["Auth:Mode"] = "Dev", ["Agent:RunWorkerEnabled"] = "false",
                ["ConnectionStrings:Lots"] = "Host=localhost;Database=none",
            }));
            b.ConfigureServices(s => s.AddSingleton(TestProfiles.Registry()));
        }).CreateClient();
        Lots.Shell.Core.Telemetry.LotsMetrics.RunsStarted.Add(1, new("profile", "test"), new("voice", false));

        var body = await client.GetStringAsync("/metrics");

        Assert.Contains("lots_runs_started_total", body);
        Assert.DoesNotContain("user=", body);
    }
}
