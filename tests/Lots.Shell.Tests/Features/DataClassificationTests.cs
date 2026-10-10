using System.Net;
using System.Text;
using System.Text.Json;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Runs;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Tests.Features;

/// <summary>#89: data classes on tools and sources, clearances on model endpoints; sensitive data never reaches an uncleared model.</summary>
public class DataClassificationTests
{
    /// <summary>A hosted endpoint (internal clearance by default) as the default alias, a local one as the sensitive alias.</summary>
    private static ModelCatalog Catalog(bool withSensitiveAlias = true, string? hostedClearance = null) => new(Options.Create(new ModelsOptions
    {
        Endpoints =
        {
            ["cloud"] = new ModelEndpointOptions { BaseUrl = "https://api.example/v1", Location = "hosted", Clearance = hostedClearance },
            ["gpu"] = new ModelEndpointOptions { BaseUrl = "http://gpu/v1" },
        },
        Aliases =
        {
            ["default"] = new ModelAlias { Targets = [new() { Endpoint = "cloud", Model = "big" }] },
            ["local"] = new ModelAlias { Targets = [new() { Endpoint = "gpu", Model = "qwen" }] },
        },
        SensitiveAlias = withSensitiveAlias ? "local" : null,
    }), Options.Create(new ModelOptions()));

    [Fact]
    public void Local_endpoints_are_cleared_for_everything_hosted_ones_for_internal_unless_configured()
    {
        Assert.Equal(DataClass.Internal, Catalog().ClearanceOf("cloud"));
        Assert.Equal(DataClass.Restricted, Catalog().ClearanceOf("gpu"));
        Assert.Equal(DataClass.Public, Catalog(hostedClearance: "public").ClearanceOf("cloud"));
        Assert.Throws<InvalidOperationException>(() => Catalog(hostedClearance: "secret"));
    }

    [Fact]
    public void Calls_stay_on_their_alias_while_the_data_allows_it_and_are_rerouted_or_blocked_above_it()
    {
        var own = Catalog().Route(null, DataClass.Internal);
        Assert.Equal(("default", null), (own.Alias, own.Rerouted));

        var rerouted = Catalog().Route(null, DataClass.Confidential);
        Assert.Equal("local", rerouted.Alias);
        Assert.Equal(["gpu"], rerouted.Targets.Select(t => t.Endpoint));
        Assert.Contains("confidential", rerouted.Rerouted);

        var blocked = Assert.Throws<DataClassificationException>(() => Catalog(withSensitiveAlias: false).Route(null, DataClass.Confidential));
        Assert.Contains("confidential", blocked.Message);
    }

    [Fact]
    public async Task The_routing_client_never_contacts_an_endpoint_that_is_not_cleared()
    {
        var http = new RecordingEndpoints();
        var client = new RoutingModelClient(Catalog(), http, NullLogger<RoutingModelClient>.Instance);

        var response = await client.CompleteAsync([new ChatMessage("user", "x")], [], new ModelCallOptions(Data: DataClass.Restricted), default);

        Assert.Equal(["gpu"], http.Calls);
        Assert.Equal("gpu", response.Endpoint);
        Assert.NotNull(response.Rerouted);
        await Assert.ThrowsAsync<DataClassificationException>(() => new RoutingModelClient(Catalog(withSensitiveAlias: false), http,
            NullLogger<RoutingModelClient>.Instance).CompleteAsync([new ChatMessage("user", "x")], [], new ModelCallOptions(Data: DataClass.Restricted), default));
        Assert.Equal(["gpu"], http.Calls); // the hosted endpoint was never called
    }

    [Fact]
    public void Profiles_declare_sensitivity_per_profile_and_per_tool()
    {
        var p = ProfileParser.Parse("""
            name: hr
            version: 1
            sensitivity: confidential
            tools:
              - { name: list_people, risk: read }
              - { name: salary, risk: read, sensitivity: restricted }
              - { name: holidays, risk: read, sensitivity: public }
            roles:
              - { name: operator, allow: [read] }
            """);
        Assert.Equal(DataClass.Confidential, p.SensitivityOf("list_people"));
        Assert.Equal(DataClass.Restricted, p.SensitivityOf("salary"));
        Assert.Equal(DataClass.Public, p.SensitivityOf("holidays"));
        var ex = Assert.Throws<ProfileException>(() => ProfileParser.Parse("name: x\nversion: 1\nsensitivity: top-secret\n"));
        Assert.Contains(ex.Errors, e => e.Contains("sensitivity"));
    }

    private static ProfileRegistry Registry() => new([new Profile("p", 1, "", "", [],
        [new ProfileTool("weather", ToolRisk.Read, DataClass.Public), new ProfileTool("payroll", ToolRisk.Read, DataClass.Restricted),
         new ProfileTool("tickets", ToolRisk.Read, DataClass.Confidential)],
        [new ProfileRole("operator", [ToolRisk.Read], [])])]);

    [Fact]
    public async Task Tools_no_model_of_the_profile_may_see_are_hidden_and_denied()
    {
        var tools = new Tools(_ => "ok");
        var hostedOnly = new ToolInvoker([tools], Registry(), Catalog(withSensitiveAlias: false));
        var me = new Principal("u", ["operator"]);

        var visible = (await hostedOnly.DefinitionsAsync(me, "p", default)).Select(d => d.Name).ToList();
        Assert.Equal(["weather"], visible);
        Assert.Equal(Decision.Deny, hostedOnly.Evaluate(new ToolCall("1", "payroll", "{}"), me, "p").Decision);
        Assert.Contains("not permitted", await hostedOnly.InvokeAsync(new ToolCall("1", "payroll", "{}"), me, "p", default));
        Assert.Contains("restricted", hostedOnly.Evaluate(new ToolCall("1", "payroll", "{}"), me, "p").Reason);
        Assert.DoesNotContain("payroll", tools.Called);

        // With a local model to fall back to, the tools are usable.
        var withLocal = new ToolInvoker([tools], Registry(), Catalog());
        Assert.Equal(3, (await withLocal.DefinitionsAsync(me, "p", default)).Count);
    }

    [Fact]
    public async Task A_run_takes_the_class_of_what_it_read_and_later_model_calls_carry_it_and_are_audited_when_rerouted()
    {
        var db = new LotsDbContext(new DbContextOptionsBuilder<LotsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var run = new RunRecord { Id = Guid.NewGuid(), Prompt = "open tickets?", Profile = "p", UserId = "u", Roles = "operator", CreatedAt = DateTimeOffset.UtcNow };
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        var model = new OptionsRecorder(
            _ => new ChatMessage("assistant", null, [new ToolCall("c1", "weather", "{}")]),
            _ => new ChatMessage("assistant", null, [new ToolCall("c2", "tickets", "{}")]),
            _ => new ChatMessage("assistant", "two open tickets"));
        var registry = Registry();
        var catalog = Catalog();
        await new AgentRunner(db, model, new ToolInvoker([new Tools(_ => "ok")], registry, catalog), registry,
            Options.Create(new AgentOptions()), TimeProvider.System, catalog: catalog).ExecuteAsync(run.Id, default);

        var done = await db.Runs.Include(r => r.Steps).SingleAsync();
        Assert.Equal(RunStatus.Completed, done.Status);
        Assert.Equal(DataClass.Confidential, done.Sensitivity);
        Assert.Equal([DataClass.Public, DataClass.Public, DataClass.Confidential], model.Data);
        var audit = Assert.Single(await db.AuditLog.Where(a => a.Decision == AuditDecision.ModelRerouted).ToListAsync());
        Assert.Equal("model:default", audit.Tool);
        Assert.Contains("rerouted", done.Steps.Last(s => s.Kind == StepKind.ModelCall).Routing);
    }

    private sealed class RecordingEndpoints : IHttpClientFactory
    {
        public List<string> Calls { get; } = [];

        private sealed class Handler(string name, List<string> calls) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                calls.Add(name);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""",
                        Encoding.UTF8, "application/json"),
                });
            }
        }

        public HttpClient CreateClient(string name) =>
            new(new Handler(name["model:".Length..], Calls)) { BaseAddress = new Uri($"http://{name["model:".Length..]}/v1/") };
    }

    /// <summary>A scripted model that records the data class of every call; it answers as if it were the routing client.</summary>
    private sealed class OptionsRecorder(params Func<IReadOnlyList<ChatMessage>, ChatMessage>[] replies) : IModelClient
    {
        public List<DataClass> Data { get; } = [];

        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct) =>
            CompleteAsync(messages, tools, new ModelCallOptions(), ct);

        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, ModelCallOptions options, CancellationToken ct)
        {
            Data.Add(options.Data);
            var route = Catalog().Route(options.Alias, options.Data);
            return Task.FromResult(new ModelResponse(replies[Data.Count - 1](messages), "stop", new ModelUsage(1, 1), TimeSpan.Zero,
                route.Targets[0].Model, route.Targets[0].Endpoint, route.Rerouted));
        }
    }

    private sealed class Tools(Func<string, string> output) : IToolSource
    {
        public List<string> Called { get; } = [];
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ToolDescriptor>>(
            new[] { "weather", "payroll", "tickets" }.Select(n => new ToolDescriptor(n, n, JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone())).ToList());
        public Task<string> CallAsync(string name, string argumentsJson, CancellationToken ct)
        {
            Called.Add(name);
            return Task.FromResult(output(name));
        }
    }
}
