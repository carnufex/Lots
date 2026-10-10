using System.Collections.Concurrent;
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
    Func<IReadOnlyList<McpServerConfig>> serverList, ILoggerFactory loggers, TimeProvider? clock = null, TokenExchangeClient? exchange = null,
    UserConnections? connections = null, CredentialStatusRegistry? credentialStatus = null)
    : IToolSource, IAsyncDisposable
{
    /// <summary>A fixed server list (tests, single-profile setups).</summary>
    public McpToolSource(IReadOnlyList<McpServerConfig> servers, ILoggerFactory loggers, TimeProvider? clock = null, TokenExchangeClient? exchange = null)
        : this(() => servers, loggers, clock, exchange) { }

    /// <summary>The servers right now: profiles applied through the admin API add or change servers without a restart.</summary>
    private IReadOnlyList<McpServerConfig> servers => serverList();

    private static readonly TimeSpan CatalogTtl = TimeSpan.FromSeconds(60);

    private sealed class State(McpClient client)
    {
        public McpClient Client { get; } = client;
        public List<ToolDescriptor> Tools { get; set; } = [];
        public DateTimeOffset At { get; set; } = DateTimeOffset.MinValue;
    }

    // Per server (and user, for per-user servers): a slow or dead server never blocks calls to the others.
    private readonly ConcurrentDictionary<(string Server, string User), SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<(string Server, string User), State> _states = new();
    private readonly ConcurrentDictionary<string, (string Error, DateTimeOffset RetryAt, int Failures)> _failures = new();

    /// <summary>After a failure a server is skipped for a while (30 s, doubling to 5 min) instead of being retried on every call.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(2);

    private static TimeSpan Backoff(int failures) => TimeSpan.FromSeconds(Math.Min(300, 30 * Math.Pow(2, Math.Max(0, failures - 1))));

    /// <summary>Reached with the run user's own token (exchanged or connected), so clients and catalogs are kept per user.</summary>
    private static bool IsDelegated(McpServerConfig s) => s.Auth is AuthStrategies.Delegated or AuthStrategies.UserConnected;

    /// <summary>"" for shared servers, the user for delegated ones, null if a delegated server has no user context.</summary>
    private static string? UserKey(McpServerConfig s) => IsDelegated(s) ? DelegationContext.Current?.UserId : "";

    public async Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct)
    {
        var list = servers;
        if (list.Count == 0) return [];
        var states = await Task.WhenAll(list.Select(s => UserKey(s) is { } user ? RefreshAsync(s, user, ct) : Task.FromResult<State?>(null)));
        var catalog = new List<ToolDescriptor>();
        foreach (var state in states.Where(s => s is not null))
            foreach (var tool in state!.Tools)
                if (catalog.All(t => t.Name != tool.Name)) // first server wins on name clashes
                    catalog.Add(tool);
        return catalog;
    }

    public async Task<IReadOnlyList<ServerStatus>> StatusAsync(CancellationToken ct)
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
                ? new ServerStatus(server.Name, server.Url, server.Auth, "unavailable", [],
                    _failures.TryGetValue(server.Name, out var f) ? $"{f.Error} (next try {f.RetryAt:HH:mm:ss})" : null, DateTimeOffset.UtcNow)
                : new ServerStatus(server.Name, server.Url, server.Auth, "ok", state.Tools, null, state.At));
        }
        return result;
    }

    public async Task<string?> ServerOfAsync(string toolName, CancellationToken ct)
    {
        await ListAsync(ct);
        return Find(toolName)?.Server.Name;
    }

    public async Task<string> CallAsync(string name, string argumentsJson, CancellationToken ct)
    {
        await ListAsync(ct); // make sure the catalog of the current user's context is loaded (cheap: cached)
        var client = Find(name)?.State.Client ?? throw new InvalidOperationException($"No MCP server provides tool '{name}'.");

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

    private (McpServerConfig Server, State State)? Find(string toolName)
    {
        foreach (var server in servers)
            if (UserKey(server) is { } user
                && _states.TryGetValue((server.Name, user), out var state)
                && state.Tools.Any(t => t.Name == toolName))
                return (server, state);
        return null;
    }

    /// <summary>Connects (once) and refreshes the tool list (every 60 s). Returns null if the server is unavailable or backing off.</summary>
    private async Task<State?> RefreshAsync(McpServerConfig server, string user, CancellationToken ct)
    {
        var key = (server.Name, user);
        if (_failures.TryGetValue(server.Name, out var failed) && DateTimeOffset.UtcNow < failed.RetryAt && !_states.ContainsKey(key)) return null;
        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        // Someone else is connecting or refreshing: use what is cached rather than queue behind a slow server.
        if (!await gate.WaitAsync(LockWait, ct)) return _states.GetValueOrDefault(key);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ConnectTimeout);
        try
        {
            if (!_states.TryGetValue(key, out var state))
            {
                state = new State(await ConnectAsync(server, limit.Token));
                _states[key] = state;
            }

            if (DateTimeOffset.UtcNow - state.At >= CatalogTtl)
            {
                state.Tools = (await state.Client.ListToolsAsync(cancellationToken: limit.Token))
                    .Select(t => new ToolDescriptor(t.Name, t.Description ?? "", t.JsonSchema.Clone()))
                    .ToList();
                state.At = DateTimeOffset.UtcNow;
            }
            _failures.TryRemove(server.Name, out _);
            return state;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (ex is OperationCanceledException) ex = new TimeoutException($"no answer within {ConnectTimeout.TotalSeconds:0} s");
            var failures = (_failures.TryGetValue(server.Name, out var f) ? f.Failures : 0) + 1;
            _failures[server.Name] = (ex.Message, DateTimeOffset.UtcNow + Backoff(failures), failures);
            loggers.CreateLogger<McpToolSource>().LogWarning("MCP server {Server} unavailable (attempt {Attempt}, next in {Delay}): {Error}",
                server.Name, failures, Backoff(failures), ex.Message);
            if (_states.TryRemove(key, out var dead)) await dead.Client.DisposeAsync(); // reconnect next time
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<McpClient> ConnectAsync(McpServerConfig server, CancellationToken ct)
    {
        var options = new HttpClientTransportOptions { Endpoint = new Uri(server.Url), Name = server.Name };
        IClientTransport transport;
        if (server.Auth == AuthStrategies.UserConnected)
        {
            var c = connections ?? throw new InvalidOperationException($"Server '{server.Name}' is user-connected but connections are not configured.");
            transport = new HttpClientTransport(options, new HttpClient(new UserConnectedBearerHandler(server, c)), loggers, ownsHttpClient: true);
        }
        else if (IsDelegated(server))
        {
            var ex = exchange ?? throw new InvalidOperationException($"Server '{server.Name}' is delegated but no token exchange is configured.");
            transport = new HttpClientTransport(options, new HttpClient(new DelegatedBearerHandler(server, ex)), loggers, ownsHttpClient: true);
        }
        else if (server.Credentials is { } credentials)
        {
            // The token is attached per request, so it is refreshed transparently when it expires.
            var provider = new BackendTokenProvider(credentials, new HttpClient(), clock ?? TimeProvider.System, server: server.Name, status: credentialStatus);
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
