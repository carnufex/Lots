using System.Net;
using System.Net.Http.Json;
using Lots.Shell.Core.Config;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Features.Admin;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lots.Shell.Tests.Features;

public class DiffTests
{
    [Fact]
    public void Unified_diff_marks_added_and_removed_lines()
    {
        Assert.Equal("  a\n- b\n+ B\n  c\n+ d\n", Diff.Unified("a\nb\nc\n", "a\nB\nc\nd\n"));
        Assert.Equal("+ x\n", Diff.Unified("", "x\n"));
    }

    [Fact]
    public void Multi_document_yaml_is_split_into_kinds_and_names()
    {
        var errors = new List<string>();
        var docs = ConfigService.Split("# comment\nname: a\nversion: 1\n---\nkind: KnowledgeSource\nid: runbooks\n---\nkind: Widget\nname: w\n", errors);

        Assert.Equal([("Profile", "a"), ("KnowledgeSource", "runbooks")], docs.Select(d => (d.Kind, d.Name)));
        Assert.Contains("unknown kind 'Widget'", Assert.Single(errors));
    }
}

public class AdminApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class Echo : IModelClient
    {
        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, CancellationToken ct) =>
            Task.FromResult(new ModelResponse(new ChatMessage("assistant", "ok"), "stop", new ModelUsage(1, 1), TimeSpan.Zero));
    }

    private readonly WebApplicationFactory<Program> _factory;

    public AdminApiTests(WebApplicationFactory<Program> factory)
    {
        var dbName = Guid.NewGuid().ToString();
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false",
                ["Auth:Mode"] = "Dev",
                ["ConnectionStrings:Lots"] = "Host=none",
                ["Auth:Dev:AllowHeaders"] = "true",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(dbName));
                s.AddSingleton(TestProfiles.Registry()); // "test" is a file-managed profile
                s.RemoveAll<IModelClient>();
                s.AddSingleton<IModelClient, Echo>();
            });
        });
    }

    private const string Ops = """
        kind: Profile
        name: ops
        version: 1
        description: applied through the API
        instructions: Be brief.
        tools:
          - name: list_containers
            risk: read
        roles:
          - name: operator
            allow: [read]
        """ + "\n";

    private async Task<(HttpStatusCode Status, ApplyOutcome Outcome)> Apply(string yaml, bool dryRun = false, string managedBy = "api", bool prune = false, string roles = "admin")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/admin/v1/apply") { Content = JsonContent.Create(new { yaml, dryRun, managedBy, prune }) };
        req.Headers.Add("X-Dev-User", "root");
        req.Headers.Add("X-Dev-Roles", roles);
        var res = await _factory.CreateClient().SendAsync(req);
        return (res.StatusCode, res.StatusCode is HttpStatusCode.OK or HttpStatusCode.UnprocessableEntity ? (await res.Content.ReadFromJsonAsync<ApplyOutcome>())! : null!);
    }

    [Fact]
    public async Task Only_admins_apply()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Apply(Ops, roles: "operator")).Status);
    }

    [Fact]
    public async Task Dry_run_shows_the_diff_and_apply_makes_the_profile_live_with_history()
    {
        var dry = await Apply(Ops, dryRun: true);
        Assert.Equal(HttpStatusCode.OK, dry.Status);
        Assert.False(dry.Outcome.Applied);
        Assert.Equal("create", dry.Outcome.Results.Single().Action);
        Assert.Contains("+ name: ops", dry.Outcome.Results.Single().Diff);
        var registry = _factory.Services.GetRequiredService<ProfileRegistry>();
        Assert.Null(registry.Find("ops"));

        var applied = await Apply(Ops);
        Assert.True(applied.Outcome.Applied);
        Assert.Equal("api", registry.ManagedBy("ops"));
        Assert.Equal("Be brief.", registry.Find("ops")!.Instructions);

        // Unchanged apply is a no-op; a change needs a higher version.
        Assert.Equal("unchanged", (await Apply(Ops)).Outcome.Results.Single().Action);
        var sameVersion = await Apply(Ops.Replace("Be brief.", "Be verbose."));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, sameVersion.Status);
        Assert.Contains("not higher than the stored 1", sameVersion.Outcome.Results.Single().Errors.Single());
        var v2 = await Apply(Ops.Replace("Be brief.", "Be verbose.").Replace("version: 1", "version: 2"));
        Assert.Equal(("update", 2), (v2.Outcome.Results.Single().Action, v2.Outcome.Results.Single().Version));
        Assert.Equal(2, registry.Find("ops")!.Version);

        // A run can use the applied profile right away.
        var client = _factory.CreateClient();
        var start = new HttpRequestMessage(HttpMethod.Post, "/runs") { Content = JsonContent.Create(new { prompt = "hi", profile = "ops" }) };
        start.Headers.Add("X-Dev-User", "alice");
        start.Headers.Add("X-Dev-Roles", "operator");
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(start)).StatusCode);

        var versions = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/resources/Profile/ops/versions");
        versions.Headers.Add("X-Dev-User", "root");
        versions.Headers.Add("X-Dev-Roles", "admin");
        var history = (await (await client.SendAsync(versions)).Content.ReadFromJsonAsync<List<VersionDto>>())!;
        Assert.Equal([2, 1], history.Select(h => h.Version));
        Assert.Contains("- instructions: Be brief.", history[0].Diff);
        Assert.Contains("+ instructions: Be verbose.", history[0].Diff);
    }

    [Fact]
    public async Task Invalid_resources_block_the_whole_apply_and_file_profiles_cannot_be_overridden()
    {
        var bad = await Apply(Ops + "---\nkind: Profile\nname: test\nversion: 9\n---\nkind: Profile\nname: broken\nversion: 0\n");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.Status);
        Assert.False(bad.Outcome.Applied);
        Assert.Contains(bad.Outcome.Results.Single(r => r.Name == "test").Errors, e => e.Contains("loaded from a file"));
        Assert.NotEmpty(bad.Outcome.Results.Single(r => r.Name == "broken").Errors);
        Assert.Null(_factory.Services.GetRequiredService<ProfileRegistry>().Find("ops")); // nothing was written
    }

    [Fact]
    public async Task Git_managed_resources_are_read_only_for_the_api_and_prune_removes_what_left_git()
    {
        var other = Ops.Replace("name: ops", "name: other");
        Assert.True((await Apply(Ops + "---\n" + other, managedBy: "gitops")).Outcome.Applied);

        var fromUi = await Apply(Ops.Replace("version: 1", "version: 5").Replace("Be brief.", "x"));
        Assert.Contains("managed from Git", fromUi.Outcome.Results.Single().Errors.Single());

        var pruned = await Apply(Ops, managedBy: "gitops", prune: true);
        Assert.Contains(pruned.Outcome.Results, r => r.Name == "other" && r.Action == "delete");
        var registry = _factory.Services.GetRequiredService<ProfileRegistry>();
        Assert.Null(registry.Find("other"));
        Assert.Equal("gitops", registry.ManagedBy("ops"));
    }
}

public class GitOpsSyncTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("lots-gitops-");
    private readonly WebApplicationFactory<Program> _factory;

    public GitOpsSyncTests(WebApplicationFactory<Program> factory)
    {
        var dbName = Guid.NewGuid().ToString();
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false",
                ["Auth:Mode"] = "Dev",
                ["ConnectionStrings:Lots"] = "Host=none",
                ["Auth:Dev:AllowHeaders"] = "true",
                ["GitOps:Path"] = _dir.FullName,
                ["GitOps:IntervalSeconds"] = "3600", // the test drives the syncs
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(dbName));
                s.AddSingleton(TestProfiles.Registry());
            });
        });
    }

    private static string Profile(string name, int version) =>
        $"kind: Profile\nname: {name}\nversion: {version}\ntools: []\nroles:\n  - name: operator\n    allow: [read]\n";

    [Fact]
    public async Task The_git_directory_is_applied_kept_in_sync_and_pruned_and_drift_is_visible()
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "a.yaml"), Profile("from-git", 1));
        File.WriteAllText(Path.Combine(_dir.FullName, "b.yml"), Profile("also-git", 1));
        Directory.CreateDirectory(Path.Combine(_dir.FullName, ".github"));
        File.WriteAllText(Path.Combine(_dir.FullName, ".github", "ci.yaml"), "not: a resource");
        var worker = _factory.Services.GetRequiredService<GitOpsSyncWorker>();
        var registry = _factory.Services.GetRequiredService<ProfileRegistry>();
        var status = _factory.Services.GetRequiredService<GitOpsStatus>();

        await worker.SyncOnceAsync(default);
        Assert.Equal("applied", status.Current.LastResult);
        Assert.Equal("gitops", registry.ManagedBy("from-git"));

        await worker.SyncOnceAsync(default);
        Assert.Equal("in sync", status.Current.LastResult);

        File.Delete(Path.Combine(_dir.FullName, "b.yml"));
        File.WriteAllText(Path.Combine(_dir.FullName, "a.yaml"), Profile("from-git", 2));
        var req = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/gitops");
        req.Headers.Add("X-Dev-User", "root");
        req.Headers.Add("X-Dev-Roles", "admin");
        var drift = (await (await _factory.CreateClient().SendAsync(req)).Content.ReadFromJsonAsync<GitOpsDto>())!.Drift;
        Assert.Equal([("from-git", "update"), ("also-git", "delete")], drift.Select(d => (d.Name, d.Action)));

        await worker.SyncOnceAsync(default);
        Assert.Null(registry.Find("also-git"));
        Assert.Equal(2, registry.Find("from-git")!.Version);
    }
}
