using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

/// <summary>#121: users rate answers; reviewers see the queue and turn bad answers into eval cases.</summary>
public class FeedbackTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class Model : IModelClient
    {
        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, CancellationToken ct) =>
            Task.FromResult(new ModelResponse(new ChatMessage("assistant", "lots-postgres-1 is running"), "stop", new ModelUsage(1, 1), TimeSpan.Zero));
    }

    private readonly WebApplicationFactory<Program> _app;

    public FeedbackTests(WebApplicationFactory<Program> factory)
    {
        var db = Guid.NewGuid().ToString();
        _app = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false", ["ConnectionStrings:Lots"] = "Host=none", ["Auth:Mode"] = "Dev", ["Auth:Dev:AllowHeaders"] = "true",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(db));
                s.AddSingleton(TestProfiles.Registry());
                s.RemoveAll<IModelClient>();
                s.AddSingleton<IModelClient, Model>();
            });
        });
    }

    private static HttpRequestMessage As(HttpMethod m, string url, object? body = null, string user = "claude-test-fb", string roles = "operator")
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("X-Dev-User", user);
        r.Headers.Add("X-Dev-Roles", roles);
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    private static async Task<Guid> AskAsync(HttpClient client, string prompt, Guid? conversationId = null)
    {
        var id = (await (await client.SendAsync(As(HttpMethod.Post, "/runs", new { prompt, conversationId }))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        for (var i = 0; i < 100; i++)
        {
            var run = await (await client.SendAsync(As(HttpMethod.Get, $"/runs/{id}"))).Content.ReadFromJsonAsync<JsonElement>();
            if (run.GetProperty("status").GetString() is "Completed" or "Failed") return id;
            await Task.Delay(50);
        }
        throw new TimeoutException();
    }

    [Fact]
    public async Task Rating_again_replaces_the_rating_and_bad_input_is_refused()
    {
        var client = _app.CreateClient();
        var run = await AskAsync(client, "Is postgres up?");

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(As(HttpMethod.Put, $"/runs/{run}/feedback", new { rating = -1, comment = "it is not" }))).StatusCode);
        var again = await (await client.SendAsync(As(HttpMethod.Put, $"/runs/{run}/feedback", new { rating = 1 }))).Content.ReadFromJsonAsync<JsonElement>();
        var mine = await (await client.SendAsync(As(HttpMethod.Get, $"/runs/{run}/feedback"))).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, again.GetProperty("rating").GetInt32());
        Assert.Equal(again.GetProperty("id").GetGuid(), mine.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(As(HttpMethod.Put, $"/runs/{run}/feedback", new { rating = 5 }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(As(HttpMethod.Put, $"/runs/{run}/feedback",
            new { rating = -1, comment = "my key is ghp_abcdefghijklmnopqrstuvwxyz0123456789" }))).StatusCode);
    }

    [Fact]
    public async Task Nobody_rates_a_run_they_cannot_read()
    {
        var client = _app.CreateClient();
        var run = await AskAsync(client, "Is postgres up?");

        var res = await client.SendAsync(As(HttpMethod.Put, $"/runs/{run}/feedback", new { rating = -1 }, user: "claude-test-other"));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Only_reviewers_see_the_queue_with_prompt_and_answer()
    {
        var client = _app.CreateClient();
        var run = await AskAsync(client, "Is postgres up?");
        await client.SendAsync(As(HttpMethod.Put, $"/runs/{run}/feedback", new { rating = -1, comment = "wrong container" }));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(As(HttpMethod.Get, "/feedback"))).StatusCode);
        var queue = await (await client.SendAsync(As(HttpMethod.Get, "/feedback?state=open&rating=down", user: "claude-test-admin", roles: "admin")))
            .Content.ReadFromJsonAsync<JsonElement>();

        var item = queue.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("runId").GetGuid() == run);
        Assert.Equal("Is postgres up?", item.GetProperty("prompt").GetString());
        Assert.Equal("lots-postgres-1 is running", item.GetProperty("answer").GetString());
        Assert.Equal("wrong container", item.GetProperty("comment").GetString());
        Assert.True(queue.GetProperty("down").GetInt32() >= 1);
    }

    [Fact]
    public async Task A_bad_answer_becomes_an_eval_case_a_dataset_and_a_judge_label()
    {
        var client = _app.CreateClient();
        var run = await AskAsync(client, "Is postgres up? My name is Kim.");
        await client.SendAsync(As(HttpMethod.Put, $"/runs/{run}/feedback", new { rating = -1, comment = "did not check" }));
        var admin = (HttpMethod m, string url, object? body) => As(m, url, body, user: "claude-test-admin", roles: "admin");
        var id = (await (await client.SendAsync(admin(HttpMethod.Get, "/feedback", null))).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray().Single(i => i.GetProperty("runId").GetGuid() == run).GetProperty("id").GetGuid();

        var empty = await client.SendAsync(admin(HttpMethod.Post, $"/feedback/{id}/eval-case", new { }));
        var made = await client.SendAsync(admin(HttpMethod.Post, $"/feedback/{id}/eval-case", new
        {
            question = "Is postgres up?", expectedTools = new[] { "list_containers", " " }, judge = "Checks the containers before answering.",
        }));
        var dataset = await (await client.SendAsync(admin(HttpMethod.Get, "/feedback/eval-cases?profile=test", null))).Content.ReadFromJsonAsync<JsonElement>();
        var labels = await (await client.SendAsync(admin(HttpMethod.Get, "/feedback/labels", null))).Content.ReadFromJsonAsync<List<JsonElement>>();

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var c = dataset.GetProperty("cases").EnumerateArray().Single();
        Assert.Equal("Is postgres up?", c.GetProperty("question").GetString()); // the reviewer removed the name
        Assert.Equal(["list_containers"], c.GetProperty("expectedTools").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(TestProfiles.Name, c.GetProperty("profile").GetString());
        Assert.False(c.TryGetProperty("expectedFacts", out _)); // nulls are left out: the file reads like a hand-written one
        Assert.Equal("feedback-test", dataset.GetProperty("dataset").GetString());
        var label = Assert.Single(labels!);
        Assert.Equal("fail", label.GetProperty("label").GetString());
        Assert.Equal("lots-postgres-1 is running", label.GetProperty("answer").GetString());
    }

    [Fact]
    public async Task The_rating_shows_on_the_conversation_turn()
    {
        var client = _app.CreateClient();
        var conversation = Guid.NewGuid();
        var run = await AskAsync(client, "Is postgres up?", conversation);
        await client.SendAsync(As(HttpMethod.Put, $"/runs/{run}/feedback", new { rating = 1, comment = "thanks" }));

        var detail = await (await client.SendAsync(As(HttpMethod.Get, $"/conversations/{conversation}"))).Content.ReadFromJsonAsync<JsonElement>();

        var turn = detail.GetProperty("turns").EnumerateArray().Single();
        Assert.Equal(1, turn.GetProperty("rating").GetInt32());
        Assert.Equal("thanks", turn.GetProperty("feedbackComment").GetString());
    }
}
