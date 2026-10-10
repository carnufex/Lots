using System.Text.Json;
using System.Text.Json.Nodes;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

/// <summary>
/// #109: the API contract is generated from the endpoints and reviewed as docs/api/openapi.json. A change to an endpoint fails this
/// test until the reviewed copy is updated (run with LOTS_UPDATE_OPENAPI=1), so API changes are always visible in a diff.
/// </summary>
public class OpenApiContractTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string Reviewed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../docs/api/openapi.json"));

    private async Task<string> GeneratedAsync()
    {
        var app = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false", ["Agent:RunWorkerEnabled"] = "false", ["ConnectionStrings:Lots"] = "Host=none",
                ["Auth:Mode"] = "Dev",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
                s.AddSingleton(TestProfiles.Registry());
            });
        });
        var json = await app.CreateClient().GetStringAsync("/openapi/v1.json");
        var doc = JsonNode.Parse(json)!.AsObject();
        doc.Remove("servers"); // the test host's address, not part of the contract
        return doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
    }

    [Fact]
    public async Task The_generated_contract_matches_the_reviewed_copy()
    {
        var generated = await GeneratedAsync();
        if (Environment.GetEnvironmentVariable("LOTS_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Reviewed)!);
            await File.WriteAllTextAsync(Reviewed, generated);
        }
        Assert.True(File.Exists(Reviewed), "docs/api/openapi.json is missing: run the tests with LOTS_UPDATE_OPENAPI=1");
        var reviewed = (await File.ReadAllTextAsync(Reviewed)).ReplaceLineEndings("\n");
        Assert.True(reviewed == generated,
            "The API changed. Review the difference, then update docs/api/openapi.json (dotnet test with LOTS_UPDATE_OPENAPI=1).");
    }

    [Fact]
    public async Task The_contract_covers_the_main_surfaces_and_declares_bearer_auth()
    {
        using var doc = JsonDocument.Parse(await GeneratedAsync());
        var paths = doc.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Contains("/runs", paths);
        Assert.Contains("/runs/{id}", paths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("/approvals", paths);
        Assert.Contains("/me/tokens", paths);
        Assert.Contains("/audit", paths);
        Assert.True(doc.RootElement.GetProperty("components").GetProperty("securitySchemes").EnumerateObject().Any());
        Assert.Equal("1", doc.RootElement.GetProperty("info").GetProperty("version").GetString());
    }
}
