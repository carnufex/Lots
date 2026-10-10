using System.Net;
using System.Net.Http.Json;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Speech;
using Lots.Shell.Features.Conversations;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

public class ConversationAudioTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private sealed class Summariser : IModelClient
    {
        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, CancellationToken ct) =>
            Task.FromResult(new ModelResponse(new ChatMessage("assistant", """Here: {"title": "Container health check", "summary": "The user asked which containers run. Twelve do."}"""),
                "stop", new ModelUsage(1, 1), TimeSpan.Zero));
    }

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("lots-audio-");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly Guid _conversation = Guid.NewGuid();

    public ConversationAudioTests(WebApplicationFactory<Program> factory)
    {
        var dbName = Guid.NewGuid().ToString();
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false", ["Auth:Mode"] = "Dev", ["ConnectionStrings:Lots"] = "Host=none", ["Auth:Dev:AllowHeaders"] = "true",
                ["Agent:RunWorkerEnabled"] = "false", ["Speech:AudioPath"] = _dir.FullName,
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(dbName));
                s.AddSingleton(TestProfiles.Registry());
                s.RemoveAll<IModelClient>();
                s.AddSingleton<IModelClient, Summariser>();
            });
        });
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        db.Runs.Add(new RunRecord
        {
            Id = Guid.NewGuid(), Prompt = "vilka containrar kör?", FinalAnswer = "Tolv.", Profile = "test", UserId = "alice", Roles = "operator",
            Status = RunStatus.Completed, ConversationId = _conversation, Voice = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    public void Dispose() => _dir.Delete(true);

    private HttpRequestMessage As(HttpMethod m, string url, string user, string roles = "operator")
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("X-Dev-User", user);
        r.Headers.Add("X-Dev-Roles", roles);
        return r;
    }

    [Fact]
    public async Task Audio_is_stored_encrypted_played_to_the_owner_and_auditors_audited_and_deletable()
    {
        var store = _factory.Services.GetRequiredService<IAudioStore>();
        Assert.True(store.Enabled);
        var sound = "RIFF fake wav bytes"u8.ToArray();
        await store.SaveAsync(_conversation, null, "alice", "user", "audio/wav", sound, DateTimeOffset.UtcNow, default);

        var file = Directory.EnumerateFiles(_dir.FullName, "*", SearchOption.AllDirectories).Single();
        Assert.False(File.ReadAllBytes(file).AsSpan().IndexOf("fake wav"u8) >= 0); // encrypted at rest

        var client = _factory.CreateClient();
        var clips = (await (await client.SendAsync(As(HttpMethod.Get, $"/conversations/{_conversation}/audio", "alice"))).Content.ReadFromJsonAsync<List<AudioClipDto>>())!;
        var clip = Assert.Single(clips);
        Assert.Equal(sound, await (await client.SendAsync(As(HttpMethod.Get, $"/conversations/audio/{clip.Id}", "alice"))).Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(As(HttpMethod.Get, $"/conversations/audio/{clip.Id}", "mallory"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(As(HttpMethod.Get, $"/conversations/audio/{clip.Id}", "eve", "auditor"))).StatusCode);
        using (var scope = _factory.Services.CreateScope())
            Assert.Contains(scope.ServiceProvider.GetRequiredService<LotsDbContext>().AuditLog, a => a.Tool == "conversation_audio" && a.ApproverId == "eve");

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(As(HttpMethod.Delete, $"/conversations/{_conversation}/audio", "eve", "auditor"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(As(HttpMethod.Delete, $"/conversations/{_conversation}/audio", "alice"))).StatusCode);
        Assert.Empty(Directory.EnumerateFiles(_dir.FullName, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Expired_audio_is_purged()
    {
        var store = _factory.Services.GetRequiredService<IAudioStore>();
        await store.SaveAsync(_conversation, null, "alice", "user", "audio/wav", [1, 2, 3], DateTimeOffset.UtcNow.AddDays(-31), default);
        await store.SaveAsync(_conversation, null, "alice", "agent", "audio/wav", [4, 5, 6], DateTimeOffset.UtcNow, default);

        Assert.Equal(1, await store.PurgeAsync(DateTimeOffset.UtcNow.AddDays(-30), default));
        Assert.Single(Directory.EnumerateFiles(_dir.FullName, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task The_retention_job_keeps_audio_for_the_configured_days_only()
    {
        var store = _factory.Services.GetRequiredService<IAudioStore>();
        await store.SaveAsync(_conversation, null, "alice", "user", "audio/wav", [1], DateTimeOffset.UtcNow.AddDays(-29), default);
        await store.SaveAsync(_conversation, null, "alice", "user", "audio/wav", [2], DateTimeOffset.UtcNow.AddDays(-31), default);
        var worker = new AudioRetentionWorker(store, Microsoft.Extensions.Options.Options.Create(new SpeechOptions { AudioRetentionDays = 30 }),
            TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<AudioRetentionWorker>.Instance);

        Assert.Equal(1, await worker.PurgeOnceAsync(default));
        Assert.Equal(0, await worker.PurgeOnceAsync(default)); // the 29-day clip stays until it is due
        Assert.Single(Directory.EnumerateFiles(_dir.FullName, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_summary_becomes_the_title_and_the_owner_can_delete_the_whole_conversation()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(As(HttpMethod.Post, $"/conversations/{_conversation}/summarize", "alice"))).StatusCode);
        var detail = (await (await client.SendAsync(As(HttpMethod.Get, $"/conversations/{_conversation}", "alice"))).Content.ReadFromJsonAsync<ConversationDetail>())!;
        Assert.Equal(("Container health check", "The user asked which containers run. Twelve do."), (detail.Conversation.Title, detail.Conversation.Summary));

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(As(HttpMethod.Delete, $"/conversations/{_conversation}", "mallory"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(As(HttpMethod.Delete, $"/conversations/{_conversation}", "alice"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(As(HttpMethod.Get, $"/conversations/{_conversation}", "alice"))).StatusCode);
    }
}
