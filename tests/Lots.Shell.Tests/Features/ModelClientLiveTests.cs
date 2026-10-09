using System.Text.Json;
using Lots.Shell.Core.Models;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Tests.Features;

/// <summary>
/// Opt-in: set LOTS_TEST_MODEL_URL (e.g. http://192.168.1.215:11434/v1) and LOTS_TEST_MODEL
/// to run a real tool-calling round trip. Without them the test is a no-op.
/// </summary>
public class ModelClientLiveTests
{
    [Fact]
    public async Task Live_tool_calling_round_trip()
    {
        var url = Environment.GetEnvironmentVariable("LOTS_TEST_MODEL_URL");
        var model = Environment.GetEnvironmentVariable("LOTS_TEST_MODEL");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(model)) return;

        var client = new OpenAiCompatibleModelClient(
            new HttpClient { BaseAddress = new Uri(url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) },
            Options.Create(new ModelOptions { BaseUrl = url, Model = model }));
        var tool = new ToolDefinition("get_weather", "Get the current weather for a city",
            JsonDocument.Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""").RootElement.Clone());
        var messages = new List<ChatMessage>
        {
            new("system", "Use the provided tools to answer. Never guess the weather."),
            new("user", "What is the weather in Oslo right now?"),
        };

        var first = await client.CompleteAsync(messages, [tool], default);
        var call = Assert.Single(first.Message.ToolCalls!);
        Assert.Equal("get_weather", call.Name);

        messages.Add(first.Message);
        messages.Add(new ChatMessage("tool", "Sunny, 21 C", ToolCallId: call.Id));
        var second = await client.CompleteAsync(messages, [tool], default);

        Assert.False(string.IsNullOrWhiteSpace(second.Message.Content));
        Assert.True(second.Usage.PromptTokens > 0);
    }
}
