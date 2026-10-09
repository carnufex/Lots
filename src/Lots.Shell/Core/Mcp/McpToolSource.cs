using System.Text;
using System.Text.Json;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Tools;
using ModelContextProtocol.Client;

namespace Lots.Shell.Core.Mcp;

/// <summary>Exposes the tools of the configured MCP servers to the <see cref="ToolInvoker"/>.</summary>
/// <remarks>Risk classes are not decided here: they come from the profile, and policy decides per call.</remarks>
public sealed class McpToolSource(
    IReadOnlyList<McpServerConfig> servers, ILoggerFactory loggers, TimeProvider? clock = null) : IToolSource, IAsyncDisposable
{
    private static readonly TimeSpan CatalogTtl = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, McpClient> _clients = [];
    private readonly Dictionary<string, string> _toolServer = [];
    private List<ToolDescriptor> _catalog = [];
    private DateTimeOffset _catalogAt = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct)
    {
        if (servers.Count == 0) return [];

        await _gate.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow - _catalogAt < CatalogTtl) return _catalog;

            var catalog = new List<ToolDescriptor>();
            _toolServer.Clear();
            foreach (var server in servers)
            {
                try
                {
                    var client = await ClientAsync(server, ct);
                    foreach (var tool in await client.ListToolsAsync(cancellationToken: ct))
                    {
                        if (_toolServer.ContainsKey(tool.Name)) continue; // first server wins on name clashes
                        _toolServer[tool.Name] = server.Name;
                        catalog.Add(new ToolDescriptor(tool.Name, tool.Description ?? "", tool.JsonSchema.Clone()));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    loggers.CreateLogger<McpToolSource>().LogWarning(ex, "MCP server {Server} unavailable", server.Name);
                    _clients.Remove(server.Name); // reconnect next time
                }
            }

            _catalog = catalog;
            _catalogAt = DateTimeOffset.UtcNow;
            return _catalog;
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
        try { return _toolServer.GetValueOrDefault(toolName); }
        finally { _gate.Release(); }
    }

    public async Task<string> CallAsync(string name, string argumentsJson, CancellationToken ct)
    {
        McpClient client;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_toolServer.TryGetValue(name, out var serverName))
                throw new InvalidOperationException($"No MCP server provides tool '{name}'.");
            client = _clients[serverName];
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

    private async Task<McpClient> ClientAsync(McpServerConfig server, CancellationToken ct)
    {
        if (_clients.TryGetValue(server.Name, out var existing)) return existing;
        var options = new HttpClientTransportOptions { Endpoint = new Uri(server.Url), Name = server.Name };
        IClientTransport transport;
        if (server.Credentials is { } credentials)
        {
            // The token is attached per request, so it is refreshed transparently when it expires.
            var provider = new BackendTokenProvider(credentials, new HttpClient(), clock ?? TimeProvider.System);
            transport = new HttpClientTransport(options, new HttpClient(new BearerHandler(provider)), loggers, ownsHttpClient: true);
        }
        else
        {
            transport = new HttpClientTransport(options, loggers);
        }
        var client = await McpClient.CreateAsync(transport, loggerFactory: loggers, cancellationToken: ct);
        _clients[server.Name] = client;
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var c in _clients.Values) await c.DisposeAsync();
    }
}
