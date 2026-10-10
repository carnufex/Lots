using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Features.Audit;

public sealed record AuditQuery(string? User = null, DateTimeOffset? From = null, DateTimeOffset? To = null, Guid? RunId = null, int? Limit = null,
    string? Tool = null, string? Decision = null, string? Profile = null, string? Format = null);

public sealed record AuditDto(
    Guid Id, DateTimeOffset At, string User, string Roles, string Profile, int ProfileVersion, Guid RunId,
    string Tool, string? Arguments, string Decision, string Reason, string? Approver, string? Result, string? BackendAuth,
    long? Seq = null, string? Hash = null);

internal static class AuditFilter
{
    public static bool Allowed(Lots.Shell.Core.Policy.Principal me, IConfiguration config)
    {
        var allowed = (config["Auth:AuditRoles"] ?? "admin,auditor").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return me.Roles.Any(r => allowed.Contains(r, StringComparer.OrdinalIgnoreCase));
    }

    public static IQueryable<AuditRecord> Apply(IQueryable<AuditRecord> q, AuditQuery req)
    {
        if (!string.IsNullOrWhiteSpace(req.User)) q = q.Where(a => a.UserId == req.User);
        if (req.From is { } from) q = q.Where(a => a.At >= from);
        if (req.To is { } to) q = q.Where(a => a.At <= to);
        if (req.RunId is { } runId) q = q.Where(a => a.RunId == runId);
        if (!string.IsNullOrWhiteSpace(req.Tool)) q = q.Where(a => a.Tool == req.Tool);
        if (!string.IsNullOrWhiteSpace(req.Profile)) q = q.Where(a => a.Profile == req.Profile);
        if (Enum.TryParse<AuditDecision>(req.Decision, true, out var decision)) q = q.Where(a => a.Decision == decision);
        return q;
    }

    public static AuditDto ToDto(AuditRecord a) => new(
        a.Id, a.At, a.UserId, a.Roles, a.Profile, a.ProfileVersion, a.RunId, a.Tool, a.ArgumentsJson,
        a.Decision.ToString(), a.Reason, a.ApproverId, a.ResultStatus, a.BackendAuth, a.Seq, a.Hash);
}

/// <summary>
/// Read access to the audit log for admins and auditors (<c>Auth:AuditRoles</c>, default admin,auditor).
/// There is deliberately no write or delete endpoint: the log is append-only.
/// </summary>
public sealed class ListAuditEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config)
    : Endpoint<AuditQuery, List<AuditDto>>
{
    public override void Configure()
    {
        Get("/audit");
    }

    public override async Task HandleAsync(AuditQuery req, CancellationToken ct)
    {
        if (!AuditFilter.Allowed(who.Get(HttpContext), config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var rows = await AuditFilter.Apply(db.AuditLog.AsNoTracking(), req).OrderByDescending(a => a.At).Take(Math.Clamp(req.Limit ?? 100, 1, 1000)).ToListAsync(ct);
        await Send.OkAsync(rows.Select(AuditFilter.ToDto).ToList(), ct);
    }
}

/// <summary>
/// The audit log for a period as CSV or JSON lines, for SIEM import or an auditor (#81). Includes the chain position and hash so the
/// export itself can be checked against the chain.
/// </summary>
public sealed class ExportAuditEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config) : Endpoint<AuditQuery>
{
    public override void Configure() => Get("/audit/export");

    public override async Task HandleAsync(AuditQuery req, CancellationToken ct)
    {
        if (!AuditFilter.Allowed(who.Get(HttpContext), config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        var json = string.Equals(req.Format, "json", StringComparison.OrdinalIgnoreCase);
        HttpContext.Response.ContentType = json ? "application/x-ndjson" : "text/csv; charset=utf-8";
        HttpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"lots-audit.{(json ? "jsonl" : "csv")}\"";
        await using var writer = new StreamWriter(HttpContext.Response.Body, new System.Text.UTF8Encoding(false));
        if (!json) await writer.WriteLineAsync("seq,at,user,roles,profile,profile_version,run_id,tool,decision,reason,approver,result,backend_auth,arguments,hash");
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        await foreach (var a in AuditFilter.Apply(db.AuditLog.AsNoTracking(), req).OrderBy(a => a.At).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (json) await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(AuditFilter.ToDto(a), options));
            else await writer.WriteLineAsync(string.Join(',', new object?[] { a.Seq, a.At.ToString("O"), a.UserId, a.Roles, a.Profile, a.ProfileVersion, a.RunId, a.Tool,
                a.Decision, a.Reason, a.ApproverId, a.ResultStatus, a.BackendAuth, a.ArgumentsJson, a.Hash }.Select(Csv)));
        }
        await writer.FlushAsync(ct);
    }

    /// <summary>RFC 4180 quoting, and a leading quote for values a spreadsheet would run as a formula.</summary>
    public static string Csv(object? v)
    {
        var s = v?.ToString() ?? "";
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

/// <summary>Checks the audit hash chain end to end (#81): intact, or the first row where it breaks and why.</summary>
public sealed class VerifyAuditEndpoint(LotsDbContext db, ICurrentPrincipal who, IConfiguration config) : EndpointWithoutRequest<Lots.Shell.Core.Audit.ChainVerification>
{
    public override void Configure() => Get("/audit/verify");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!AuditFilter.Allowed(who.Get(HttpContext), config))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }
        await Send.OkAsync(await Lots.Shell.Core.Audit.AuditSealer.VerifyAsync(db, ct), ct);
    }
}
