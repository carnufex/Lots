using System.Net.Http.Json;
using Lots.Shell.Features.Usage;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

public class UsageTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public UsageTests(WebApplicationFactory<Program> factory)
    {
        var dbName = Guid.NewGuid().ToString();
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false", ["Auth:Mode"] = "Dev", ["ConnectionStrings:Lots"] = "Host=none", ["Auth:Dev:AllowHeaders"] = "true",
                ["Agent:RunWorkerEnabled"] = "false",
                ["Models:Currency"] = "SEK",
                ["Models:Prices:big_model:InputPerMillion"] = "10",
                ["Models:Prices:big_model:OutputPerMillion"] = "30",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(dbName));
                s.AddSingleton(TestProfiles.Registry());
            });
        });
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var now = DateTimeOffset.UtcNow;
        RunRecord R(string user, RunStatus status) => new() { Id = Guid.NewGuid(), Prompt = "p", Profile = "test", UserId = user, Roles = "operator", Status = status, CreatedAt = now, UpdatedAt = now };
        var a = R("alice", RunStatus.Completed);
        var b = R("bob", RunStatus.Failed);
        db.Runs.AddRange(a, b);
        db.RunSteps.AddRange(
            new RunStepRecord { RunId = a.Id, Seq = 0, Kind = StepKind.ModelCall, Name = "big:model", PromptTokens = 100_000, CompletionTokens = 10_000, LatencyMs = 2000, CreatedAt = now },
            new RunStepRecord { RunId = b.Id, Seq = 0, Kind = StepKind.ModelCall, Name = "local", PromptTokens = 500, CompletionTokens = 50, LatencyMs = 500, CreatedAt = now });
        db.SaveChanges();
    }

    private async Task<UsageReport> Get(string user, string roles, string query)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/usage" + query);
        req.Headers.Add("X-Dev-User", user);
        req.Headers.Add("X-Dev-Roles", roles);
        return (await (await _factory.CreateClient().SendAsync(req)).Content.ReadFromJsonAsync<UsageReport>())!;
    }

    [Fact]
    public async Task Cost_follows_the_price_table_and_users_see_only_their_own_usage()
    {
        var all = await Get("root", "admin", "?groupBy=user");
        Assert.Equal("SEK", all.Currency);
        Assert.Equal(1.3, all.Rows.Single(r => r.Key == "alice").Cost); // 100k * 10/M + 10k * 30/M
        Assert.Equal(0.5, all.Total.ErrorRate);
        Assert.Equal(["local"], all.UnpricedModels);

        var mine = await Get("bob", "operator", "?groupBy=user");
        Assert.Equal("day", mine.GroupBy); // grouping by user is for admins
        Assert.Equal(550, mine.Total.PromptTokens + mine.Total.CompletionTokens);

        var byModel = await Get("root", "admin", "?groupBy=model");
        Assert.Equal(["big:model", "local"], byModel.Rows.Select(r => r.Key));
    }
}
