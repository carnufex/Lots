using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Core.Models;

/// <summary>Chat completions with tool calling against any OpenAI-compatible endpoint.</summary>
public sealed class OpenAiCompatibleModelClient(HttpClient http, IOptions<ModelOptions> options) : IModelClient
{
    private readonly ModelOptions _options = options.Value;

    public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct) =>
        CompleteAsync(messages, tools, new ModelCallOptions(), ct);

    public async Task<ModelResponse> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ModelCallOptions callOptions,
        CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["messages"] = new JsonArray(messages.Select(ToJson).ToArray()),
        };
        var effort = callOptions.ReasoningEffort ?? (callOptions.Fast ? _options.FastReasoningEffort : null);
        if (!string.IsNullOrEmpty(effort))
            body["reasoning_effort"] = effort;
        if (tools.Count > 0)
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.Parameters.GetRawText()),
                },
            }).ToArray());
        }

        var sw = Stopwatch.StartNew();
        using var response = await http.PostAsJsonAsync("chat/completions", body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        sw.Stop();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Model endpoint returned {(int)response.StatusCode}: {Truncate(text, 500)}", null, response.StatusCode);

        var root = JsonNode.Parse(text)!;
        var choice = root["choices"]![0]!;
        var msg = choice["message"]!;

        var toolCalls = msg["tool_calls"]?.AsArray()
            .Select(c => new ToolCall(
                c!["id"]!.GetValue<string>(),
                c["function"]!["name"]!.GetValue<string>(),
                c["function"]!["arguments"]?.GetValue<string>() ?? "{}"))
            .ToList();

        var usage = root["usage"];
        return new ModelResponse(
            new ChatMessage("assistant", msg["content"]?.GetValue<string>(),
                toolCalls is { Count: > 0 } ? toolCalls : null,
                Reasoning: (msg["reasoning"] ?? msg["reasoning_content"])?.GetValue<string>()),
            choice["finish_reason"]?.GetValue<string>(),
            new ModelUsage(
                usage?["prompt_tokens"]?.GetValue<int>() ?? 0,
                usage?["completion_tokens"]?.GetValue<int>() ?? 0),
            sw.Elapsed,
            _options.Model);
    }

    private static JsonNode ToJson(ChatMessage m)
    {
        var o = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
        if (m.ToolCallId is not null) o["tool_call_id"] = m.ToolCallId;
        if (m.ToolCalls is { Count: > 0 })
            o["tool_calls"] = new JsonArray(m.ToolCalls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
            }).ToArray());
        return o;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...[truncated]";
}

public static class ModelClientRegistration
{
    /// <summary>
    /// Registers the model endpoints (<c>Models:Endpoints</c>, plus the legacy <c>Model</c> section as <c>default</c>), the aliases
    /// and the routing client every caller uses as <see cref="IModelClient"/>.
    /// </summary>
    public static IServiceCollection AddModelClient(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ModelOptions>(config.GetSection(ModelOptions.Section));
        services.Configure<ModelsOptions>(config.GetSection(ModelsOptions.Section));
        services.AddSingleton<ModelCatalog>();

        // Named HTTP clients have to be known at registration, so the catalog is built from configuration once here as well.
        var catalog = new ModelCatalog(
            Options.Create(config.GetSection(ModelsOptions.Section).Get<ModelsOptions>() ?? new ModelsOptions()),
            Options.Create(config.GetSection(ModelOptions.Section).Get<ModelOptions>() ?? new ModelOptions()));
        foreach (var (name, endpoint) in catalog.Endpoints)
            services.AddHttpClient(RoutingModelClient.HttpClientName(name), http =>
            {
                http.BaseAddress = new Uri(endpoint.BaseUrl.TrimEnd('/') + "/");
                http.Timeout = TimeSpan.FromSeconds(endpoint.TimeoutSeconds);
                var key = string.IsNullOrEmpty(endpoint.ApiKeyEnv) ? null : Environment.GetEnvironmentVariable(endpoint.ApiKeyEnv);
                if (!string.IsNullOrEmpty(key))
                    http.DefaultRequestHeaders.Authorization = new("Bearer", key);
            });
        services.AddSingleton<RoutingModelClient>();
        services.AddSingleton<IModelClient>(sp => sp.GetRequiredService<RoutingModelClient>());
        return services;
    }
}
