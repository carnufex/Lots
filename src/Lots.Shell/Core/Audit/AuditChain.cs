using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Core.Audit;

/// <summary>
/// Tamper evidence for the audit log (#81): rows are numbered in insert order and each carries SHA-256 over its content and the previous
/// row's hash. Changing, deleting or reordering a sealed row breaks the chain from there on, which verification reports. Sealing runs in
/// the background (one replica at a time) so writers never contend on the chain.
/// </summary>
public static class AuditHash
{
    public static string Compute(AuditRecord a, string? previous)
    {
        var canonical = string.Join('\u001f', a.Seq, a.Id, a.At.ToUniversalTime().ToString("O"), a.UserId, a.Roles, a.Profile, a.ProfileVersion,
            a.RunId, a.Tool, a.ToolCallId, a.ArgumentsJson, a.Decision, a.Reason, a.ApproverId, a.BackendAuth, a.ResultStatus, previous ?? "")
            + (a.Preview is null ? "" : "\u001fpreview=" + a.Preview) // rows without a preview hash exactly as before #156
            + (a.Playbook is null ? "" : "\u001fplaybook=" + a.Playbook); // likewise for #161
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed record ChainVerification(bool Intact, long Sealed, long? FirstBrokenSeq, string? Problem, long Unsealed);

public sealed class AuditSealer(IServiceScopeFactory scopes, ILogger<AuditSealer> logger) : BackgroundService
{
    private const long LockKey = 0x4c6f7473_41756474; // "LotsAudt"

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await SealAsync(stop); }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested) { logger.LogWarning("Audit sealing failed: {Error}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(5), stop).ContinueWith(_ => { });
        }
    }

    public async Task<int> SealAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var relational = db.Database.IsRelational();
        if (relational)
        {
            await db.Database.OpenConnectionAsync(ct);
            var got = await db.Database.SqlQueryRaw<bool>($"SELECT pg_try_advisory_lock({LockKey}) AS \"Value\"").SingleAsync(ct);
            if (!got) return 0; // another replica is sealing
        }
        try
        {
            var last = await db.AuditLog.Where(a => a.Seq != null).OrderByDescending(a => a.Seq).Select(a => new { a.Seq, a.Hash }).FirstOrDefaultAsync(ct);
            var seq = last?.Seq ?? 0;
            var previous = last?.Hash;
            var batch = await db.AuditLog.Where(a => a.Seq == null).OrderBy(a => a.At).ThenBy(a => a.Id).Take(500).ToListAsync(ct);
            foreach (var row in batch)
            {
                row.Seq = ++seq;
                row.PrevHash = previous;
                row.Hash = AuditHash.Compute(row, previous);
                previous = row.Hash;
            }
            if (batch.Count > 0) await db.SaveChangesAsync(ct);
            return batch.Count;
        }
        finally
        {
            if (relational)
            {
                await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_unlock({LockKey})", ct);
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    public static async Task<ChainVerification> VerifyAsync(LotsDbContext db, CancellationToken ct)
    {
        string? previous = null;
        long expected = 0, count = 0;
        var unsealed = await db.AuditLog.LongCountAsync(a => a.Seq == null, ct);
        // The oldest rows may have been purged by retention: the chain then starts at the first remaining row.
        var first = true;
        await foreach (var row in db.AuditLog.AsNoTracking().Where(a => a.Seq != null).OrderBy(a => a.Seq).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (first) { expected = row.Seq!.Value; previous = row.PrevHash; first = false; }
            if (row.Seq != expected) return new ChainVerification(false, count, expected, $"row {expected} is missing", unsealed);
            if (row.PrevHash != previous) return new ChainVerification(false, count, row.Seq, "the link to the previous row does not match", unsealed);
            if (AuditHash.Compute(row, previous) != row.Hash) return new ChainVerification(false, count, row.Seq, "the row's content was changed", unsealed);
            previous = row.Hash;
            expected++;
            count++;
        }
        return new ChainVerification(true, count, null, null, unsealed);
    }
}

public sealed class AuditForwardOptions
{
    public const string Section = "Audit:Forward";
    /// <summary>POSTs batches of sealed rows as JSON arrays (a SIEM HTTP collector, Splunk HEC behind a proxy, Loki, ...).</summary>
    public string? WebhookUrl { get; set; }
    public string? WebhookAuthHeaderRef { get; set; }
    /// <summary>Syslog (RFC 5424, one JSON message per row) over TCP or UDP.</summary>
    public string? SyslogHost { get; set; }
    public int SyslogPort { get; set; } = 514;
    public string SyslogProtocol { get; set; } = "tcp";
}

/// <summary>Sends sealed audit rows, in order and exactly from where it stopped, to the configured targets (#81).</summary>
public sealed class AuditForwarder(IServiceScopeFactory scopes, IOptions<AuditForwardOptions> options, IHttpClientFactory http, ILogger<AuditForwarder> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var o = options.Value;
        if (string.IsNullOrEmpty(o.WebhookUrl) && string.IsNullOrEmpty(o.SyslogHost)) return;
        while (!stop.IsCancellationRequested)
        {
            if (!string.IsNullOrEmpty(o.WebhookUrl)) await ForwardAsync("webhook", SendWebhookAsync, stop);
            if (!string.IsNullOrEmpty(o.SyslogHost)) await ForwardAsync("syslog", SendSyslogAsync, stop);
            await Task.Delay(TimeSpan.FromSeconds(10), stop).ContinueWith(_ => { });
        }
    }

    private async Task ForwardAsync(string target, Func<IReadOnlyList<AuditRecord>, CancellationToken, Task> send, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
            var state = await db.AuditForwardState.SingleOrDefaultAsync(s => s.Target == target, ct);
            if (state is null) db.AuditForwardState.Add(state = new AuditForwardStateRecord { Target = target });
            var rows = await db.AuditLog.AsNoTracking().Where(a => a.Seq != null && a.Seq > state.LastSeq).OrderBy(a => a.Seq).Take(200).ToListAsync(ct);
            if (rows.Count == 0) return;
            await send(rows, ct);
            state.LastSeq = rows[^1].Seq!.Value;
            state.LastSentAt = DateTimeOffset.UtcNow;
            state.LastError = null;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Audit forwarding to {Target} failed (will retry from the same row): {Error}", target, ex.Message);
        }
    }

    private async Task SendWebhookAsync(IReadOnlyList<AuditRecord> rows, CancellationToken ct)
    {
        var o = options.Value;
        using var msg = new HttpRequestMessage(HttpMethod.Post, o.WebhookUrl) { Content = JsonContent.Create(rows, options: Json) };
        if (o.WebhookAuthHeaderRef is { } r) msg.Headers.TryAddWithoutValidation("Authorization", Mcp.SecretReference.Resolve(r));
        using var res = await http.CreateClient(nameof(AuditForwarder)).SendAsync(msg, ct);
        res.EnsureSuccessStatusCode();
    }

    private async Task SendSyslogAsync(IReadOnlyList<AuditRecord> rows, CancellationToken ct)
    {
        var o = options.Value;
        var lines = rows.Select(r => Syslog(r)).ToList();
        if (o.SyslogProtocol.Equals("udp", StringComparison.OrdinalIgnoreCase))
        {
            using var udp = new UdpClient();
            foreach (var line in lines) await udp.SendAsync(Encoding.UTF8.GetBytes(line), o.SyslogHost!, o.SyslogPort, ct);
            return;
        }
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(o.SyslogHost!, o.SyslogPort, ct);
        await using var stream = tcp.GetStream();
        foreach (var line in lines)
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"{bytes.Length} "), ct); // RFC 6587 octet counting
            await stream.WriteAsync(bytes, ct);
        }
    }

    /// <summary>RFC 5424: facility auth (4) / severity notice (5), the row as structured JSON in the message.</summary>
    public static string Syslog(AuditRecord r) =>
        $"<37>1 {r.At.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ} {Environment.MachineName} lots - audit [lots@32473 seq=\"{r.Seq}\" decision=\"{r.Decision}\"] " +
        JsonSerializer.Serialize(r, Json);
}
