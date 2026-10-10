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
        if (callOptions.OnText is { } onText) return await StreamAsync(body, onText, sw, ct);
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

    /// <summary>
    /// Streams the reply (#95): content deltas go to <paramref name="onText"/> as they arrive; tool calls, which arrive in pieces,
    /// are assembled by index. The result is the same response a non-streaming call returns.
    /// </summary>
    private async Task<ModelResponse> StreamAsync(JsonObject body, Action<string> onText, Stopwatch sw, CancellationToken ct)
    {
        body["stream"] = true;
        body["stream_options"] = new JsonObject { ["include_usage"] = true };
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = JsonContent.Create(body) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Model endpoint returned {(int)response.StatusCode}: {Truncate(error, 500)}", null, response.StatusCode);
        }

        var content = new System.Text.StringBuilder();
        var reasoning = new System.Text.StringBuilder();
        var calls = new SortedDictionary<int, (string? Id, string? Name, System.Text.StringBuilder Args)>();
        string? finish = null;
        int prompt = 0, completion = 0;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            var chunk = JsonNode.Parse(data)!;
            if (chunk["usage"] is JsonObject u)
            {
                prompt = u["prompt_tokens"]?.GetValue<int>() ?? prompt;
                completion = u["completion_tokens"]?.GetValue<int>() ?? completion;
            }
            if (chunk["choices"] is not JsonArray { Count: > 0 } choices) continue;
            var choice = choices[0]!;
            finish = choice["finish_reason"]?.GetValue<string>() ?? finish;
            if (choice["delta"] is not JsonObject delta) continue;
            if ((delta["reasoning"] ?? delta["reasoning_content"])?.GetValue<string>() is { Length: > 0 } r) reasoning.Append(r);
            if (delta["content"]?.GetValue<string>() is { Length: > 0 } text)
            {
                content.Append(text);
                onText(text);
            }
            if (delta["tool_calls"] is JsonArray parts)
                foreach (var part in parts)
                {
                    var index = part!["index"]?.GetValue<int>() ?? calls.Count;
                    var current = calls.TryGetValue(index, out var c) ? c : (null, null, new System.Text.StringBuilder());
                    var id = part["id"]?.GetValue<string>() ?? current.Id;
                    var name = part["function"]?["name"]?.GetValue<string>() ?? current.Name;
                    current.Args.Append(part["function"]?["arguments"]?.GetValue<string>());
                    calls[index] = (id, name, current.Args);
                }
        }
        sw.Stop();

        var toolCalls = calls.Values.Where(c => c.Name is not null)
            .Select((c, i) => new ToolCall(c.Id ?? $"call_{i}", c.Name!, c.Args.Length == 0 ? "{}" : c.Args.ToString())).ToList();
        return new ModelResponse(
            new ChatMessage("assistant", content.Length == 0 ? null : content.ToString(), toolCalls.Count > 0 ? toolCalls : null,
                Reasoning: reasoning.Length == 0 ? null : reasoning.ToString()),
            finish, new ModelUsage(prompt, completion), sw.Elapsed, _options.Model);
    }

    private static JsonNode ToJson(ChatMessage m)
    {
        var o = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
        if (m.Images is { Count: > 0 })
        {
            // OpenAI content parts: the text, then the images (#105).
            var parts = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = m.Content ?? "" });
            foreach (var url in m.Images) parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } });
            o["content"] = parts;
        }
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
