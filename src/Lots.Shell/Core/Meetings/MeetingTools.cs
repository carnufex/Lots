using System.Globalization;
using System.Text;
using System.Text.Json;
using Lots.Shell.Core.Tools;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Core.Meetings;

/// <summary>
/// Meeting transcripts for the agent (#43): <c>list_meetings</c> and <c>read_meeting</c>. Only the run user's own meetings, never anyone
/// else's (an admin reads others' in the UI, audited, not through the agent). Callable only where a profile declares them, so policy and
/// the audit apply; the text reaches the model inside the untrusted-data envelope like every tool result, and long meetings are read in
/// parts so a result stays under the output cap.
/// </summary>
public sealed class MeetingToolSource(IServiceScopeFactory scopes, TimeProvider clock) : IToolSource
{
    public const string ServerName = "meetings";
    public const int PartChars = 6_000;

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static readonly IReadOnlyList<ToolDescriptor> Tools =
    [
        new("list_meetings", "Lists the user's own transcribed meetings, newest first: id, title, date, length, speakers.",
            Schema("""{ "type": "object", "properties": { "query": { "type": "string", "description": "Optional words in the title" } } }""")),
        new("read_meeting", "Reads a meeting transcript with speaker names and times, in parts for long meetings. Quote speakers and times.",
            Schema("""{ "type": "object", "properties": { "id": { "type": "string" }, "part": { "type": "integer", "description": "1-based; default 1" } }, "required": ["id"] }""")),
    ];

    public Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct) => Task.FromResult(Tools);

    public Task<string?> ServerOfAsync(string toolName, CancellationToken ct) => Task.FromResult<string?>(Tools.Any(t => t.Name == toolName) ? ServerName : null);

    public Task<IReadOnlyList<ServerStatus>> StatusAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ServerStatus>>(
        [new ServerStatus(ServerName, "builtin:meetings", ServerStatus.BuiltIn, "ok", Tools, null, clock.GetUtcNow())]);

    public async Task<string> CallAsync(string name, string argumentsJson, CancellationToken ct)
    {
        var user = (ToolCallContext.Current ?? throw new InvalidOperationException("Meetings need the caller's identity.")).Principal.UserId;
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var a = doc.RootElement;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        return name switch
        {
            "list_meetings" => await ListMeetingsAsync(db, user, a.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null, ct),
            "read_meeting" => await ReadAsync(db, user, a, ct),
            _ => throw new InvalidOperationException($"Unknown tool '{name}'."),
        };
    }

    private static async Task<string> ListMeetingsAsync(LotsDbContext db, string user, string? query, CancellationToken ct)
    {
        var rows = await db.Meetings.AsNoTracking().Where(m => m.UserId == user && m.Status == MeetingStatus.Done)
            .OrderByDescending(m => m.CreatedAt).Take(50).ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(query)) rows = rows.Where(m => m.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (rows.Count == 0) return "No transcribed meetings.";
        return string.Join("\n", rows.Select(m =>
            $"{m.Id} | {m.Title} | {m.CreatedAt:yyyy-MM-dd HH:mm} | {Clock(m.DurationSeconds ?? 0)} | {string.Join(", ", Speakers(m))}"));
    }

    private static async Task<string> ReadAsync(LotsDbContext db, string user, JsonElement a, CancellationToken ct)
    {
        if (!Guid.TryParse(a.TryGetProperty("id", out var i) ? i.GetString() : null, out var id)) return "Error: id must be a meeting id from list_meetings.";
        var m = await db.Meetings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == user, ct); // own meetings only
        if (m is null) return "Error: no such meeting of yours.";
        if (m.Status != MeetingStatus.Done) return $"The meeting is not transcribed yet ({m.Status}).";
        var names = m.SpeakerNamesJson is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(m.SpeakerNamesJson) ?? [];
        var lines = (await db.MeetingSegments.AsNoTracking().Where(s => s.MeetingId == id).OrderBy(s => s.Seq).ToListAsync(ct))
            .Select(s => $"[{Clock(s.StartMs / 1000.0)}] {names.GetValueOrDefault(s.Speaker) ?? s.Speaker}: {s.Text}").ToList();

        // Parts of whole lines, each under the cap.
        var parts = new List<string>();
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length + line.Length + 1 > PartChars && sb.Length > 0) { parts.Add(sb.ToString()); sb.Clear(); }
            sb.Append(line).Append('\n');
        }
        if (sb.Length > 0) parts.Add(sb.ToString());
        if (parts.Count == 0) return $"{m.Title}: the transcript is empty.";
        var part = Math.Clamp(a.TryGetProperty("part", out var p) && p.TryGetInt32(out var n) ? n : 1, 1, parts.Count);
        return $"{m.Title} ({m.CreatedAt:yyyy-MM-dd}, {Clock(m.DurationSeconds ?? 0)}), part {part} of {parts.Count}" +
               (part < parts.Count ? $" (read part {part + 1} next)" : "") + ":\n" + parts[part - 1];
    }

    private static IEnumerable<string> Speakers(MeetingRecord m)
    {
        var names = m.SpeakerNamesJson is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(m.SpeakerNamesJson) ?? [];
        return Enumerable.Range(1, m.SpeakerCount).Select(i => names.GetValueOrDefault($"Speaker {i}") ?? $"Speaker {i}");
    }

    private static string Clock(double seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
}
