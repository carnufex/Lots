using System.Net;
using System.Net.Http.Json;
using Lots.Shell.Core.Runs;
using Lots.Shell.Core.Speech;
using Lots.Shell.Features.Voice;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

/// <summary>#37 (ADR 0025): the answer is spoken sentence by sentence while it is written, and only the run's own answer.</summary>
public class SpeakNextTests : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void Pieces_are_whole_sentences_the_first_as_soon_as_it_exists()
    {
        const string text = "Ja. Jag kollar klustret nu, det tar en liten stund att gå igenom alla noder. Och sen";
        Assert.Equal(3, SpeakNextEndpoint.NextEnd(text, 0, final: false)); // "Ja." alone: first audio as early as possible
        var second = SpeakNextEndpoint.NextEnd(text, 3, final: false)!.Value;
        Assert.EndsWith("noder.", text[..second]);
        Assert.Null(SpeakNextEndpoint.NextEnd(text, second, final: false)); // "Och sen" is not a sentence yet
        Assert.Equal(text.Length, SpeakNextEndpoint.NextEnd(text, second, final: true)); // the final answer: the rest
        Assert.Equal(second, SpeakNextEndpoint.NextEnd(text, 3, final: true));          // ...still in sentence-sized pieces
        Assert.Null(SpeakNextEndpoint.NextEnd(text, text.Length, final: true));
    }

    private sealed class FakeTts : ITextToSpeech
    {
        public List<string> Spoken { get; } = [];

        public Task<SpeechAudio> SynthesizeAsync(string text, string language, CancellationToken ct)
        {
            lock (Spoken) Spoken.Add(text);
            return Task.FromResult(new SpeechAudio(new MemoryStream([1, 2, 3]), "audio/wav"));
        }
    }

    private readonly FakeTts _tts = new();
    private readonly WebApplicationFactory<Program> _app;

    public SpeakNextTests(WebApplicationFactory<Program> factory)
    {
        var db = Guid.NewGuid().ToString();
        _app = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false", ["ConnectionStrings:Lots"] = "Host=none", ["Auth:Mode"] = "Dev", ["Auth:Dev:AllowHeaders"] = "true",
                ["Outcomes:Enabled"] = "false", ["Mining:Enabled"] = "false", ["Speech:BaseUrl"] = "http://voice.test:8700/v1",
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

    private async Task<HttpResponseMessage> Next(Guid run, int from, string? hash, string user = "claude-test-speaknext")
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/runs/{run}/speak/next") { Content = JsonContent.Create(new { from, hash }) };
        req.Headers.Add("X-Dev-User", user);
        req.Headers.Add("X-Dev-Roles", "operator");
        return await _app.CreateClient().SendAsync(req);
    }

    [Fact]
    public async Task The_streaming_answer_is_spoken_in_sentences_and_finished_from_the_final_answer()
    {
        var id = Guid.NewGuid();
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
            db.Runs.Add(new RunRecord { Id = id, Prompt = "q", Profile = TestProfiles.Name, UserId = "claude-test-speaknext", Roles = "operator", Status = RunStatus.Running,
                CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var streams = _app.Services.GetRequiredService<RunStreams>();
        streams.Publish(id, "Hej. Jag kollar klustret nu och");

        var first = await Next(id, 0, null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var to = int.Parse(first.Headers.GetValues("X-Spoken-To").Single());
        var hash = first.Headers.GetValues("X-Spoken-Hash").Single();
        Assert.Equal("Hej.", _tts.Spoken.Single());

        var nothingYet = await Next(id, to, hash);
        Assert.Equal(HttpStatusCode.NoContent, nothingYet.StatusCode);
        Assert.Equal("false", nothingYet.Headers.GetValues("X-Run-Done").Single());

        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
            var run = await db.Runs.SingleAsync(r => r.Id == id);
            run.Status = RunStatus.Completed;
            run.FinalAnswer = "Hej. Jag kollar klustret nu och allt ser bra ut.";
            await db.SaveChangesAsync();
        }
        var rest = await Next(id, to, hash);
        Assert.Equal(HttpStatusCode.OK, rest.StatusCode);
        Assert.Equal("true", rest.Headers.GetValues("X-Run-Done").Single());
        Assert.Equal("Jag kollar klustret nu och allt ser bra ut.", _tts.Spoken[^1]); // the last sentence of the final answer

        // A text that no longer continues what was spoken (a new model message) starts over, flagged.
        var reset = await Next(id, to, "0000000000000000");
        Assert.True(reset.Headers.Contains("X-Speak-Reset"));
        Assert.Equal(HttpStatusCode.NotFound, (await Next(id, 0, null, user: "claude-test-other")).StatusCode);
    }
}
