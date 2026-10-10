using System.Text;
using System.Text.Json;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Tools;
using ModelContextProtocol.Client;

namespace Lots.Shell.Core.Mcp;

/// <summary>Exposes the tools of the configured MCP servers to the <see cref="ToolInvoker"/>.</summary>
/// <remarks>
/// Risk classes are not decided here: they come from the profile, and policy decides per call.
/// Servers with <c>auth: delegated</c> are reached with a per-user token (token exchange), so their client and tool
/// catalog are kept per user and are only available inside a <see cref="DelegationContext"/>.
/// </remarks>
public sealed class McpToolSource(
    IReadOnlyList<McpServerConfig> servers, ILoggerFactory loggers, TimeProvider? clock = null, TokenExchangeClient? exchange = null)
    : IToolSource, IAsyncDisposable
{
    private static readonly TimeSpan CatalogTtl = TimeSpan.FromSeconds(60);

    private sealed class State(McpClient client)
    {
        public McpClient Client { get; } = client;
        public List<ToolDescriptor> Tools { get; set; } = [];
        public DateTimeOffset At { get; set; } = DateTimeOffset.MinValue;
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(string Server, string User), State> _states = [];
    private readonly Dictionary<string, string> _errors = [];

    private static bool IsDelegated(McpServerConfig s) => s.Auth == AuthStrategies.Delegated;

    /// <summary>"" for shared servers, the user for delegated ones, null if a delegated server has no user context.</summary>
    private static string? UserKey(McpServerConfig s) => IsDelegated(s) ? DelegationContext.Current?.UserId : "";

    public async Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct)
    {
        if (servers.Count == 0) return [];

        await _gate.WaitAsync(ct);
        try
        {
            var catalog = new List<ToolDescriptor>();
            foreach (var server in servers)
            {
                if (UserKey(server) is not { } user) continue;
                var state = await RefreshAsync(server, user, ct);
                if (state is null) continue;
                foreach (var tool in state.Tools)
                    if (catalog.All(t => t.Name != tool.Name)) // first server wins on name clashes
                        catalog.Add(tool);
            }
            return catalog;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ServerStatus>> StatusAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var result = new List<ServerStatus>();
            foreach (var server in servers)
            {
                if (IsDelegated(server))
                {
                    result.Add(new ServerStatus(server.Name, server.Url, server.Auth, "per-user", [], null, null));
                    continue;
                }
                var state = await RefreshAsync(server, "", ct);
                result.Add(state is null
                    ? new ServerStatus(server.Name, server.Url, server.Auth, "unavailable", [], _errors.GetValueOrDefault(server.Name), DateTimeOffset.UtcNow)
                    : new ServerStatus(server.Name, server.Url, server.Auth, "ok", state.Tools, null, state.At));
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> ServerOfAsync(string toolName, CancellationToken ct)
    {
        await ListAsync(ct);
        await _gate.WaitAsync(ct);
        try { return Find(toolName)?.Server.Name; }
        finally { _gate.Release(); }
    }

    public async Task<string> CallAsync(string name, string argumentsJson, CancellationToken ct)
    {
        await ListAsync(ct); // make sure the catalog of the current user's context is loaded (cheap: cached)
        McpClient client;
        await _gate.WaitAsync(ct);
        try
        {
            client = Find(name)?.State.Client
                     ?? throw new InvalidOperationException($"No MCP server provides tool '{name}'.");
        }
        finally
        {
            _gate.Release();
        }

        var args = string.IsNullOrWhiteSpace(argumentsJson)
            ? new Dictionary<string, object?>()
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson)
              ?? new Dictionary<string, object?>();

        var result = await client.CallToolAsync(name, args, cancellationToken: ct);

        var text = new StringBuilder();
        foreach (var block in result.Content)
            if (block is ModelContextProtocol.Protocol.TextContentBlock t)
                text.AppendLine(t.Text);
        var output = text.ToString().TrimEnd();

        return result.IsError == true ? $"Error from tool '{name}': {output}" : output;
    }

    /// <summary>Caller holds the gate.</summary>
    private (McpServerConfig Server, State State)? Find(string toolName)
    {
        foreach (var server in servers)
            if (UserKey(server) is { } user
                && _states.TryGetValue((server.Name, user), out var state)
                && state.Tools.Any(t => t.Name == toolName))
                return (server, state);
        return null;
    }

    /// <summary>Caller holds the gate. Returns null if the server is unavailable.</summary>
    private async Task<State?> RefreshAsync(McpServerConfig server, string user, CancellationToken ct)
    {
        var key = (server.Name, user);
        try
        {
            if (!_states.TryGetValue(key, out var state))
            {
                state = new State(await ConnectAsync(server, ct));
                _states[key] = state;
            }

            if (DateTimeOffset.UtcNow - state.At >= CatalogTtl)
            {
                state.Tools = (await state.Client.ListToolsAsync(cancellationToken: ct))
                    .Select(t => new ToolDescriptor(t.Name, t.Description ?? "", t.JsonSchema.Clone()))
                    .ToList();
                state.At = DateTimeOffset.UtcNow;
            }
            _errors.Remove(server.Name);
            return state;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggers.CreateLogger<McpToolSource>().LogWarning(ex, "MCP server {Server} unavailable", server.Name);
            _errors[server.Name] = ex.Message;
            _states.Remove(key); // reconnect next time
            return null;
        }
    }

    private async Task<McpClient> ConnectAsync(McpServerConfig server, CancellationToken ct)
    {
        var options = new HttpClientTransportOptions { Endpoint = new Uri(server.Url), Name = server.Name };
        IClientTransport transport;
        if (IsDelegated(server))
        {
            var ex = exchange ?? throw new InvalidOperationException($"Server '{server.Name}' is delegated but no token exchange is configured.");
            transport = new HttpClientTransport(options, new HttpClient(new DelegatedBearerHandler(server, ex)), loggers, ownsHttpClient: true);
        }
        else if (server.Credentials is { } credentials)
        {
            // The token is attached per request, so it is refreshed transparently when it expires.
            var provider = new BackendTokenProvider(credentials, new HttpClient(), clock ?? TimeProvider.System);
            transport = new HttpClientTransport(options, new HttpClient(new BearerHandler(provider)), loggers, ownsHttpClient: true);
        }
        else
        {
            transport = new HttpClientTransport(options, loggers);
        }
        return await McpClient.CreateAsync(transport, loggerFactory: loggers, cancellationToken: ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _states.Values) await s.Client.DisposeAsync();
    }
}
