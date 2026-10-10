using System.Text.Json;

namespace Lots.Shell.Core.Models;

public sealed record ChatMessage(
    string Role,
    string? Content = null,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    /// <summary>Reasoning text some models return beside the content. Diagnostics only, never sent back.</summary>
    string? Reasoning = null);

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>A tool offered to the model. <paramref name="Parameters"/> is a JSON Schema object.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonElement Parameters);

public sealed record ModelUsage(int PromptTokens, int CompletionTokens);

/// <param name="Model">The model that answered and <paramref name="Endpoint"/> the endpoint it ran on (after any fallback).</param>
public sealed record ModelResponse(
    ChatMessage Message,
    string? FinishReason,
    ModelUsage Usage,
    TimeSpan Latency,
    string? Model = null,
    string? Endpoint = null,
    string? Rerouted = null);

/// <summary>
/// Per-call hints. <see cref="Fast"/>: answer without a long reasoning phase (voice needs the first words quickly).
/// <see cref="ReasoningEffort"/>: an explicit effort ("none", "low", ...) that wins over <see cref="Fast"/>.
/// <see cref="Alias"/>: which configured model alias to use (default when null or unknown).
/// </summary>
public sealed record ModelCallOptions(bool Fast = false, string? ReasoningEffort = null, string? Alias = null,
    Policy.DataClass Data = Policy.DataClass.Public, Action<string>? OnText = null);

public interface IModelClient
{
    Task<ModelResponse> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct);

    /// <summary>With hints. Providers that do not support them simply ignore the hints.</summary>
    Task<ModelResponse> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ModelCallOptions options,
        CancellationToken ct) => CompleteAsync(messages, tools, ct);
}

public sealed class ModelOptions
{
    public const string Section = "Model";

    /// <summary>Base URL of an OpenAI-compatible API, e.g. http://ollama:11434/v1</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "";

    /// <summary>Name of the environment variable holding the API key. Empty = no auth (e.g. Ollama).</summary>
    public string? ApiKeyEnv { get; set; }

    /// <summary>
    /// Value of <c>reasoning_effort</c> sent for fast (voice) calls. "none" turns thinking off on models that think by default
    /// (without it a 120-token budget can be spent entirely on thinking and the answer comes back empty). Empty = not sent.
    /// </summary>
    public string? FastReasoningEffort { get; set; } = "none";
}
