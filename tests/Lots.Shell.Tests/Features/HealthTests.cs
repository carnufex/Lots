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
                    ["Agent:RunWorkerEnabled"] = "false",
                    ["ConnectionStrings:Lots"] = "Host=localhost;Database=none",
                }));
            b.ConfigureServices(s => s.AddSingleton(TestProfiles.Registry()));
        }).CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
