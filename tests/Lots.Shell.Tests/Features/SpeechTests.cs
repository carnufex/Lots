using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lots.Shell.Core.Speech;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Tests.Features;

public class SpeechAdapterTests
{
    private sealed class Provider(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.AbsolutePath, request.Content is null ? "" : Encoding.UTF8.GetString(await request.Content.ReadAsByteArrayAsync(ct))));
            return await respond(request);
        }
    }

    private static OpenAiCompatibleSpeech Adapter(Provider p) =>
        new(new HttpClient(p) { BaseAddress = new Uri("http://voice.test/v1/") }, Options.Create(new SpeechOptions { BaseUrl = "http://voice.test/v1" }));

    [Fact]
    public async Task Transcription_posts_multipart_and_parses_segments_and_language()
    {
        var p = new Provider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"text":" Hej där ","language":"sv","duration":2.5,"segments":[{"start":0.0,"end":2.5,"text":" Hej där "}]}""", Encoding.UTF8, "application/json"),
        }));

        var t = await Adapter(p).TranscribeAsync(new AudioInput(new MemoryStream([1, 2, 3]), "audio/wav", "a.wav"), "sv", default);

        Assert.Equal("Hej där", t.Text);
        Assert.Equal("sv", t.Language);
        Assert.Equal(2.5, t.DurationSeconds);
        Assert.Equal("Hej där", t.Segments.Single().Text);
        Assert.Equal("/v1/audio/transcriptions", p.Requests[0].Path);
        Assert.Contains("name=language", p.Requests[0].Body);
        Assert.Contains("verbose_json", p.Requests[0].Body);
    }

    [Fact]
    public async Task Synthesis_sends_the_configured_voice_and_streams_the_body()
    {
        var p = new Provider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([9, 8, 7]) { Headers = { ContentType = new("audio/wav") } },
        }));

        using var audio = await Adapter(p).SynthesizeAsync("Hej", "sv", default);
        using var ms = new MemoryStream();
        await audio.Content.CopyToAsync(ms);

        Assert.Equal("audio/wav", audio.ContentType);
        Assert.Equal([9, 8, 7], ms.ToArray());
        Assert.Equal("/v1/audio/speech", p.Requests[0].Path);
        Assert.Contains("\"voice\":\"sv-nst\"", p.Requests[0].Body);
        Assert.Contains("\"input\":\"Hej\"", p.Requests[0].Body);
    }

    [Fact]
    public async Task Provider_failures_become_SpeechUnavailable_so_callers_can_fall_back_to_text()
    {
        var down = new Provider(_ => throw new HttpRequestException("connection refused"));
        var error = new Provider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<SpeechUnavailableException>(() => Adapter(down).TranscribeAsync(new AudioInput(new MemoryStream([1]), "audio/wav", "a"), null, default));
        await Assert.ThrowsAsync<SpeechUnavailableException>(() => Adapter(down).SynthesizeAsync("x", "en", default));
        await Assert.ThrowsAsync<SpeechUnavailableException>(() => Adapter(error).TranscribeAsync(new AudioInput(new MemoryStream([1]), "audio/wav", "a"), null, default));
        await Assert.ThrowsAsync<SpeechUnavailableException>(() => Adapter(error).SynthesizeAsync("x", "en", default));
        await Assert.ThrowsAsync<SpeechUnavailableException>(() => Adapter(error).SynthesizeAsync("x", "de", default)); // no voice for the language
    }

    [Theory]
    [InlineData("Två av containrarna är inte friska just nu.", "sv")]
    [InlineData("Jag kollar det åt dig", "sv")]
    [InlineData("Which containers are unhealthy right now?", "en")]
    [InlineData("Hur många kablar är det?", "sv")]
    [InlineData("The database restarted four minutes ago.", "en")]
    [InlineData("42", "en")]
    public void Language_guess_picks_swedish_or_english(string text, string expected) =>
        Assert.Equal(expected, LanguageGuess.Of(text));
}

public class VoiceApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class FakeStt : ISpeechToText
    {
        public bool Down { get; set; }
        public string? LastLanguage { get; private set; }

        public Task<Transcript> TranscribeAsync(AudioInput audio, string? language, CancellationToken ct)
        {
            if (Down) throw new SpeechUnavailableException("down");
            LastLanguage = language;
            return Task.FromResult(new Transcript("vilka containrar är trasiga", language ?? "sv", 3.2, []));
        }
    }

    private sealed class FakeTts : ITextToSpeech
    {
        public List<(string Text, string Language)> Spoken { get; } = [];
        public bool Down { get; set; }

        public Task<SpeechAudio> SynthesizeAsync(string text, string language, CancellationToken ct)
        {
            if (Down) throw new SpeechUnavailableException("down");
            Spoken.Add((text, language));
            return Task.FromResult(new SpeechAudio(new MemoryStream([1, 2, 3, 4]), "audio/wav"));
        }
    }

    private readonly FakeStt _stt = new();
    private readonly FakeTts _tts = new();
    private readonly WebApplicationFactory<Program> _factory;

    public VoiceApiTests(WebApplicationFactory<Program> factory)
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
                ["Speech:BaseUrl"] = "http://voice.test:8700/v1",
                ["Speech:MaxAudioBytes"] = "1000",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(db));
                s.AddSingleton(TestProfiles.Registry());
                s.RemoveAll<ISpeechToText>();
                s.RemoveAll<ITextToSpeech>();
                s.AddSingleton<ISpeechToText>(_stt);
                s.AddSingleton<ITextToSpeech>(_tts);
            });
        });
    }

    private static HttpRequestMessage As(HttpMethod m, string url, string user, string roles, HttpContent? body = null)
    {
        var r = new HttpRequestMessage(m, url) { Content = body };
        r.Headers.Add("X-Dev-User", user);
        r.Headers.Add("X-Dev-Roles", roles);
        return r;
    }

    private static MultipartFormDataContent Audio(int bytes = 100, string? language = null)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(new byte[bytes]) { Headers = { ContentType = new("audio/wav") } }, "Audio", "q.wav" } };
        if (language is not null) form.Add(new StringContent(language), "Language");
        return form;
    }

    private async Task<Guid> SeedRunAsync(string user, RunStatus status, string? answer)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var run = new RunRecord
        {
            Id = Guid.NewGuid(), Prompt = "q", Profile = TestProfiles.Name, UserId = user, Roles = "operator",
            Status = status, FinalAnswer = answer, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private async Task<List<VoiceUsageRecord>> UsageAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LotsDbContext>().VoiceUsage.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task Dictation_returns_the_transcript_and_records_metadata_only()
    {
        var client = _factory.CreateClient();

        var res = await client.SendAsync(As(HttpMethod.Post, "/voice/transcribe", "alice", "operator", Audio(language: "sv")));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("vilka containrar är trasiga", json.GetProperty("text").GetString());
        Assert.Equal("sv", _stt.LastLanguage);
        var row = Assert.Single(await UsageAsync(), u => u.Direction == "Stt");
        Assert.Equal(("alice", "ok", "voice.test"), (row.UserId, row.Outcome, row.Provider));
        Assert.Equal(3.2, row.AudioSeconds);
        Assert.DoesNotContain(typeof(VoiceUsageRecord).GetProperties(), p => p.Name is "Text" or "Audio" or "Content");
    }

    [Fact]
    public async Task Dictation_rejects_bad_input_and_degrades_when_the_provider_is_down()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(As(HttpMethod.Post, "/voice/transcribe", "a", "operator", Audio(language: "de")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(As(HttpMethod.Post, "/voice/transcribe", "a", "operator", Audio(bytes: 0)))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(As(HttpMethod.Post, "/voice/transcribe", "a", "operator", Audio(bytes: 5000)))).StatusCode);

        _stt.Down = true;
        var down = await client.SendAsync(As(HttpMethod.Post, "/voice/transcribe", "a", "operator", Audio()));
        _stt.Down = false;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        Assert.Contains("Type your question", await down.Content.ReadAsStringAsync());
        Assert.Contains(await UsageAsync(), u => u.Outcome == "error");
    }

    [Fact]
    public async Task Only_the_final_answer_of_the_owners_own_run_is_spoken()
    {
        var client = _factory.CreateClient();
        var done = await SeedRunAsync("alice", RunStatus.Completed, "Två av containrarna är inte friska just nu.");
        var running = await SeedRunAsync("alice", RunStatus.Running, null);
        var failed = await SeedRunAsync("alice", RunStatus.Failed, null);

        var ok = await client.SendAsync(As(HttpMethod.Post, $"/runs/{done}/speak", "alice", "operator", JsonContent.Create(new { })));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("audio/wav", ok.Content.Headers.ContentType?.MediaType);
        Assert.Equal([1, 2, 3, 4], await ok.Content.ReadAsByteArrayAsync());
        Assert.Equal(("Två av containrarna är inte friska just nu.", "sv"), _tts.Spoken.Single());

        // someone else's run: not found; admin may; unfinished or failed runs have nothing to say
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(As(HttpMethod.Post, $"/runs/{done}/speak", "bob", "operator", JsonContent.Create(new { })))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(As(HttpMethod.Post, $"/runs/{done}/speak", "root", "admin", JsonContent.Create(new { })))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(As(HttpMethod.Post, $"/runs/{running}/speak", "alice", "operator", JsonContent.Create(new { })))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(As(HttpMethod.Post, $"/runs/{failed}/speak", "alice", "operator", JsonContent.Create(new { })))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(As(HttpMethod.Post, $"/runs/{done}/speak", "alice", "operator", JsonContent.Create(new { language = "fr" })))).StatusCode);
        Assert.Equal(2, _tts.Spoken.Count); // nothing was synthesized for the rejected requests
    }

    [Fact]
    public async Task There_is_no_free_text_to_speech_and_no_approval_by_voice()
    {
        var client = _factory.CreateClient();
        var json = JsonContent.Create(new { text = "read this", language = "en" });

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(As(HttpMethod.Post, "/voice/speak", "alice", "admin", json))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(As(HttpMethod.Post, "/voice/approve", "alice", "admin", JsonContent.Create(new { })))).StatusCode);
        Assert.Empty(_tts.Spoken);
    }

    [Fact]
    public async Task Speech_failure_falls_back_to_text_and_is_recorded()
    {
        var client = _factory.CreateClient();
        var done = await SeedRunAsync("alice", RunStatus.Completed, "Hello there.");

        _tts.Down = true;
        var res = await client.SendAsync(As(HttpMethod.Post, $"/runs/{done}/speak", "alice", "operator", JsonContent.Create(new { })));
        _tts.Down = false;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Contains(await UsageAsync(), u => u is { Direction: "Tts", Outcome: "error", Characters: 12 });
    }

    [Fact]
    public async Task Config_tells_the_client_whether_voice_is_available()
    {
        var client = _factory.CreateClient();

        var config = await client.GetFromJsonAsync<JsonElement>("/config");

        Assert.True(config.GetProperty("voice").GetProperty("enabled").GetBoolean());
        Assert.Equal(["sv", "en"], config.GetProperty("voice").GetProperty("languages").EnumerateArray().Select(l => l.GetString()!));
    }
}
