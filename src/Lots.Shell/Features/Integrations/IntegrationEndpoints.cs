using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Tools;
using Lots.Shell.Features.Conversations;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Integrations;

/// <summary>A tool a server offers. Profiles = where it is declared (with its risk class); empty = unclassified, so not callable.</summary>
public sealed record ServerToolDto(string Name, string Description, IReadOnlyList<string> Profiles);

public sealed record ServerDto(
    string Name, string Url, string Auth, string? CredentialType, IReadOnlyList<string> Profiles, string Health, string? Error,
    DateTimeOffset? CheckedAt, IReadOnlyList<ServerToolDto> Tools);

/// <summary>
/// One catalog row. Status: exposed (declared and offered by a server), missing (declared, but no reachable server offers it),
/// unclassified (offered by a server but declared in no profile: deny by default, the model never sees it).
/// </summary>
public sealed record CatalogEntry(
    string Tool, string Description, string? Server, string? Profile, string? Risk, string Status,
    IReadOnlyList<string> AllowedRoles, IReadOnlyList<string> ApprovalRoles, IReadOnlyList<string> ApproverRoles,
    int Calls, DateTimeOffset? LastUsed);

internal static class IntegrationAccess
{
    public static bool IsAuditor(Principal me, IConfiguration config)
    {
        var auditors = (config["Auth:AuditRoles"] ?? "admin,auditor").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ConversationViews.IsAdmin(me, config) || me.Roles.Any(r => auditors.Contains(r, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The tool servers the profiles use, their health and the tools they offer. Admins only: addresses and backend auth are
/// operator information that end users never see (principle 3).
/// </summary>
public sealed class ListServersEndpoint(ToolInvoker tools, ProfileRegistry profiles, ICurrentPrincipal who, IConfiguration config)
    : EndpointWithoutRequest<List<ServerDto>>
{
    public override void Configure()
    {
        Get("/integrations/servers");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!ConversationViews.IsAdmin(who.Get(HttpContext), config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var status = await tools.ServerStatusAsync(ct);
        var result = profiles.Servers.Select(server =>
        {
            var seen = status.FirstOrDefault(s => s.Name == server.Name);
            var using_ = profiles.All.Where(p => p.Servers.Any(s => s.Name == server.Name)).Select(p => p.Name).ToList();
            var offered = (seen?.Tools ?? []).Select(t => new ServerToolDto(t.Name, t.Description,
                profiles.All.Where(p => using_.Contains(p.Name))
                    .SelectMany(p => p.Tools.Where(d => d.Name == t.Name).Select(d => $"{p.Name}:{d.Risk}")).ToList())).ToList();
            return new ServerDto(server.Name, server.Url, server.Auth, server.Credentials?.Type, using_,
                seen?.Health ?? "unknown", seen?.Error, seen?.CheckedAt, offered);
        }).ToList();

        await Send.OkAsync(result, ct);
    }
}

/// <summary>Every tool per profile with its risk class, who may use and approve it, and usage. Admins and auditors.</summary>
public sealed class CatalogEndpoint(ToolInvoker tools, ProfileRegistry profiles, LotsDbContext db, ICurrentPrincipal who, IConfiguration config)
    : EndpointWithoutRequest<List<CatalogEntry>>
{
    public override void Configure()
    {
        Get("/integrations/catalog");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!IntegrationAccess.IsAuditor(who.Get(HttpContext), config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var status = await tools.ServerStatusAsync(ct);
        var usage = (await (from s in db.RunSteps.AsNoTracking()
                            join r in db.Runs.AsNoTracking() on s.RunId equals r.Id
                            where s.Kind == StepKind.ToolCall
                            group s by new { r.Profile, s.Name } into g
                            select new { g.Key.Profile, g.Key.Name, Calls = g.Count(), Last = g.Max(x => x.CreatedAt) })
                .ToListAsync(ct))
            .ToDictionary(u => (u.Profile, u.Name));

        var rows = new List<CatalogEntry>();
        foreach (var profile in profiles.All)
        {
            var mine = status.Where(s => profile.Servers.Any(p => p.Name == s.Name)).ToList();
            foreach (var tool in profile.Tools)
            {
                var server = mine.FirstOrDefault(s => s.Tools.Any(t => t.Name == tool.Name));
                // A per-user server cannot be listed here, so its tools are not reported missing.
                var unknown = server is null && mine.Any(s => s.Health == "per-user");
                var used = usage.GetValueOrDefault((profile.Name, tool.Name));
                rows.Add(new CatalogEntry(
                    tool.Name, server?.Tools.First(t => t.Name == tool.Name).Description ?? "", server?.Name, profile.Name, tool.Risk.ToString(),
                    server is not null ? "exposed" : unknown ? "per-user" : "missing",
                    profile.Roles.Where(r => r.Allow.Contains(tool.Risk)).Select(r => r.Name).ToList(),
                    profile.Roles.Where(r => r.Allow.Contains(tool.Risk) && r.RequireApproval.Contains(tool.Risk)).Select(r => r.Name).ToList(),
                    profile.Roles.Where(r => r.MayApprove.Contains(tool.Risk)).Select(r => r.Name).ToList(),
                    used?.Calls ?? 0, used?.Last));
            }
        }

        foreach (var server in status)
            foreach (var tool in server.Tools)
            {
                var declared = profiles.All.Any(p => p.Servers.Any(s => s.Name == server.Name) && p.Tools.Any(t => t.Name == tool.Name));
                if (!declared)
                    rows.Add(new CatalogEntry(tool.Name, tool.Description, server.Name, null, null, "unclassified", [], [], [], 0, null));
            }

        await Send.OkAsync(rows.OrderBy(r => r.Status == "unclassified").ThenBy(r => r.Profile).ThenBy(r => r.Tool).ToList(), ct);
    }
}
