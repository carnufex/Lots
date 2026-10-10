using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lots.Shell.Core.Speech;
using Lots.Shell.Features.Settings;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

public class PersonaTests
{
    [Fact]
    public void Defaults_add_no_instructions_and_extremes_do()
    {
        Assert.Null(Persona.Instructions(null));
        Assert.Null(Persona.Instructions(new UserSettingsRecord { UserId = "a" }));

        var terse = Persona.Instructions(new UserSettingsRecord { UserId = "a", Talkativeness = 10, Warmth = 10, Formality = 90 });
        Assert.Contains("brief", terse);
        Assert.Contains("matter-of-fact", terse);
        Assert.Contains("formal", terse);

        var chatty = Persona.Instructions(new UserSettingsRecord { UserId = "a", Talkativeness = 90, Warmth = 90 });
        Assert.Contains("talkative", chatty);
        Assert.Contains("warm", chatty);
    }

    [Fact]
    public void Style_instructions_state_that_they_never_override_the_rules()
    {
        Assert.Contains("never override", Persona.Instructions(new UserSettingsRecord { UserId = "a", Warmth = 90 }));
    }
}

public class SettingsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class FakeRegistry : IVoiceRegistry
    {
        public Dictionary<string, int> Voices { get; } = [];
        public string? Reject { get; set; }
        public bool Down { get; set; }
        /// <summary>Simulates a voice service that reports success but keeps the clip.</summary>
        public bool KeepOnDelete { get; set; }

        public Task<bool?> ExistsAsync(string voiceId, CancellationToken ct) => Task.FromResult<bool?>(Voices.ContainsKey(voiceId));

        public Task<double> RegisterAsync(string voiceId, AudioInput clip, CancellationToken ct)
        {
            if (Down) throw new SpeechUnavailableException("down");
            if (Reject is not null) throw new VoiceRejectedException(Reject);
            using var ms = new MemoryStream();
            clip.Content.CopyTo(ms);
            Voices[voiceId] = (int)ms.Length;
            return Task.FromResult(12.5);
        }

        public Task DeleteAsync(string voiceId, CancellationToken ct)
        {
            if (Down) throw new SpeechUnavailableException("down");
            if (!KeepOnDelete) Voices.Remove(voiceId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTts : ITextToSpeech
    {
        public List<SpeechVoice?> Voices { get; } = [];

        public Task<SpeechAudio> SynthesizeAsync(string text, string language, CancellationToken ct) => SynthesizeAsync(text, language, null, ct);

        public Task<SpeechAudio> SynthesizeAsync(string text, string language, SpeechVoice? voice, CancellationToken ct)
        {
            Voices.Add(voice);
            return Task.FromResult(new SpeechAudio(new MemoryStream([1, 2, 3]), "audio/wav"));
        }
    }

    private readonly FakeRegistry _registry = new();
    private readonly FakeTts _tts = new();
    private readonly WebApplicationFactory<Program> _factory;

    public SettingsApiTests(WebApplicationFactory<Program> factory)
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
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<Microsoft.EntityFrameworkCore.DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(db));
                s.AddSingleton(TestProfiles.Registry());
                s.RemoveAll<ITextToSpeech>();
                s.RemoveAll<IVoiceRegistry>();
                s.AddSingleton<ITextToSpeech>(_tts);
                s.AddSingleton<IVoiceRegistry>(_registry);
            });
        });
    }

    private static HttpRequestMessage As(HttpMethod m, string url, string user, HttpContent? body = null, string roles = "operator")
    {
        var r = new HttpRequestMessage(m, url) { Content = body };
        r.Headers.Add("X-Dev-User", user);
        r.Headers.Add("X-Dev-Roles", roles);
        return r;
    }

    private async Task<List<VoiceConsentRecord>> ConsentsAsync(string user)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LotsDbContext>().VoiceConsents.Where(c => c.UserId == user).OrderBy(c => c.At).ToListAsync();
    }

    [Fact]
    public async Task Every_registration_and_withdrawal_is_kept_in_the_consent_trail()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(As(HttpMethod.Put, "/me/voice", "carol", Clip()))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(As(HttpMethod.Delete, "/me/voice", "carol"))).StatusCode);

        var trail = await ConsentsAsync("carol");
        Assert.Equal([VoiceConsentEvent.Given, VoiceConsentEvent.Withdrawn], trail.Select(c => c.Event));
        Assert.Equal(VoiceConsent.Statement, trail[0].Statement);
        Assert.Equal(12.5, trail[0].Seconds);
        Assert.All(trail, c => Assert.Equal("carol", c.Actor));
    }

    [Fact]
    public async Task Registrations_are_limited_per_day()
    {
        var client = _factory.CreateClient();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(As(HttpMethod.Put, "/me/voice", "dave", Clip()))).StatusCode);
        var sixth = await client.SendAsync(As(HttpMethod.Put, "/me/voice", "dave", Clip()));
        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(As(HttpMethod.Put, "/me/voice", "erin", Clip()))).StatusCode); // per user
    }

    [Fact]
    public async Task Admins_see_own_voices_and_can_revoke_them()
    {
        var client = _factory.CreateClient();
        await client.SendAsync(As(HttpMethod.Put, "/me/voice", "frank", Clip()));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(As(HttpMethod.Get, "/admin/voices", "frank"))).StatusCode);

        var list = await (await client.SendAsync(As(HttpMethod.Get, "/admin/voices", "claude-test-admin", roles: "admin"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(list.GetProperty("voices").EnumerateArray(), v => v.GetProperty("userId").GetString() == "frank");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(As(HttpMethod.Delete, "/admin/voices/frank", "frank"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(As(HttpMethod.Delete, "/admin/voices/frank", "claude-test-admin", roles: "admin"))).StatusCode);
        Assert.Empty(_registry.Voices);
        var settings = await (await client.SendAsync(As(HttpMethod.Get, "/me/settings", "frank"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("ownVoice").ValueKind);
        var revoked = (await ConsentsAsync("frank")).Last();
        Assert.Equal((VoiceConsentEvent.Revoked, "claude-test-admin"), (revoked.Event, revoked.Actor));
    }

    [Fact]
    public async Task A_recording_the_voice_service_keeps_after_deletion_stops_the_deletion()
    {
        var client = _factory.CreateClient();
        await client.SendAsync(As(HttpMethod.Put, "/me/voice", "gina", Clip()));
        _registry.KeepOnDelete = true;

        Assert.Equal(HttpStatusCode.BadGateway, (await client.SendAsync(As(HttpMethod.Delete, "/admin/voices/gina", "claude-test-admin", roles: "admin"))).StatusCode);
        var erase = await client.SendAsync(As(HttpMethod.Delete, "/me/data", "gina", JsonContent.Create(new { confirm = "delete my data" })));
        Assert.False(erase.IsSuccessStatusCode);
        var settings = await (await client.SendAsync(As(HttpMethod.Get, "/me/settings", "gina"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(JsonValueKind.Null, settings.GetProperty("ownVoice").ValueKind); // nothing was deleted half-way

        _registry.KeepOnDelete = false;
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(As(HttpMethod.Delete, "/me/data", "gina", JsonContent.Create(new { confirm = "delete my data" })))).StatusCode);
        Assert.Empty(_registry.Voices);
        Assert.Equal(VoiceConsentEvent.Erased, (await ConsentsAsync("gina")).Last().Event);
    }

    private static MultipartFormDataContent Clip(bool consent = true, int bytes = 100)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(new byte[bytes]), "Audio", "voice.webm" } };
        form.Add(new StringContent(consent ? "true" : "false"), "Consent");
        return form;
    }

    [Fact]
    public async Task Settings_default_to_neutral_and_are_saved_clamped_per_user()
    {
        var client = _factory.CreateClient();

        var first = await (await client.SendAsync(As(HttpMethod.Get, "/me/settings", "alice"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(50, first.GetProperty("talkativeness").GetInt32());
        Assert.True(first.GetProperty("voiceEnabled").GetBoolean());

        var put = await client.SendAsync(As(HttpMethod.Put, "/me/settings", "alice",
            JsonContent.Create(new { talkativeness = 150, warmth = -4, formality = 20, expressiveness = 80, pace = 10 })));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((100, 0, 20, 80, 10), (saved.GetProperty("talkativeness").GetInt32(), saved.GetProperty("warmth").GetInt32(),
            saved.GetProperty("formality").GetInt32(), saved.GetProperty("expressiveness").GetInt32(), saved.GetProperty("pace").GetInt32()));

        var bob = await (await client.SendAsync(As(HttpMethod.Get, "/me/settings", "bob"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(50, bob.GetProperty("talkativeness").GetInt32()); // other users are unaffected
    }

    [Fact]
    public async Task Own_voice_needs_consent_is_stored_per_user_and_can_be_deleted()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(As(HttpMethod.Put, "/me/voice", "alice", Clip(consent: false)))).StatusCode);
        Assert.Empty(_registry.Voices);

        var ok = await client.SendAsync(As(HttpMethod.Put, "/me/voice", "alice", Clip()));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var voice = (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ownVoice");
        Assert.Equal(12.5, voice.GetProperty("seconds").GetDouble());
        var id = Assert.Single(_registry.Voices.Keys);
        Assert.StartsWith("u-", id);

        // Re-recording replaces the clip under the same id; another user gets their own id.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(As(HttpMethod.Put, "/me/voice", "alice", Clip(bytes: 200)))).StatusCode);
        Assert.Equal(200, _registry.Voices[id]);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(As(HttpMethod.Put, "/me/voice", "bob", Clip()))).StatusCode);
        Assert.Equal(2, _registry.Voices.Count);

        var bobSettings = await (await client.SendAsync(As(HttpMethod.Get, "/me/settings", "bob"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(JsonValueKind.Null, bobSettings.GetProperty("ownVoice").ValueKind);

        var del = await client.SendAsync(As(HttpMethod.Delete, "/me/voice", "alice"));
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await del.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ownVoice").ValueKind);
        Assert.DoesNotContain(id, _registry.Voices.Keys);
        Assert.Single(_registry.Voices); // bob's voice is untouched
    }

    [Fact]
    public async Task Unusable_clips_and_an_unavailable_voice_service_are_reported_without_storing_anything()
    {
        var client = _factory.CreateClient();

        _registry.Reject = "The clip must be 3-40 seconds, got 1.0.";
        var rejected = await client.SendAsync(As(HttpMethod.Put, "/me/voice", "alice", Clip()));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("3-40 seconds", await rejected.Content.ReadAsStringAsync());

        _registry.Reject = null;
        _registry.Down = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.SendAsync(As(HttpMethod.Put, "/me/voice", "alice", Clip()))).StatusCode);

        var s = await (await client.SendAsync(As(HttpMethod.Get, "/me/settings", "alice"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, s.GetProperty("ownVoice").ValueKind);
    }

    [Fact]
    public async Task Answers_are_spoken_with_the_callers_own_voice_and_settings()
    {
        var client = _factory.CreateClient();
        await client.SendAsync(As(HttpMethod.Put, "/me/voice", "alice", Clip()));
        await client.SendAsync(As(HttpMethod.Put, "/me/settings", "alice", JsonContent.Create(new { talkativeness = 50, warmth = 50, formality = 50, expressiveness = 90, pace = 20 })));
        var id = _registry.Voices.Keys.Single();

        Guid run;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
            var r = new RunRecord
            {
                Id = Guid.NewGuid(), Prompt = "q", Profile = TestProfiles.Name, UserId = "alice", Roles = "operator",
                Status = RunStatus.Completed, FinalAnswer = "Det här är svaret.", CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Runs.Add(r);
            await db.SaveChangesAsync();
            run = r.Id;
        }

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(As(HttpMethod.Post, $"/runs/{run}/speak", "alice", JsonContent.Create(new { })))).StatusCode);
        var voice = Assert.Single(_tts.Voices)!;
        Assert.Equal((id, 0.9, 0.2), (voice.VoiceId, voice.Expressiveness, voice.Pace));
    }

    [Fact]
    public async Task Without_an_own_voice_the_deployment_voice_is_used()
    {
        var client = _factory.CreateClient();
        Guid run;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
            var r = new RunRecord
            {
                Id = Guid.NewGuid(), Prompt = "q", Profile = TestProfiles.Name, UserId = "carol", Roles = "operator",
                Status = RunStatus.Completed, FinalAnswer = "Svar.", CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Runs.Add(r);
            await db.SaveChangesAsync();
            run = r.Id;
        }

        await client.SendAsync(As(HttpMethod.Post, $"/runs/{run}/speak", "carol", JsonContent.Create(new { })));
        Assert.Null(Assert.Single(_tts.Voices)!.VoiceId);
    }
}
