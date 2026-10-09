using System.ComponentModel;
using Lots.Shell.Core.Mcp;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Lots.Shell.Tests.Features;

[McpServerToolType]
public sealed class SampleTools
{
    [McpServerTool(ReadOnly = true), Description("Echoes the text back")]
    public static string Echo(string text) => $"echo:{text}";

    [McpServerTool(ReadOnly = true), Description("Read-only per its own hint, but not allow-listed by the admin")]
    public static string Peek() => "peek";

    [McpServerTool(Destructive = true), Description("Deletes everything")]
    public static string DeleteAll() => "deleted";
}

public sealed class McpServerFixture : IAsyncLifetime
{
    private WebApplication? _app;
    public string Url { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["urls"] = "http://127.0.0.1:0";
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<SampleTools>();
        _app = builder.Build();
        _app.MapMcp("/mcp");
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Url = address.TrimEnd('/') + "/mcp";
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}

public class McpToolSourceTests(McpServerFixture server) : IClassFixture<McpServerFixture>
{
    private McpToolSource Source(Action<McpServerConfig>? configure = null)
    {
        var s = new McpServerConfig { Name = "sample", Url = server.Url, ReadTools = ["echo"] };
        configure?.Invoke(s);
        return new McpToolSource(Options.Create(new McpOptions { Servers = [s] }), NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task Lists_tools_with_deny_by_default_risk()
    {
        await using var source = Source();

        var tools = (await source.ListAsync(default)).ToDictionary(t => t.Name);

        Assert.Equal(ToolRisk.Read, tools["echo"].Risk);
        Assert.NotEqual(ToolRisk.Read, tools["peek"].Risk); // the server's own hint is not trusted
        Assert.Equal(ToolRisk.Destructive, tools["delete_all"].Risk);
    }

    [Fact]
    public async Task Trusting_the_read_only_hint_is_opt_in()
    {
        await using var source = Source(s => s.TrustReadOnlyHint = true);

        var tools = (await source.ListAsync(default)).ToDictionary(t => t.Name);

        Assert.Equal(ToolRisk.Read, tools["peek"].Risk);
        Assert.Equal(ToolRisk.Destructive, tools["delete_all"].Risk);
    }

    [Fact]
    public async Task Loop_can_call_a_tool_on_an_external_mcp_server_through_the_invoker()
    {
        await using var source = Source();
        var invoker = new ToolInvoker([source]);

        var definitions = await invoker.DefinitionsAsync(default);
        var ok = await invoker.InvokeAsync(new ToolCall("1", "echo", "{\"text\":\"hi\"}"), default);
        var denied = await invoker.InvokeAsync(new ToolCall("2", "delete_all", "{}"), default);

        Assert.Equal(["echo"], definitions.Select(d => d.Name));
        Assert.Equal("echo:hi", ok);
        Assert.Contains("not permitted", denied);
    }

    [Fact]
    public async Task Unreachable_server_yields_no_tools_instead_of_failing()
    {
        await using var source = new McpToolSource(
            Options.Create(new McpOptions { Servers = [new McpServerConfig { Name = "down", Url = "http://127.0.0.1:1/mcp" }] }),
            NullLoggerFactory.Instance);

        Assert.Empty(await source.ListAsync(default));
    }
}
