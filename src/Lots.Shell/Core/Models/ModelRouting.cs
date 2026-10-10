using System.Diagnostics;
using System.Net;
using Lots.Shell.Core.Policy;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Core.Models;

/// <summary>One OpenAI-compatible endpoint (Ollama, vLLM, a hosted provider, ...).</summary>
public sealed class ModelEndpointOptions
{
    public string BaseUrl { get; set; } = "";
    /// <summary>Environment variable holding the API key. Empty = no auth.</summary>
    public string? ApiKeyEnv { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    /// <summary>Where data sent to this endpoint goes: local (our own hardware) or hosted. Used by data classification (#89).</summary>
    public string Location { get; set; } = "local";
    /// <summary>
    /// Highest data class (public|internal|confidential|restricted) this endpoint may see (#89). Default: restricted for local
    /// endpoints, internal for hosted ones.
    /// </summary>
    public string? Clearance { get; set; }
}

/// <summary>One step of an alias' fallback chain.</summary>
public sealed class ModelTarget
{
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
}

/// <summary>
/// A name profiles and features use instead of a concrete model (default, voice, embed, ...). Targets are tried in order:
/// the next one is used when an endpoint is unreachable, times out or answers 5xx (never on 4xx: that is our request's fault).
/// </summary>
public sealed class ModelAlias
{
    public List<ModelTarget> Targets { get; set; } = [];
    /// <summary><c>reasoning_effort</c> for fast (voice) calls; see <see cref="ModelOptions.FastReasoningEffort"/>.</summary>
    public string? FastReasoningEffort { get; set; } = "none";
}

public sealed class ModelsOptions
{
    public const string Section = "Models";
    public Dictionary<string, ModelEndpointOptions> Endpoints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ModelAlias> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Alias used when a run's data is above the clearance of every target of its own alias (#89), typically a local model.
    /// Without it such calls are blocked.
    /// </summary>
    public string? SensitiveAlias { get; set; }
}

public sealed record ModelEndpointHealth(string Name, string BaseUrl, string Location, bool Up, long LatencyMs, string? Error, IReadOnlyList<string> Models,
    string Clearance = "restricted");

/// <summary>The targets a call may use for its data, and why it left its own alias if it did.</summary>
public sealed record ModelRoute(string Alias, IReadOnlyList<ModelTarget> Targets, string? Rerouted);

/// <summary>
/// The configured endpoints and aliases. The single legacy <c>Model</c> section becomes endpoint and alias <c>default</c>,
/// so existing deployments keep working unchanged.
/// </summary>
public sealed class ModelCatalog
{
    public const string Default = "default";
    public const string Voice = "voice";

    public IReadOnlyDictionary<string, ModelEndpointOptions> Endpoints { get; }
    public IReadOnlyDictionary<string, ModelAlias> Aliases { get; }

    public ModelCatalog(IOptions<ModelsOptions> models, IOptions<ModelOptions> legacy)
    {
        var endpoints = new Dictionary<string, ModelEndpointOptions>(models.Value.Endpoints, StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<string, ModelAlias>(models.Value.Aliases, StringComparer.OrdinalIgnoreCase);
        var l = legacy.Value;
        if (!endpoints.ContainsKey(Default) && !string.IsNullOrWhiteSpace(l.BaseUrl))
            endpoints[Default] = new ModelEndpointOptions { BaseUrl = l.BaseUrl, ApiKeyEnv = l.ApiKeyEnv };
        if (!aliases.ContainsKey(Default))
            aliases[Default] = new ModelAlias
            {
                Targets = [new ModelTarget { Endpoint = Default, Model = l.Model }],
                FastReasoningEffort = l.FastReasoningEffort,
            };

        foreach (var (name, alias) in aliases)
            foreach (var t in alias.Targets)
                if (!endpoints.ContainsKey(t.Endpoint))
                    throw new InvalidOperationException($"Model alias '{name}' refers to unknown endpoint '{t.Endpoint}'.");
        foreach (var (name, e) in endpoints)
            if (e.Clearance is not null && !Policy.DataClasses.TryParse(e.Clearance, out _))
                throw new InvalidOperationException($"Model endpoint '{name}' has unknown clearance '{e.Clearance}' ({Policy.DataClasses.Choices}).");
        if (models.Value.SensitiveAlias is { Length: > 0 } sensitive && !aliases.ContainsKey(sensitive))
            throw new InvalidOperationException($"Models:SensitiveAlias '{sensitive}' is not a configured alias.");
        Endpoints = endpoints;
        Aliases = aliases;
        SensitiveAlias = string.IsNullOrWhiteSpace(models.Value.SensitiveAlias) ? null : models.Value.SensitiveAlias;
    }

    public string? SensitiveAlias { get; }

    /// <summary>The highest data class an endpoint may see.</summary>
    public Policy.DataClass ClearanceOf(string endpoint) =>
        Endpoints.TryGetValue(endpoint, out var e)
            ? Policy.DataClasses.Parse(e.Clearance, string.Equals(e.Location, "hosted", StringComparison.OrdinalIgnoreCase)
                ? Policy.DataClass.Internal : Policy.DataClass.Restricted)
            : Policy.DataClass.Public;

    /// <summary>
    /// Where a call with data of class <paramref name="data"/> may go (#89): the alias' own targets that are cleared for it, else
    /// the cleared targets of <see cref="SensitiveAlias"/>. Throws when no endpoint may see the data.
    /// </summary>
    public ModelRoute Route(string? alias, Policy.DataClass data)
    {
        var name = Resolve(alias);
        var own = Aliases[name].Targets.Where(t => ClearanceOf(t.Endpoint) >= data).ToList();
        if (own.Count > 0) return new ModelRoute(name, own, null);
        if (SensitiveAlias is { } s && s != name && Aliases[s].Targets.Where(t => ClearanceOf(t.Endpoint) >= data).ToList() is { Count: > 0 } safe)
            return new ModelRoute(s, safe, $"{data.Name()} data: rerouted from '{name}' to '{s}'");
        throw new Policy.DataClassificationException(
            $"This run has read {data.Name()} data, and no model endpoint of '{name}'" + (SensitiveAlias is null ? "" : $" or '{SensitiveAlias}'") +
            $" is cleared for it. Ask an admin to set a clearance or Models:SensitiveAlias.");
    }

    /// <summary>The highest data class a run on this alias can work with (own targets or the sensitive alias).</summary>
    public Policy.DataClass MaxClearance(string? alias)
    {
        var targets = Aliases[Resolve(alias)].Targets.AsEnumerable();
        if (SensitiveAlias is { } s) targets = targets.Concat(Aliases[s].Targets);
        return targets.Select(t => ClearanceOf(t.Endpoint)).DefaultIfEmpty(Policy.DataClass.Public).Max();
    }

    /// <summary>The alias to use: the requested one if configured, otherwise default.</summary>
    public string Resolve(string? alias) => alias is not null && Aliases.ContainsKey(alias) ? alias : Default;

    /// <summary>The first model of an alias' chain (for traces and display).</summary>
    public string PrimaryModel(string? alias) => Aliases[Resolve(alias)].Targets.FirstOrDefault()?.Model ?? "";
}

/// <summary>
/// The <see cref="IModelClient"/> the shell uses: resolves the call's alias to its chain of endpoints and falls back along it.
/// The response says which endpoint and model actually answered, so the trace records it.
/// </summary>
public sealed class RoutingModelClient(ModelCatalog catalog, IHttpClientFactory httpFactory, ILogger<RoutingModelClient> logger) : IModelClient
{
    public static string HttpClientName(string endpoint) => "model:" + endpoint;

    public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct) =>
        CompleteAsync(messages, tools, new ModelCallOptions(), ct);

    public async Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, ModelCallOptions options, CancellationToken ct)
    {
        var route = catalog.Route(options.Alias, options.Data); // data above every endpoint's clearance never leaves (#89)
        var aliasName = route.Alias;
        var alias = catalog.Aliases[aliasName];
        Exception? last = null;
        foreach (var target in route.Targets)
        {
            var client = new OpenAiCompatibleModelClient(httpFactory.CreateClient(HttpClientName(target.Endpoint)),
                Options.Create(new ModelOptions { Model = target.Model, FastReasoningEffort = alias.FastReasoningEffort }));
            try
            {
                var response = await client.CompleteAsync(messages, tools, options, ct);
                return response with { Model = target.Model, Endpoint = target.Endpoint, Rerouted = route.Rerouted };
            }
            catch (Exception ex) when (Retriable(ex, ct))
            {
                last = ex;
                logger.LogWarning("Model endpoint {Endpoint} ({Model}) failed for alias {Alias}: {Error}; trying the next one",
                    target.Endpoint, target.Model, aliasName, ex.Message);
            }
        }
        throw new HttpRequestException($"All model endpoints for '{aliasName}' failed: {last?.Message}", last);
    }

    /// <summary>Connection failures, timeouts and 5xx move on to the next endpoint; 4xx and the caller's own cancellation do not.</summary>
    internal static bool Retriable(Exception ex, CancellationToken ct) => ex switch
    {
        OperationCanceledException => !ct.IsCancellationRequested,
        HttpRequestException { StatusCode: { } code } => (int)code >= 500 || code == HttpStatusCode.TooManyRequests,
        HttpRequestException => true, // no status: connection refused, DNS, reset
        _ => false,
    };

    /// <summary>Probes every endpoint (<c>GET models</c>) for the Models page and readiness details.</summary>
    public async Task<IReadOnlyList<ModelEndpointHealth>> HealthAsync(CancellationToken ct)
    {
        var tasks = catalog.Endpoints.Select(async e =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probe.CancelAfter(TimeSpan.FromSeconds(5));
                using var res = await httpFactory.CreateClient(HttpClientName(e.Key)).GetAsync("models", probe.Token);
                var models = new List<string>();
                if (res.IsSuccessStatusCode)
                {
                    var doc = System.Text.Json.Nodes.JsonNode.Parse(await res.Content.ReadAsStringAsync(probe.Token));
                    foreach (var m in doc?["data"]?.AsArray() ?? [])
                        if (m?["id"]?.GetValue<string>() is { } id) models.Add(id);
                }
                return new ModelEndpointHealth(e.Key, e.Value.BaseUrl, e.Value.Location, res.IsSuccessStatusCode, sw.ElapsedMilliseconds,
                    res.IsSuccessStatusCode ? null : $"HTTP {(int)res.StatusCode}", models, catalog.ClearanceOf(e.Key).Name());
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return new ModelEndpointHealth(e.Key, e.Value.BaseUrl, e.Value.Location, false, sw.ElapsedMilliseconds, ex.Message, [],
                    catalog.ClearanceOf(e.Key).Name());
            }
        });
        return await Task.WhenAll(tasks);
    }
}
