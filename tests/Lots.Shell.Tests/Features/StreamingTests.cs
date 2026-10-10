using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Runs;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Tests.Features;

/// <summary>#95: streamed answers (OpenAI-style SSE), live run events, regenerate and edit within a conversation.</summary>
public class StreamingTests
{
    private sealed class SseHandler(params string[] chunks) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            var sse = string.Concat(chunks.Select(c => $"data: {c}\n\n")) + "data: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        }
    }

    private static OpenAiCompatibleModelClient Client(HttpMessageHandler h) =>
        new(new HttpClient(h) { BaseAddress = new Uri("http://model/v1/") }, Options.Create(new ModelOptions { Model = "m" }));

    [Fact]
    public async Task Content_deltas_are_forwarded_as_they_arrive_and_assembled_into_the_reply()
    {
        var handler = new SseHandler(
            """{"choices":[{"delta":{"role":"assistant","reasoning":"thinking "}}]}""",
            """{"choices":[{"delta":{"content":"Hej"}}]}""",
            """{"choices":[{"delta":{"content":" där"}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":12,"completion_tokens":3}}""");
        var seen = new List<string>();

        var r = await Client(handler).CompleteAsync([new ChatMessage("user", "hej")], [], new ModelCallOptions(OnText: seen.Add), default);

        Assert.Equal(["Hej", " där"], seen);
        Assert.Equal("Hej där", r.Message.Content);
        Assert.Equal("thinking ", r.Message.Reasoning);
        Assert.Equal(("stop", 12, 3), (r.FinishReason, r.Usage.PromptTokens, r.Usage.CompletionTokens));
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task Tool_calls_that_arrive_in_pieces_are_put_together()
    {
        var handler = new SseHandler(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"get_logs","arguments":"{\"conta"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"iner\":\"web\"}"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"c2","function":{"name":"list_containers","arguments":"{}"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""");

        var r = await Client(handler).CompleteAsync([new ChatMessage("user", "x")], [], new ModelCallOptions(OnText: _ => { }), default);

        Assert.Null(r.Message.Content);
        Assert.Equal([("c1", "get_logs", "{\"container\":\"web\"}"), ("c2", "list_containers", "{}")],
            r.Message.ToolCalls!.Select(c => (c.Id, c.Name, c.ArgumentsJson)));
    }

    [Fact]
    public async Task Partial_text_is_kept_per_run_until_the_call_ends()
    {
        var streams = new RunStreams(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            NullLogger<RunStreams>.Instance);
        var id = Guid.NewGuid();
        streams.Publish(id, "Hej");
        streams.Publish(id, " där");
        Assert.Equal("Hej där", streams.Get(id)!.Value.Text);
        Assert.Null(streams.Get(Guid.NewGuid()));
        await streams.ClearAsync(id);
        Assert.Null(streams.Get(id));
    }

    // ---- API: events and regenerate

    private sealed class Answer(string text) : IModelClient
    {
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, CancellationToken ct) =>
            CompleteAsync(m, t, new ModelCallOptions(), ct);

        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, ModelCallOptions o, CancellationToken ct)
        {
            lock (Requests) Requests.Add(m.ToList());
            o.OnText?.Invoke(text[..2]);
            o.OnText?.Invoke(text[2..]);
            return Task.FromResult(new ModelResponse(new ChatMessage("assistant", text + " #" + Requests.Count), "stop", new ModelUsage(1, 1), TimeSpan.Zero));
        }
    }

    private static (WebApplicationFactory<Program> App, Answer Model) Host(WebApplicationFactory<Program> factory)
    {
        var model = new Answer("streamed answer");
        var db = Guid.NewGuid().ToString();
        return (factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false", ["ConnectionStrings:Lots"] = "Host=none", ["Auth:Mode"] = "Dev",
                ["Auth:Dev:AllowHeaders"] = "true",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(db));
                s.AddSingleton(TestProfiles.Registry());
                s.RemoveAll<IModelClient>();
                s.AddSingleton<IModelClient>(model);
            });
        }), model);
    }

    private static HttpRequestMessage As(HttpMethod m, string url, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("X-Dev-User", "claude-test-chat");
        r.Headers.Add("X-Dev-Roles", "operator");
        if (body is not null) r.Content = System.Net.Http.Json.JsonContent.Create(body);
        return r;
    }

    private sealed record Started(Guid Id);

    private static async Task<JsonElement> WaitAsync(HttpClient client, Guid id)
    {
        for (var i = 0; i < 100; i++)
        {
            var run = await (await client.SendAsync(As(HttpMethod.Get, $"/runs/{id}"))).Content.ReadFromJsonAsync<JsonElement>();
            if (run.GetProperty("status").GetString() is "Completed" or "Failed") return run;
            await Task.Delay(50);
        }
        throw new TimeoutException();
    }

    public class Api(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
    {
        [Fact]
        public async Task The_event_stream_reports_changes_and_ends_when_the_run_is_done()
        {
            var (app, _) = Host(factory);
            var client = app.CreateClient();
            var id = (await (await client.SendAsync(As(HttpMethod.Post, "/runs", new { prompt = "hej" }))).Content.ReadFromJsonAsync<Started>())!.Id;
            await WaitAsync(client, id);

            using var res = await client.SendAsync(As(HttpMethod.Get, $"/runs/{id}/events"), HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal("text/event-stream", res.Content.Headers.ContentType!.MediaType);
            var text = await res.Content.ReadAsStringAsync();
            Assert.Contains("event: changed", text);
            Assert.Contains("event: done\ndata: {\"status\":\"Completed\"}", text);

            var other = new HttpRequestMessage(HttpMethod.Get, $"/runs/{id}/events");
            other.Headers.Add("X-Dev-User", "someone-else");
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(other)).StatusCode);
        }

        [Fact]
        public async Task Regenerating_or_editing_the_last_turn_replaces_it_in_the_conversation()
        {
            var (app, model) = Host(factory);
            var client = app.CreateClient();
            var conversation = Guid.NewGuid();
            var first = (await (await client.SendAsync(As(HttpMethod.Post, "/runs", new { prompt = "first question", conversationId = conversation }))).Content.ReadFromJsonAsync<Started>())!.Id;
            await WaitAsync(client, first);
            var second = (await (await client.SendAsync(As(HttpMethod.Post, "/runs", new { prompt = "second question", conversationId = conversation }))).Content.ReadFromJsonAsync<Started>())!.Id;
            await WaitAsync(client, second);

            // Only the latest turn can be redone.
            Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(As(HttpMethod.Post, $"/runs/{first}/regenerate", new { }))).StatusCode);

            var edited = await client.SendAsync(As(HttpMethod.Post, $"/runs/{second}/regenerate", new { prompt = "second question, edited" }));
            Assert.Equal(HttpStatusCode.Accepted, edited.StatusCode);
            var third = (await edited.Content.ReadFromJsonAsync<Started>())!.Id;
            var run = await WaitAsync(client, third);
            Assert.Equal("Completed", run.GetProperty("status").GetString());
            Assert.Equal(second.ToString(), run.GetProperty("retryOf").GetString());

            // The edited turn's context has the first turn, not the replaced second one.
            var context = model.Requests.Last();
            Assert.Contains(context, m => m.Content == "first question");
            Assert.DoesNotContain(context, m => m.Content == "second question");
            Assert.Equal("second question, edited", context.Last().Content);

            Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(As(HttpMethod.Post, $"/runs/{second}/regenerate", new { }))).StatusCode);
        }
    }
}
