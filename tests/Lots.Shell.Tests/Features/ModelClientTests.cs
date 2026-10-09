using System.Net;
using System.Text;
using System.Text.Json;
using Lots.Shell.Core.Models;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Tests.Features;

public class ModelClientTests
{
    private sealed class FakeModel(Func<string, string> reply) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadAsStringAsync(ct);
            Requests.Add(body);
            await Task.Delay(5, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply(body), Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubStatus(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
    }

    private static OpenAiCompatibleModelClient Client(HttpMessageHandler h) =>
        new(new HttpClient(h) { BaseAddress = new Uri("http://model.test/v1/") },
            Options.Create(new ModelOptions { Model = "test-model" }));

    private static readonly ToolDefinition Weather = new(
        "get_weather", "Get weather",
        JsonDocument.Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""").RootElement.Clone());

    [Fact]
    public async Task Tool_calling_round_trip()
    {
        var fake = new FakeModel(body => body.Contains("\"role\":\"tool\"")
            ? """{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"It is sunny."}}],"usage":{"prompt_tokens":30,"completion_tokens":5}}"""
            : """{"choices":[{"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Oslo\"}"}}]}}],"usage":{"prompt_tokens":20,"completion_tokens":8}}""");
        var client = Client(fake);
        var messages = new List<ChatMessage> { new("user", "Weather in Oslo?") };

        var first = await client.CompleteAsync(messages, [Weather], default);

        var call = Assert.Single(first.Message.ToolCalls!);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("Oslo", JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("city").GetString());
        Assert.Equal(new ModelUsage(20, 8), first.Usage);
        Assert.True(first.Latency > TimeSpan.Zero);

        messages.Add(first.Message);
        messages.Add(new ChatMessage("tool", "sunny, 21C", ToolCallId: call.Id));
        var second = await client.CompleteAsync(messages, [Weather], default);

        Assert.Equal("It is sunny.", second.Message.Content);
        Assert.Null(second.Message.ToolCalls);
        Assert.Contains("\"tool_call_id\":\"call_1\"", fake.Requests[1]);
    }

    [Fact]
    public async Task Non_success_status_throws_with_body()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Client(new StubStatus(HttpStatusCode.BadRequest, "bad model"))
                .CompleteAsync([new("user", "hi")], [], default));
        Assert.Contains("400", ex.Message);
        Assert.Contains("bad model", ex.Message);
    }
}
