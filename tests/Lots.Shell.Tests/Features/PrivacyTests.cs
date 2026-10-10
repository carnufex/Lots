using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using Lots.Shell.Core.Speech;
using Lots.Shell.Features.Privacy;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

public class PrivacyTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class Voices : IVoiceRegistry
    {
        public List<string> Deleted { get; } = [];
        public Task<double> RegisterAsync(string voiceId, AudioInput clip, CancellationToken ct) => Task.FromResult(10.0);
        public Task DeleteAsync(string voiceId, CancellationToken ct) { Deleted.Add(voiceId); return Task.CompletedTask; }
    }

    private readonly Voices _voices = new();
    private readonly WebApplicationFactory<Program> _factory;

    public PrivacyTests(WebApplicationFactory<Program> factory)
    {
        var dbName = Guid.NewGuid().ToString();
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false", ["Auth:Mode"] = "Dev", ["ConnectionStrings:Lots"] = "Host=none", ["Auth:Dev:AllowHeaders"] = "true",
                ["Agent:RunWorkerEnabled"] = "false", ["Retention:RunsDays"] = "90", ["Retention:AuditDays"] = "0",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(dbName));
                s.AddSingleton(TestProfiles.Registry());
                s.RemoveAll<IVoiceRegistry>();
                s.AddSingleton<IVoiceRegistry>(_voices);
            });
        });
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var now = DateTimeOffset.UtcNow;
        RunRecord R(string user, string prompt, int daysAgo, RunStatus status = RunStatus.Completed) => new()
        {
            Id = Guid.NewGuid(), Prompt = prompt, FinalAnswer = "a", Profile = "test", UserId = user, Status = status, CreatedAt = now.AddDays(-daysAgo), UpdatedAt = now.AddDays(-daysAgo),
        };
        db.Runs.AddRange(R("alice", "alice question", 1), R("bob", "bob question", 1), R("bob", "old finished", 200), R("bob", "old but waiting", 200, RunStatus.WaitingForApproval));
        db.UserSettings.Add(new UserSettingsRecord { UserId = "alice", VoiceId = "alice-voice", Warmth = 80 });
        db.UserVocabulary.Add(new UserVocabularyRecord { UserId = "alice", WordsJson = "[\"Talos\"]" });
        db.AuditLog.Add(new AuditRecord { Id = Guid.NewGuid(), At = now.AddDays(-400), UserId = "alice", Tool = "t", Decision = AuditDecision.Allowed });
        db.SaveChanges();
    }

    private HttpRequestMessage As(HttpMethod m, string url, string user, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("X-Dev-User", user);
        r.Headers.Add("X-Dev-Roles", "operator");
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    [Fact]
    public async Task Export_contains_only_the_callers_data()
    {
        var res = await _factory.CreateClient().SendAsync(As(HttpMethod.Get, "/me/export", "alice"));
        using var zip = new ZipArchive(await res.Content.ReadAsStreamAsync());
        var json = await new StreamReader(zip.GetEntry("data.json")!.Open()).ReadToEndAsync();

        Assert.Contains("alice question", json);
        Assert.Contains("Talos", json);
        Assert.DoesNotContain("bob question", json);
    }

    [Fact]
    public async Task Deleting_my_data_removes_it_and_my_own_voice_but_nobody_elses_and_keeps_the_audit_log()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(As(HttpMethod.Delete, "/me/data", "alice", new { confirm = "yes" }))).StatusCode);

        var report = (await (await client.SendAsync(As(HttpMethod.Delete, "/me/data", "alice", new { confirm = "delete my data" }))).Content.ReadFromJsonAsync<DeletionReport>())!;

        Assert.Equal(1, report.Deleted["runs"]);
        Assert.Equal(["alice-voice"], _voices.Deleted);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        Assert.Empty(db.Runs.Where(r => r.UserId == "alice"));
        Assert.Empty(db.UserSettings.Where(s => s.UserId == "alice"));
        Assert.Empty(db.UserVocabulary.Where(s => s.UserId == "alice"));
        Assert.Equal(3, db.Runs.Count(r => r.UserId == "bob"));
        Assert.Single(db.AuditLog.Where(a => a.UserId == "alice"));
    }

    [Fact]
    public async Task Retention_removes_old_finished_runs_but_never_unfinished_ones()
    {
        var purged = await _factory.Services.GetRequiredService<RetentionWorker>().PurgeAsync(default);

        Assert.Equal(1, purged["runs"]);
        Assert.False(purged.ContainsKey("audit")); // AuditDays 0 = kept
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        Assert.Contains(db.Runs, r => r.Prompt == "old but waiting");
        Assert.DoesNotContain(db.Runs, r => r.Prompt == "old finished");
    }
}
