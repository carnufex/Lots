using System.Net;
using System.Net.Http.Json;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

public class RunApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class CannedModel : IModelClient
    {
        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, CancellationToken ct) =>
            Task.FromResult(new ModelResponse(new ChatMessage("assistant", "all good"), "stop", new ModelUsage(11, 3), TimeSpan.FromMilliseconds(7)));
    }

    private readonly WebApplicationFactory<Program> _factory;

    public RunApiTests(WebApplicationFactory<Program> factory)
    {
        var dbName = Guid.NewGuid().ToString();
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false",
                ["ConnectionStrings:Lots"] = "Host=none",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(dbName));
                s.RemoveAll<IModelClient>();
                s.AddSingleton<IModelClient, CannedModel>();
            });
        });
    }

    private sealed record Started(Guid Id, string Status);

    private sealed record Step(int Seq, string Kind, string Name, long LatencyMs, int? PromptTokens, int? CompletionTokens);

    private sealed record Run(Guid Id, string Status, string? FinalAnswer, List<Step> Steps);

    [Fact]
    public async Task Run_can_be_started_followed_and_read_back_step_by_step()
    {
        var client = _factory.CreateClient();

        var post = await client.PostAsJsonAsync("/runs", new { prompt = "status?" });
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        var started = (await post.Content.ReadFromJsonAsync<Started>())!;

        Run? run = null;
        for (var i = 0; i < 50; i++)
        {
            run = await client.GetFromJsonAsync<Run>($"/runs/{started.Id}");
            if (run!.Status is "Completed" or "Failed") break;
            await Task.Delay(100);
        }

        Assert.Equal("Completed", run!.Status);
        Assert.Equal("all good", run.FinalAnswer);
        var step = Assert.Single(run.Steps);
        Assert.Equal("ModelCall", step.Kind);
        Assert.Equal(7, step.LatencyMs);
        Assert.Equal(11, step.PromptTokens);
        Assert.Equal(3, step.CompletionTokens);
    }

    [Fact]
    public async Task Empty_prompt_is_rejected_and_unknown_run_is_404()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/runs", new { prompt = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/runs/{Guid.NewGuid()}")).StatusCode);
    }
}
