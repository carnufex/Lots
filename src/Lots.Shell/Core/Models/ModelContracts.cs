using System.Text.Json;

namespace Lots.Shell.Core.Models;

public sealed record ChatMessage(
    string Role,
    string? Content = null,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolCallId = null);

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>A tool offered to the model. <paramref name="Parameters"/> is a JSON Schema object.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonElement Parameters);

public sealed record ModelUsage(int PromptTokens, int CompletionTokens);

public sealed record ModelResponse(
    ChatMessage Message,
    string? FinishReason,
    ModelUsage Usage,
    TimeSpan Latency);

public interface IModelClient
{
    Task<ModelResponse> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken ct);
}

public sealed class ModelOptions
{
    public const string Section = "Model";

    /// <summary>Base URL of an OpenAI-compatible API, e.g. http://ollama:11434/v1</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434/v1";
    public string Model { get; set; } = "";

    /// <summary>Name of the environment variable holding the API key. Empty = no auth (e.g. Ollama).</summary>
    public string? ApiKeyEnv { get; set; }
}
