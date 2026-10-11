using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lots.Shell.Core.Meetings;
using Lots.Shell.Core.Speech;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Lots.Shell.Tests.Features;

/// <summary>#41/#44: who said what. Text and speaker turns come from the voice service; the merge is ours.</summary>
public class MeetingMergeTests
{
    // Two speakers; the second Whisper segment spans a speaker change (A finishes, B answers without a pause).
    private static readonly MeetingAnalysis Sample = new("sv", 10,
        [
            new MeetingSegmentIn(0.0, 3.0, "Hej, hur går det?", [new(0.0, 0.5, " Hej,"), new(0.6, 1.2, " hur"), new(1.3, 1.8, " går"), new(1.9, 3.0, " det?")]),
            new MeetingSegmentIn(3.2, 8.0, "Bra tack. Själv då?", [new(3.2, 4.0, " Bra"), new(4.1, 5.0, " tack."), new(5.6, 6.5, " Själv"), new(6.6, 8.0, " då?")]),
        ],
        [new SpeakerTurn(0.0, 3.1, 0), new SpeakerTurn(3.15, 5.3, 1), new SpeakerTurn(5.4, 8.2, 0)]);

    private static readonly (double, double, string)[] Truth = [(0.0, 3.0, "A"), (3.2, 5.0, "B"), (5.6, 8.0, "A")];

    [Fact]
    public void Segments_get_the_speaker_with_the_most_overlap()
    {
        var simple = new MeetingAnalysis("sv", 6, [new MeetingSegmentIn(0, 2.5, "Hej.", []), new MeetingSegmentIn(3, 6, "Tja.", [])],
            [new SpeakerTurn(0, 2.8, 4), new SpeakerTurn(2.9, 6, 7)]);
        var lines = MeetingMerge.Merge(simple, useWords: false);
        Assert.Equal(["Speaker 1", "Speaker 2"], lines.Select(l => l.Speaker)); // named in order of appearance, not by cluster id
        // Without words the mixed segment goes to its majority speaker, and B's "Bra tack." is lost into A's line.
        Assert.Single(MeetingMerge.Merge(Sample, useWords: false));
    }

    [Fact]
    public void Word_timestamps_split_a_segment_at_the_speaker_change_and_lower_the_error()
    {
        var bySegment = MeetingMerge.Merge(Sample, useWords: false);
        var byWord = MeetingMerge.Merge(Sample, useWords: true);
        Assert.Equal(["Speaker 1", "Speaker 2", "Speaker 1"], byWord.Select(l => l.Speaker));
        Assert.Equal(["Hej, hur går det?", "Bra tack.", "Själv då?"], byWord.Select(l => l.Text));
        Assert.True(MeetingMerge.SpeakerError(byWord, Truth) < MeetingMerge.SpeakerError(bySegment, Truth));
        Assert.Equal(0, MeetingMerge.SpeakerError(byWord, Truth));
    }

    [Fact]
    public void Exports_carry_times_and_speakers()
    {
        var lines = new List<(double, double, string, string)> { (1.5, 3.25, "Anna", "Hej"), (3.5, 4, "Speaker 2", "Tja") };
        Assert.Contains("00:00:01,500 --> 00:00:03,250\nAnna: Hej", MeetingExport.Render("srt", "Möte", lines));
        Assert.Contains("00:00:01.500 --> 00:00:03.250\n<v Anna>Hej", MeetingExport.Render("vtt", "Möte", lines));
        Assert.StartsWith("WEBVTT", MeetingExport.Render("vtt", "Möte", lines));
        Assert.Contains("[00:00:03] Speaker 2: Tja", MeetingExport.Render("txt", "Möte", lines));
        Assert.Equal("Anna", JsonDocument.Parse(MeetingExport.Render("json", "Möte", lines)).RootElement.GetProperty("lines")[0].GetProperty("speaker").GetString());
    }
}

public class MeetingApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private sealed class FakeTranscriber : IMeetingTranscriber
    {
        public int Calls;
        public Exception? Fail;

        public Task<MeetingAnalysis> AnalyseAsync(Stream audio, string fileName, string contentType, string? language, int? speakers, IReadOnlyList<string>? vocabulary,
            bool words, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Fail is { } f) throw f;
            return Task.FromResult(new MeetingAnalysis("sv", 8, [new MeetingSegmentIn(0, 3, "Vi höjer minnet.", []), new MeetingSegmentIn(3.5, 6, "Jag tar det.", [])],
                [new SpeakerTurn(0, 3.2, 0), new SpeakerTurn(3.3, 6, 1)]));
        }
    }

    private readonly WebApplicationFactory<Program> _app;
    private readonly FakeTranscriber _transcriber = new();
    private readonly string _audio = Path.Combine(Path.GetTempPath(), "lots-meetings-" + Guid.NewGuid().ToString("N"));

    public MeetingApiTests(WebApplicationFactory<Program> factory)
    {
        var db = Guid.NewGuid().ToString();
        _app = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false", ["ConnectionStrings:Lots"] = "Host=none", ["Auth:Mode"] = "Dev", ["Auth:Dev:AllowHeaders"] = "true",
                ["Outcomes:Enabled"] = "false", ["Mining:Enabled"] = "false", ["Meetings:AudioPath"] = _audio,
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(db));
                s.AddSingleton(TestProfiles.Registry());
                s.RemoveAll<IMeetingTranscriber>();
                s.AddSingleton<IMeetingTranscriber>(_transcriber);
                // The test drives the worker itself: no background polling racing it.
                var hosted = s.Where(d => d.ImplementationType == typeof(MeetingWorker)).ToList();
                foreach (var d in hosted) s.Remove(d);
                s.AddSingleton<MeetingWorker>();
            });
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_audio)) Directory.Delete(_audio, true);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod m, string url, string user, HttpContent? body = null, string roles = "operator")
    {
        using var req = new HttpRequestMessage(m, url) { Content = body };
        req.Headers.Add("X-Dev-User", user);
        req.Headers.Add("X-Dev-Roles", roles);
        return await _app.CreateClient().SendAsync(req);
    }

    private async Task<Guid> Upload(string user, int? speakers = 2)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(new byte[2048]), "File", "standup.wav" }, { new StringContent("Driftmöte \"v42\""), "Title" } };
        if (speakers is { } n) form.Add(new StringContent(n.ToString()), "Speakers");
        var res = await Send(HttpMethod.Post, "/meetings", user, form);
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private Task<bool> Work(string owner = "worker-a") => _app.Services.GetRequiredService<MeetingWorker>().ProcessNextAsync(owner, default);

    [Fact]
    public async Task An_upload_becomes_a_transcript_with_speakers_for_its_owner_only()
    {
        const string ann = "claude-test-meetings-ann";
        var id = await Upload(ann);
        Assert.True(await Work());
        var detail = await (await Send(HttpMethod.Get, $"/meetings/{id}", ann)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("done", detail.GetProperty("meeting").GetProperty("status").GetString());
        Assert.Equal(["Speaker 1", "Speaker 2"], detail.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("speaker").GetString()));

        // Names, export, other users and admins.
        JsonContent Named() => JsonContent.Create(new { names = new Dictionary<string, string> { ["Speaker 1"] = "Anna" } });
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"/meetings/{id}/speakers", ann, Named())).StatusCode);
        var export = await Send(HttpMethod.Get, $"/meetings/{id}/export?format=srt", ann);
        Assert.Matches(@"^Driftmöte .*v42.*\.srt$",export.Content.Headers.ContentDisposition!.FileNameStar!); // non-ASCII titles survive the header
        var srt = await export.Content.ReadAsStringAsync();
        Assert.Contains("Anna: Vi höjer minnet.", srt);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"/meetings/{id}", "claude-test-meetings-bob")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Put, $"/meetings/{id}/speakers", "claude-test-meetings-bob", Named())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, $"/meetings/{id}", "claude-test-meetings-root", roles: "admin")).StatusCode);
        using (var scope = _app.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<LotsDbContext>().AuditLog.AnyAsync(a => a.Tool == "meeting.read" && a.UserId == "claude-test-meetings-root"));

        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, $"/meetings/{id}", ann)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"/meetings/{id}", ann)).StatusCode);
    }

    [Fact]
    public async Task A_meeting_survives_a_restart_mid_processing()
    {
        var id = await Upload("claude-test-meetings-cy");
        using (var scope = _app.Services.CreateScope())
        {
            // A replica claimed it, started, and died: status processing, lease held by a worker that never comes back.
            var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
            var m = await db.Meetings.SingleAsync(x => x.Id == id);
            m.Status = MeetingStatus.Processing;
            m.LeaseOwner = "dead-replica";
            m.LeaseUntilMs = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds();
            await db.SaveChangesAsync();
        }
        Assert.False(await Work("worker-b")); // not while the lease is valid
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
            (await db.Meetings.SingleAsync(x => x.Id == id)).LeaseUntilMs = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds();
            await db.SaveChangesAsync();
        }
        Assert.True(await Work("worker-b")); // the lease expired: another worker finishes it
        var detail = await (await Send(HttpMethod.Get, $"/meetings/{id}", "claude-test-meetings-cy")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("done", detail.GetProperty("meeting").GetProperty("status").GetString());
        Assert.Equal(2, detail.GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public async Task Failures_retry_when_the_voice_service_is_away_and_cancel_stops_a_queued_meeting()
    {
        var id = await Upload("claude-test-meetings-dee");
        _transcriber.Fail = new SpeechUnavailableException("down");
        Assert.True(await Work());
        using (var scope = _app.Services.CreateScope())
            Assert.Equal(MeetingStatus.Queued, (await scope.ServiceProvider.GetRequiredService<LotsDbContext>().Meetings.SingleAsync(x => x.Id == id)).Status);

        _transcriber.Fail = new InvalidOperationException("Could not process this recording: InvalidDataError");
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
            (await db.Meetings.SingleAsync(x => x.Id == id)).LeaseUntilMs = null; // skip the back-off
            await db.SaveChangesAsync();
        }
        Assert.True(await Work());
        var failed = await (await Send(HttpMethod.Get, $"/meetings/{id}", "claude-test-meetings-dee")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("failed", failed.GetProperty("meeting").GetProperty("status").GetString());
        Assert.Contains("InvalidDataError", failed.GetProperty("meeting").GetProperty("error").GetString());

        _transcriber.Fail = null;
        var other = await Upload("claude-test-meetings-dee");
        var cancelled = await (await Send(HttpMethod.Post, $"/meetings/{other}/cancel", "claude-test-meetings-dee", JsonContent.Create(new { }))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cancelled", cancelled.GetProperty("status").GetString());
        Assert.False(await Work());
    }
}
