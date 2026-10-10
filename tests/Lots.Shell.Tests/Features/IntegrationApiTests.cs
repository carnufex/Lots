using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lots.Shell.Core.Tools;
using Lots.Shell.Features.Integrations;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

public class IntegrationApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>The "sample" server of the test profile offers one declared tool and one nobody classified.</summary>
    private sealed class Server : IToolSource
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone();
        private static readonly List<ToolDescriptor> Tools =
            [new("list_containers", "lists containers", Schema), new("drop_database", "new on the server", Schema)];

        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ToolDescriptor>>(Tools);
        public Task<string> CallAsync(string name, string argumentsJson, CancellationToken ct) => Task.FromResult("ok");
        public Task<IReadOnlyList<ServerStatus>> StatusAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ServerStatus>>([new ServerStatus("sample", "http://localhost:1/mcp", "service-account", "ok", Tools, null, DateTimeOffset.UtcNow)]);
    }

    private readonly WebApplicationFactory<Program> _factory;

    public IntegrationApiTests(WebApplicationFactory<Program> factory)
    {
        var dbName = Guid.NewGuid().ToString();
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false",
                ["Auth:Mode"] = "Dev",
                ["ConnectionStrings:Lots"] = "Host=none",
                ["Auth:Dev:AllowHeaders"] = "true",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(dbName));
                s.AddSingleton(TestProfiles.Registry(("list_containers", ToolRisk.Read), ("restart", ToolRisk.Write)));
                s.RemoveAll<IToolSource>();
                s.AddSingleton<IToolSource>(new Server());
            });
        });
    }

    private Task<HttpResponseMessage> Get(string url, string roles)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Dev-User", "someone");
        req.Headers.Add("X-Dev-Roles", roles);
        return _factory.CreateClient().SendAsync(req);
    }

    [Fact]
    public async Task Server_details_are_for_admins_and_the_catalog_for_admins_and_auditors()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/integrations/servers", "operator")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/integrations/servers", "auditor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/integrations/catalog", "operator")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get("/integrations/catalog", "auditor")).StatusCode);

        var servers = (await (await Get("/integrations/servers", "admin")).Content.ReadFromJsonAsync<List<ServerDto>>())!;
        var sample = Assert.Single(servers);
        Assert.Equal(("sample", "ok"), (sample.Name, sample.Health));
        Assert.Equal([TestProfiles.Name], sample.Profiles);
        Assert.Equal(["test:Read"], sample.Tools.Single(t => t.Name == "list_containers").Profiles);
        Assert.Empty(sample.Tools.Single(t => t.Name == "drop_database").Profiles);
    }

    [Fact]
    public async Task Catalog_marks_declared_missing_and_unclassified_tools_with_who_may_use_them()
    {
        var rows = (await (await Get("/integrations/catalog", "admin")).Content.ReadFromJsonAsync<List<CatalogEntry>>())!;

        var list = rows.Single(r => r.Tool == "list_containers");
        Assert.Equal(("exposed", "Read", "sample"), (list.Status, list.Risk, list.Server));
        Assert.Equal(["operator", "admin"], list.AllowedRoles);

        var restart = rows.Single(r => r.Tool == "restart");
        Assert.Equal("missing", restart.Status);
        Assert.Equal(["admin"], restart.ApprovalRoles);
        Assert.Equal(["admin"], restart.ApproverRoles);

        // Offered by the server but declared nowhere: listed for review, granted to nobody.
        var unclassified = rows.Single(r => r.Tool == "drop_database");
        Assert.Equal(("unclassified", (string?)null, (string?)null), (unclassified.Status, unclassified.Profile, unclassified.Risk));
        Assert.Empty(unclassified.AllowedRoles);
    }
}
