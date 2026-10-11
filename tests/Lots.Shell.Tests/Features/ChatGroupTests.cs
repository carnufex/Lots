using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Runs;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Tests.Features;

/// <summary>#153: chat groups organise one user's chats, and chats in a group share context unless isolated.</summary>
public class ChatGroupContextTests
{
    private sealed class Capture : IModelClient
    {
        public string System { get; private set; } = "";

        public Task<ModelResponse> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ToolDefinition> t, CancellationToken ct)
        {
            System = m[0].Content ?? "";
            return Task.FromResult(new ModelResponse(new ChatMessage("assistant", "ok"), "stop", new ModelUsage(1, 1), TimeSpan.FromMilliseconds(1)));
        }
    }

    private static LotsDbContext Db(string name) => new(new DbContextOptionsBuilder<LotsDbContext>().UseInMemoryDatabase(name).Options);

    private static async Task<(LotsDbContext Db, Guid ChatA, Guid ChatB, ConversationGroupRecord Group)> Seed(string name, string user = "claude-test-groups")
    {
        var db = Db(name);
        var group = new ConversationGroupRecord { Id = Guid.NewGuid(), UserId = user, Name = "Incident 42", Instructions = "Answer in bullet points." };
        db.ConversationGroups.Add(group);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        db.Runs.Add(new RunRecord
        {
            Id = Guid.NewGuid(), ConversationId = a, Prompt = "The broken switch is in rack 7, remember that.", FinalAnswer = "Noted: the broken switch sits in rack 7.",
            Status = RunStatus.Completed, Profile = TestProfiles.Name, UserId = user, Roles = "operator", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        });
        db.Conversations.Add(new ConversationRecord { Id = a, UserId = user, Title = "Switch hunt", GroupId = group.Id });
        db.Conversations.Add(new ConversationRecord { Id = b, UserId = user, GroupId = group.Id });
        await db.SaveChangesAsync();
        return (db, a, b, group);
    }

    private static async Task<(string System, RunRecord Run)> Ask(LotsDbContext db, Guid chat, string user = "claude-test-groups")
    {
        var model = new Capture();
        var registry = TestProfiles.Registry();
        var run = new RunRecord
        {
            Id = Guid.NewGuid(), ConversationId = chat, Prompt = "Which rack was the broken switch in?", Profile = TestProfiles.Name, UserId = user,
            Roles = "operator", CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        await new AgentRunner(db, model, new ToolInvoker([], registry), registry, Options.Create(new AgentOptions()), TimeProvider.System).ExecuteAsync(run.Id, default);
        return (model.System, await db.Runs.SingleAsync(r => r.Id == run.Id));
    }

    [Fact]
    public async Task A_fact_from_one_chat_is_used_in_its_sibling_with_its_title_to_cite()
    {
        var (db, a, b, _) = await Seed(nameof(A_fact_from_one_chat_is_used_in_its_sibling_with_its_title_to_cite));
        var (system, run) = await Ask(db, b);
        Assert.Contains("rack 7", system);
        Assert.Contains("From: Switch hunt", system);
        Assert.Contains("Answer in bullet points.", system); // the group's instructions
        Assert.Equal([a], JsonSerializer.Deserialize<List<Guid>>(run.GroupContextJson!)); // traced: which siblings were read
    }

    [Fact]
    public async Task An_isolated_chat_neither_reads_nor_is_read()
    {
        var (db, a, b, _) = await Seed(nameof(An_isolated_chat_neither_reads_nor_is_read));
        (await db.Conversations.SingleAsync(c => c.Id == b)).Isolated = true;
        await db.SaveChangesAsync();
        Assert.DoesNotContain("rack 7", (await Ask(db, b)).System);

        (await db.Conversations.SingleAsync(c => c.Id == b)).Isolated = false;
        (await db.Conversations.SingleAsync(c => c.Id == a)).Isolated = true;
        await db.SaveChangesAsync();
        Assert.DoesNotContain("rack 7", (await Ask(db, b)).System);
    }

    [Fact]
    public async Task Turning_sharing_off_for_the_group_keeps_chats_apart()
    {
        var (db, _, b, group) = await Seed(nameof(Turning_sharing_off_for_the_group_keeps_chats_apart));
        (await db.ConversationGroups.SingleAsync(g => g.Id == group.Id)).ShareContext = false;
        await db.SaveChangesAsync();
        var (system, run) = await Ask(db, b);
        Assert.DoesNotContain("rack 7", system);
        Assert.Contains("Answer in bullet points.", system); // instructions still apply
        Assert.Null(run.GroupContextJson);
    }

    [Fact]
    public async Task Another_users_chats_are_never_shared_even_in_a_group_with_the_same_id()
    {
        var (db, _, _, group) = await Seed(nameof(Another_users_chats_are_never_shared_even_in_a_group_with_the_same_id));
        var mallory = Guid.NewGuid();
        db.Conversations.Add(new ConversationRecord { Id = mallory, UserId = "claude-test-mallory", GroupId = group.Id });
        await db.SaveChangesAsync();
        Assert.DoesNotContain("rack 7", (await Ask(db, mallory, "claude-test-mallory")).System);
    }
}

public class ChatGroupApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _app;

    public ChatGroupApiTests(WebApplicationFactory<Program> factory)
    {
        var db = Guid.NewGuid().ToString();
        _app = factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false", ["ConnectionStrings:Lots"] = "Host=none", ["Auth:Mode"] = "Dev", ["Auth:Dev:AllowHeaders"] = "true",
                ["Outcomes:Enabled"] = "false", ["Mining:Enabled"] = "false",
            }));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<DbContextOptions<LotsDbContext>>();
                s.RemoveAll(typeof(Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<LotsDbContext>));
                s.AddDbContext<LotsDbContext>(o => o.UseInMemoryDatabase(db));
                s.AddSingleton(new ProfileRegistry([ContextRouterTests.Homelab, ContextRouterTests.Cmdb]));
            });
        });
    }

    private async Task<HttpResponseMessage> Send(HttpMethod m, string url, string user, object? body = null)
    {
        using var req = new HttpRequestMessage(m, url) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Add("X-Dev-User", user);
        req.Headers.Add("X-Dev-Roles", "operator");
        return await _app.CreateClient().SendAsync(req);
    }

    private async Task<Guid> Chat(string user, string prompt)
    {
        var conv = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await Send(HttpMethod.Post, "/runs", user, new { prompt, profile = "homelab", conversationId = conv })).StatusCode);
        return conv;
    }

    [Fact]
    public async Task Groups_are_private_and_chats_move_in_and_out()
    {
        const string ann = "claude-test-groups-ann";
        var created = await Send(HttpMethod.Post, "/groups", ann, new { name = "Incident 42", color = "#f80" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var group = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var chat = await Chat(ann, "which containers are unhealthy?");
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, $"/conversations/{chat}/organise", ann, new { groupId = group })).StatusCode);
        var inGroup = await (await Send(HttpMethod.Get, $"/conversations?group={group}", ann)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, inGroup.GetProperty("count").GetInt32());
        Assert.Equal(1, (await (await Send(HttpMethod.Get, "/groups", ann)).Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("chats").GetInt32());

        // Someone else sees nothing, cannot rename it, and cannot move Ann's chat.
        const string bob = "claude-test-groups-bob";
        Assert.Equal(0, (await (await Send(HttpMethod.Get, "/groups", bob)).Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Put, $"/groups/{group}", bob, new { name = "mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Put, $"/conversations/{chat}/organise", bob, new { archived = true })).StatusCode);

        // Archived chats leave the default list; deleting the group keeps the chat.
        await Send(HttpMethod.Put, $"/conversations/{chat}/organise", ann, new { archived = true });
        Assert.Equal(0, (await (await Send(HttpMethod.Get, $"/conversations?group={group}", ann)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("count").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, $"/groups/{group}", ann)).StatusCode);
        var all = await (await Send(HttpMethod.Get, "/conversations?archived=true", ann)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, all.GetProperty("conversations")[0].GetProperty("groupId").ValueKind);
    }

    [Fact]
    public async Task A_new_chat_in_a_group_starts_in_the_groups_default_context()
    {
        const string cy = "claude-test-groups-cy";
        var group = (await (await Send(HttpMethod.Post, "/groups", cy, new { name = "Network", defaultContext = "cmdb" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/groups", cy, new { name = "x", defaultContext = "nope" })).StatusCode);

        // A new chat started in the group, with a question that fits nowhere: the group's context applies instead of asking.
        var chat = Guid.NewGuid();
        var started = await Send(HttpMethod.Post, "/runs", cy, new { prompt = "and what about yesterday?", conversationId = chat, groupId = group });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var next = await started.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("sticky", "cmdb"), (next.GetProperty("routing").GetString(), next.GetProperty("profile").GetString()));
        Assert.Equal(1, (await (await Send(HttpMethod.Get, $"/conversations?group={group}", cy)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("count").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, "/runs", "claude-test-groups-dee", new { prompt = "x", conversationId = Guid.NewGuid(), groupId = group })).StatusCode);
    }
}
