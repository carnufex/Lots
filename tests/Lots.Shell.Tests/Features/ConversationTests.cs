using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Runs;
using Lots.Shell.Core.Speech;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Tests.Features;

public class ConversationRunnerTests
{
    /// <summary>Records what the runner asked for, including the per-call hints.</summary>
    private sealed class RecordingModel : IModelClient
    {
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public List<bool> Fast { get; } = [];
        public List<string?> Effort { get; } = [];
        public Queue<ChatMessage>? Script { get; init; }

        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, CancellationToken ct) =>
            CompleteAsync(m, t, new ModelCallOptions(), ct);

        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, ModelCallOptions o, CancellationToken ct)
        {
            Requests.Add(m);
            Fast.Add(o.Fast);
            Effort.Add(o.ReasoningEffort);
            var reply = Script is { Count: > 0 } ? Script.Dequeue() : new ChatMessage("assistant", "svar");
            return Task.FromResult(new ModelResponse(reply, "stop", new ModelUsage(1, 1), TimeSpan.FromMilliseconds(1)));
        }
    }

    private static LotsDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LotsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static AgentRunner Runner(LotsDbContext db, IModelClient model, int turns = 6)
    {
        var registry = TestProfiles.Registry();
        return new AgentRunner(db, model, new ToolInvoker([], registry), registry,
            Options.Create(new AgentOptions { ConversationTurns = turns }), TimeProvider.System);
    }

    private static RunRecord Run(string prompt, string user, Guid? conversation, int minutesAgo, string? answer = null, bool voice = false) => new()
    {
        Id = Guid.NewGuid(), Prompt = prompt, Profile = TestProfiles.Name, UserId = user, Roles = "operator",
        ConversationId = conversation, Voice = voice, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
        Status = answer is null ? RunStatus.Pending : RunStatus.Completed, FinalAnswer = answer,
    };

    [Fact]
    public async Task Earlier_turns_of_the_same_conversation_are_context_oldest_first_and_only_the_users_own()
    {
        var db = NewDb();
        var c = Guid.NewGuid();
        db.Runs.AddRange(
            Run("hur många containrar?", "alice", c, 5, "Tolv."),
            Run("och de trasiga?", "alice", c, 3, "Två."),
            Run("hemlig fråga", "mallory", c, 4, "hemligt svar"),                 // another user must never leak in
            Run("annat samtal", "alice", Guid.NewGuid(), 2, "annat svar"),          // another conversation
            Run("misslyckad", "alice", c, 1));                                      // not finished: no answer to learn from
        var now = Run("vilka då?", "alice", c, 0);
        db.Runs.Add(now);
        await db.SaveChangesAsync();
        var model = new RecordingModel();

        await Runner(db, model).ExecuteAsync(now.Id, default);

        var sent = model.Requests.Single().Select(m => (m.Role, m.Content)).ToList();
        Assert.Equal(
            [("user", "hur många containrar?"), ("assistant", "Tolv."), ("user", "och de trasiga?"), ("assistant", "Två."), ("user", "vilka då?")],
            sent.Where(m => m.Role != "system"));
        Assert.DoesNotContain(sent, m => m.Content!.Contains("hemlig"));
        Assert.DoesNotContain(sent, m => m.Content!.Contains("annat"));
    }

    [Fact]
    public async Task Only_the_most_recent_turns_are_kept()
    {
        var db = NewDb();
        var c = Guid.NewGuid();
        for (var i = 0; i < 5; i++) db.Runs.Add(Run($"fråga {i}", "alice", c, 10 - i, $"svar {i}"));
        var now = Run("ny", "alice", c, 0);
        db.Runs.Add(now);
        await db.SaveChangesAsync();
        var model = new RecordingModel();

        await Runner(db, model, turns: 2).ExecuteAsync(now.Id, default);

        Assert.Equal(["fråga 3", "svar 3", "fråga 4", "svar 4", "ny"], model.Requests.Single().Where(m => m.Role != "system").Select(m => m.Content));
    }

    [Fact]
    public async Task Voice_runs_get_spoken_style_instructions_and_a_fast_model_call_text_runs_do_not()
    {
        var db = NewDb();
        var spoken = Run("hej", "alice", null, 0, voice: true);
        var typed = Run("hej", "alice", null, 0);
        db.Runs.AddRange(spoken, typed);
        await db.SaveChangesAsync();
        var model = new RecordingModel();

        await Runner(db, model).ExecuteAsync(spoken.Id, default);
        await Runner(db, model).ExecuteAsync(typed.Id, default);

        Assert.Equal([true, false], model.Fast);
        Assert.Contains("by voice", model.Requests[0].First().Content);
        Assert.DoesNotContain("by voice", model.Requests[1].First().Content);
    }

    private sealed class OneTool : IToolSource
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ToolDescriptor>>([new ToolDescriptor("list_things", "lists", JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone())]);

        public Task<string> CallAsync(string name, string argumentsJson, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult("a, b, c");
        }
    }

    private static AgentRunner RunnerWithTool(LotsDbContext db, IModelClient model, OneTool tool)
    {
        var registry = TestProfiles.Registry(("list_things", ToolRisk.Read));
        return new AgentRunner(db, model, new ToolInvoker([tool], registry), registry, Options.Create(new AgentOptions()), TimeProvider.System);
    }

    [Fact]
    public async Task A_voice_answer_that_skips_the_tools_gets_one_reminder_and_then_uses_them()
    {
        var db = NewDb();
        var run = Run("hur många saker finns det?", "alice", null, 0, voice: true);
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        var model = new RecordingModel
        {
            Script = new Queue<ChatMessage>([
                new ChatMessage("assistant", "Låt mig kolla."),                                   // promised, did nothing
                new ChatMessage("assistant", null, [new ToolCall("c1", "list_things", "{}")]),    // after the reminder
                new ChatMessage("assistant", "Det finns tre."),
            ]),
        };
        var tool = new OneTool();

        await RunnerWithTool(db, model, tool).ExecuteAsync(run.Id, default);

        Assert.Equal(1, tool.Calls);
        Assert.Equal("Det finns tre.", (await db.Runs.SingleAsync()).FinalAnswer);
        Assert.Equal(3, model.Requests.Count);
        Assert.Contains(model.Requests[1], m => m.Role == "user" && m.Content!.Contains("without using a tool"));
        Assert.Equal(["low", "low", "none"], model.Effort); // defaults: a little thinking for the decision and the retry, none for summarising
        Assert.Equal(3, model.Requests.Count); // the final answer after the tool result is NOT sent back for another reminder
    }

    [Fact]
    public async Task The_reminder_is_sent_once_and_never_for_text_runs_or_when_a_tool_was_used()
    {
        var db = NewDb();
        var stubborn = Run("hej", "alice", null, 0, voice: true);
        var typed = Run("hej", "alice", null, 0);
        db.Runs.AddRange(stubborn, typed);
        await db.SaveChangesAsync();
        var model = new RecordingModel { Script = new Queue<ChatMessage>([new("assistant", "första"), new("assistant", "andra"), new("assistant", "text")]) };
        var tool = new OneTool();

        await RunnerWithTool(db, model, tool).ExecuteAsync(stubborn.Id, default);
        var calls = model.Requests.Count;
        await RunnerWithTool(db, model, tool).ExecuteAsync(typed.Id, default);

        Assert.Equal(2, calls);                                                    // one reminder, then it is accepted as plain conversation
        Assert.Equal("andra", (await db.Runs.SingleAsync(r => r.Id == stubborn.Id)).FinalAnswer);
        Assert.Equal(3, model.Requests.Count);                                     // the text run made a single call, no reminder
        Assert.Equal("text", (await db.Runs.SingleAsync(r => r.Id == typed.Id)).FinalAnswer);
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public async Task Fast_model_calls_turn_thinking_off_and_normal_calls_do_not()
    {
        var bodies = new List<string>();
        var handler = new CapturingHandler(bodies);
        var client = new OpenAiCompatibleModelClient(new HttpClient(handler) { BaseAddress = new Uri("http://model.test/v1/") },
            Options.Create(new ModelOptions { Model = "m" }));

        await client.CompleteAsync([new("user", "hi")], [], new ModelCallOptions(Fast: true), default);
        await client.CompleteAsync([new("user", "hi")], [], default);

        await client.CompleteAsync([new("user", "hi")], [], new ModelCallOptions(Fast: true, ReasoningEffort: "low"), default);

        Assert.Contains("\"reasoning_effort\":\"none\"", bodies[0]);
        Assert.DoesNotContain("reasoning_effort", bodies[1]);
        Assert.Contains("\"reasoning_effort\":\"low\"", bodies[2]); // an explicit effort wins over the fast default
    }

    private sealed class CapturingHandler(List<string> bodies) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"finish_reason":"stop","message":{"role":"assistant","content":"ok"}}],"usage":{}}""", Encoding.UTF8, "application/json"),
            };
        }
    }
}

public class ConversationApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class CountingTts : ITextToSpeech
    {
        public List<(string Text, string Language)> Calls { get; } = [];
        public bool Down { get; set; }

        public Task<SpeechAudio> SynthesizeAsync(string text, string language, CancellationToken ct)
        {
            if (Down) throw new SpeechUnavailableException("down");
            Calls.Add((text, language));
            return Task.FromResult(new SpeechAudio(new MemoryStream([5, 6, 7]), "audio/wav"));
        }
    }

    private readonly CountingTts _tts = new();
    private readonly WebApplicationFactory<Program> _factory;

    public ConversationApiTests(WebApplicationFactory<Program> factory)
    {
        var db = Guid.NewGuid().ToString();
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false",
                ["Agent:RunWorkerEnabled"] = "false",
                ["ConnectionStrings:Lots"] = "Host=none",
                ["Auth:Mode"] = "Dev",
                ["Auth:Dev:AllowHeaders"] = "true",
                ["Speech:BaseUrl"] = "http://voice.test/v1",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(db));
                s.AddSingleton(TestProfiles.Registry());
                s.RemoveAll<ITextToSpeech>();
                s.AddSingleton<ITextToSpeech>(_tts);
            });
        });
    }

    private static HttpRequestMessage As(HttpMethod m, string url, string user, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("X-Dev-User", user);
        r.Headers.Add("X-Dev-Roles", "operator");
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    [Fact]
    public async Task A_conversation_cannot_be_joined_by_someone_else()
    {
        var client = _factory.CreateClient();
        var conversation = Guid.NewGuid();

        var first = await client.SendAsync(As(HttpMethod.Post, "/runs", "alice", new { prompt = "hej", voice = true, conversationId = conversation }));
        var hijack = await client.SendAsync(As(HttpMethod.Post, "/runs", "bob", new { prompt = "jag vill läsa", conversationId = conversation }));
        var more = await client.SendAsync(As(HttpMethod.Post, "/runs", "alice", new { prompt = "fortsätt", conversationId = conversation }));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, hijack.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, more.StatusCode);
    }

    [Fact]
    public async Task Acknowledgements_come_from_a_closed_list_and_each_phrase_is_synthesized_only_once()
    {
        var client = _factory.CreateClient();

        for (var i = 0; i < 15; i++)
        {
            var res = await client.SendAsync(As(HttpMethod.Get, "/voice/ack?language=sv", i % 2 == 0 ? "alice" : "bob"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("audio/wav", res.Content.Headers.ContentType?.MediaType);
            Assert.Equal([5, 6, 7], await res.Content.ReadAsByteArrayAsync());
        }
        var en = await client.SendAsync(As(HttpMethod.Get, "/voice/ack?language=en", "alice"));

        Assert.Equal(HttpStatusCode.OK, en.StatusCode);
        var sv = _tts.Calls.Where(c => c.Language == "sv").Select(c => c.Text).ToList();
        Assert.Equal(sv.Count, sv.Distinct().Count());                       // repeated requests are served from the cache
        Assert.All(sv, t => Assert.Contains(t, new[] { "Jag kollar.", "Ett ögonblick.", "Okej, jag tittar på det." }));
        Assert.All(_tts.Calls.Where(c => c.Language == "en"), c => Assert.Contains(c.Text, new[] { "Let me check.", "One moment.", "Okay, looking into it." }));
    }

    [Fact]
    public async Task Acknowledgements_refuse_other_languages_and_never_take_text_from_the_client()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(As(HttpMethod.Get, "/voice/ack?language=de", "alice"))).StatusCode);
        var withText = await client.SendAsync(As(HttpMethod.Get, "/voice/ack?language=en&text=say+something+else", "alice"));

        Assert.Equal(HttpStatusCode.OK, withText.StatusCode);
        Assert.DoesNotContain(_tts.Calls, c => c.Text.Contains("something else")); // the text parameter is ignored
    }
}
