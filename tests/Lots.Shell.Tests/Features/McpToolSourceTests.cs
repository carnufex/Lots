using System.ComponentModel;
using Lots.Shell.Core.Mcp;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
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
    private McpToolSource Source() =>
        new([new McpServerConfig("sample", server.Url)], NullLoggerFactory.Instance);

    private static readonly Principal Operator = new("u1", ["operator"]);

    [Fact]
    public async Task Lists_the_tools_of_the_server()
    {
        await using var source = Source();

        var names = (await source.ListAsync(default)).Select(t => t.Name).Order().ToList();

        Assert.Equal(["delete_all", "echo", "peek"], names);
    }

    [Fact]
    public async Task Loop_can_call_a_tool_on_an_external_mcp_server_through_the_invoker()
    {
        await using var source = Source();
        // echo is declared read, delete_all destructive, peek is not declared at all.
        var registry = TestProfiles.Registry(("echo", ToolRisk.Read), ("delete_all", ToolRisk.Destructive));
        var invoker = new ToolInvoker([source], registry);

        var definitions = await invoker.DefinitionsAsync(Operator, TestProfiles.Name, default);
        var ok = await invoker.InvokeAsync(new ToolCall("1", "echo", "{\"text\":\"hi\"}"), Operator, TestProfiles.Name, default);
        var denied = await invoker.InvokeAsync(new ToolCall("2", "delete_all", "{}"), Operator, TestProfiles.Name, default);
        var undeclared = await invoker.InvokeAsync(new ToolCall("3", "peek", "{}"), Operator, TestProfiles.Name, default);

        Assert.Equal(["echo"], definitions.Select(d => d.Name));
        Assert.Equal("echo:hi", ok);
        Assert.Contains("not permitted", denied);
        Assert.Contains("not permitted", undeclared); // the server's own readOnly hint grants nothing
    }

    [Fact]
    public async Task Unreachable_server_yields_no_tools_instead_of_failing()
    {
        await using var source = new McpToolSource([new McpServerConfig("down", "http://127.0.0.1:1/mcp")], NullLoggerFactory.Instance);

        Assert.Empty(await source.ListAsync(default));
    }
}
