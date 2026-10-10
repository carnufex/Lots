using System.Net;
using System.Text;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Runs;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Tests.Features;

public class ModelRoutingTests
{
    private const string Ok = """{"choices":[{"message":{"role":"assistant","content":"hej"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";

    /// <summary>Per endpoint: what it answers (status) or that it is unreachable (null). Records the model asked for.</summary>
    private sealed class Endpoints(Dictionary<string, HttpStatusCode?> behaviour) : IHttpClientFactory
    {
        public List<string> Calls { get; } = [];

        private sealed class Handler(string name, HttpStatusCode? status, List<string> calls) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
                calls.Add($"{name}:{System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("model").GetString()}");
                if (status is null) throw new HttpRequestException("connection refused");
                return new HttpResponseMessage(status.Value)
                {
                    Content = new StringContent(status == HttpStatusCode.OK ? Ok : "{\"error\":\"x\"}", Encoding.UTF8, "application/json"),
                };
            }
        }

        public HttpClient CreateClient(string name)
        {
            var endpoint = name["model:".Length..];
            return new HttpClient(new Handler(endpoint, behaviour[endpoint], Calls)) { BaseAddress = new Uri($"http://{endpoint}/v1/") };
        }
    }

    private static ModelCatalog Catalog(ModelsOptions models, ModelOptions? legacy = null) =>
        new(Options.Create(models), Options.Create(legacy ?? new ModelOptions { Model = "legacy-model" }));

    private static ModelsOptions TwoEndpoints() => new()
    {
        Endpoints =
        {
            ["gpu"] = new ModelEndpointOptions { BaseUrl = "http://gpu/v1" },
            ["cpu"] = new ModelEndpointOptions { BaseUrl = "http://cpu/v1" },
        },
        Aliases =
        {
            ["default"] = new ModelAlias { Targets = [new() { Endpoint = "gpu", Model = "big" }, new() { Endpoint = "cpu", Model = "small" }] },
            ["voice"] = new ModelAlias { Targets = [new() { Endpoint = "gpu", Model = "fast" }] },
        },
    };

    private static Task<ModelResponse> Ask(ModelCatalog catalog, Endpoints http, string? alias = null) =>
        new RoutingModelClient(catalog, http, NullLogger<RoutingModelClient>.Instance)
            .CompleteAsync([new ChatMessage("user", "hej")], [], new ModelCallOptions(Alias: alias), CancellationToken.None);

    [Theory]
    [InlineData(null)]                              // unreachable
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_failing_primary_falls_back_and_the_response_says_who_answered(HttpStatusCode? primary)
    {
        var http = new Endpoints(new() { ["gpu"] = primary, ["cpu"] = HttpStatusCode.OK });

        var response = await Ask(Catalog(TwoEndpoints()), http);

        Assert.Equal(["gpu:big", "cpu:small"], http.Calls);
        Assert.Equal(("small", "cpu", "hej"), (response.Model, response.Endpoint, response.Message.Content));
    }

    [Fact]
    public async Task A_bad_request_is_not_retried_elsewhere()
    {
        var http = new Endpoints(new() { ["gpu"] = HttpStatusCode.BadRequest, ["cpu"] = HttpStatusCode.OK });

        await Assert.ThrowsAsync<HttpRequestException>(() => Ask(Catalog(TwoEndpoints()), http));
        Assert.Equal(["gpu:big"], http.Calls);
    }

    [Fact]
    public async Task Aliases_pick_their_model_and_unknown_aliases_use_default()
    {
        var http = new Endpoints(new() { ["gpu"] = HttpStatusCode.OK, ["cpu"] = HttpStatusCode.OK });
        var catalog = Catalog(TwoEndpoints());

        await Ask(catalog, http, "voice");
        await Ask(catalog, http, "nope");

        Assert.Equal(["gpu:fast", "gpu:big"], http.Calls);
    }

    [Fact]
    public void The_legacy_model_section_becomes_the_default_alias_and_bad_references_fail()
    {
        var catalog = Catalog(new ModelsOptions(), new ModelOptions { BaseUrl = "http://ollama:11434/v1", Model = "qwen" });
        Assert.Equal("http://ollama:11434/v1", catalog.Endpoints["default"].BaseUrl);
        Assert.Equal("qwen", catalog.PrimaryModel(null));

        var bad = new ModelsOptions { Aliases = { ["x"] = new ModelAlias { Targets = [new() { Endpoint = "missing", Model = "m" }] } } };
        Assert.Throws<InvalidOperationException>(() => Catalog(bad));
    }

    private sealed class AliasRecorder : IModelClient
    {
        public List<string?> Aliases { get; } = [];

        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, CancellationToken ct) =>
            CompleteAsync(m, t, new ModelCallOptions(), ct);

        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, ModelCallOptions o, CancellationToken ct)
        {
            Aliases.Add(o.Alias);
            return Task.FromResult(new ModelResponse(new ChatMessage("assistant", "svar"), "stop", new ModelUsage(1, 1), TimeSpan.FromMilliseconds(1), "m", "gpu"));
        }
    }

    [Theory]
    [InlineData(false, true, "tools")]   // text run: the profile's alias
    [InlineData(true, true, "voice")]    // voice run: the voice alias when configured
    [InlineData(true, false, "tools")]   // no voice alias: the profile's
    public async Task Runs_use_the_profile_alias_and_voice_runs_the_voice_alias(bool voice, bool voiceAlias, string expected)
    {
        var db = new LotsDbContext(new DbContextOptionsBuilder<LotsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var run = new RunRecord
        {
            Id = Guid.NewGuid(), Prompt = "p", Profile = "p", Voice = voice, UserId = "u", Roles = "operator",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        var registry = new ProfileRegistry([new Profile("p", 1, "", "", [], [], [], Model: "tools")]);
        var models = TwoEndpoints();
        models.Aliases["tools"] = models.Aliases["default"];
        if (!voiceAlias) models.Aliases.Remove("voice");
        var recorder = new AliasRecorder();

        await new AgentRunner(db, recorder, new ToolInvoker([], registry), registry, Options.Create(new AgentOptions()), TimeProvider.System,
            catalog: Catalog(models)).ExecuteAsync(run.Id, default);

        Assert.Equal(expected, Assert.Single(recorder.Aliases));
        var step = Assert.Single(db.RunSteps.Where(s => s.RunId == run.Id));
        Assert.Equal(("m", "gpu"), (step.Name, step.Endpoint));
    }
}
