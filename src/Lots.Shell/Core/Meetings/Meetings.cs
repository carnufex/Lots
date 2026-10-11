using System.Globalization;
using System.Text;
using System.Text.Json;
using Lots.Shell.Core.Speech;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Core.Meetings;

public static class MeetingStatus
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed class MeetingOptions
{
    public const string Section = "Meetings";
    /// <summary>Encrypted recordings live here (a persistent volume); unset = <c>Speech:AudioPath</c>/meetings. No path = no meetings.</summary>
    public string? AudioPath { get; set; }
    public int MaxMegabytes { get; set; } = 300;
    /// <summary>The recording is deleted after this many days; the transcript stays (0 = keep it).</summary>
    public int AudioRetentionDays { get; set; } = 30;
    /// <summary>How long the voice service may take for one recording.</summary>
    public int TimeoutMinutes { get; set; } = 60;
    public int MaxAttempts { get; set; } = 3;
    /// <summary>Word timestamps place a speaker change inside a segment (#44).</summary>
    public bool WordAlignment { get; set; } = true;
    public bool Enabled { get; set; } = true;
}

public sealed record MeetingWord(double Start, double End, string Word);
public sealed record MeetingSegmentIn(double Start, double End, string Text, IReadOnlyList<MeetingWord> Words);
public sealed record SpeakerTurn(double Start, double End, int Speaker);

/// <summary>What the voice service returns for a recording: timestamped text and who spoke when, merged here.</summary>
public sealed record MeetingAnalysis(string Language, double Duration, IReadOnlyList<MeetingSegmentIn> Segments, IReadOnlyList<SpeakerTurn> Turns);

public sealed record MergedLine(double Start, double End, string Speaker, string Text);

public interface IMeetingTranscriber
{
    Task<MeetingAnalysis> AnalyseAsync(Stream audio, string fileName, string contentType, string? language, int? speakers, IReadOnlyList<string>? vocabulary,
        bool words, CancellationToken ct);
}

/// <summary>The voice service's <c>POST /v1/audio/meetings</c> (#41).</summary>
public sealed class VoiceMeetingTranscriber(HttpClient http, IOptions<SpeechOptions> speech) : IMeetingTranscriber
{
    public async Task<MeetingAnalysis> AnalyseAsync(Stream audio, string fileName, string contentType, string? language, int? speakers,
        IReadOnlyList<string>? vocabulary, bool words, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(audio);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType);
        form.Add(file, "file", fileName);
        if (!string.IsNullOrEmpty(language)) form.Add(new StringContent(language), "language");
        if (speakers is { } n) form.Add(new StringContent(n.ToString(CultureInfo.InvariantCulture)), "num_speakers");
        if (OpenAiCompatibleSpeech.PromptOf(vocabulary, speech.Value.MaxVocabularyChars) is { Length: > 0 } prompt) form.Add(new StringContent(prompt), "prompt");
        form.Add(new StringContent(words ? "true" : "false"), "word_timestamps");
        HttpResponseMessage response;
        try { response = await http.PostAsync("audio/meetings", form, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SpeechUnavailableException("The voice service is unreachable.", ex);
        }
        using var _ = response;
        var body = await response.Content.ReadAsStringAsync(ct);
        if ((int)response.StatusCode is 503 or 502 or 504) throw new SpeechUnavailableException($"The voice service answered {(int)response.StatusCode}.");
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(Detail(body) ?? $"The voice service answered {(int)response.StatusCode}.");
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        var segments = r.GetProperty("segments").EnumerateArray().Select(s => new MeetingSegmentIn(s.GetProperty("start").GetDouble(), s.GetProperty("end").GetDouble(),
            s.GetProperty("text").GetString() ?? "",
            s.TryGetProperty("words", out var w)
                ? w.EnumerateArray().Select(x => new MeetingWord(x.GetProperty("start").GetDouble(), x.GetProperty("end").GetDouble(), x.GetProperty("word").GetString() ?? "")).ToList()
                : [])).ToList();
        var turns = r.GetProperty("turns").EnumerateArray()
            .Select(t => new SpeakerTurn(t.GetProperty("start").GetDouble(), t.GetProperty("end").GetDouble(), t.GetProperty("speaker").GetInt32())).ToList();
        return new MeetingAnalysis(r.GetProperty("language").GetString() ?? "", r.GetProperty("duration").GetDouble(), segments, turns);
    }

    private static string? Detail(string body)
    {
        try { return JsonDocument.Parse(body).RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null; }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Who said what (#41, #44): each piece of text gets the speaker whose turn overlaps it most. With word timestamps the unit is the word,
/// so a speaker change inside a Whisper segment splits it; without, the whole segment goes to one speaker. Speakers are named
/// "Speaker 1..N" in order of first appearance; the labels are suggestions (diarization is weak on overlapping speech).
/// </summary>
public static class MeetingMerge
{
    public static List<MergedLine> Merge(MeetingAnalysis a, bool useWords)
    {
        var names = new Dictionary<int, string>();
        string Name(int? speaker)
        {
            if (speaker is not { } s) return "Speaker ?";
            if (!names.TryGetValue(s, out var n)) names[s] = n = $"Speaker {names.Count + 1}";
            return n;
        }
        int? SpeakerOf(double start, double end)
        {
            var best = a.Turns.Select(t => (t.Speaker, Overlap: Math.Min(end, t.End) - Math.Max(start, t.Start))).Where(x => x.Overlap > 0)
                .GroupBy(x => x.Speaker).Select(g => (g.Key, Sum: g.Sum(x => x.Overlap))).OrderByDescending(x => x.Sum).FirstOrDefault();
            if (best.Sum > 0) return best.Key;
            // In a gap between turns: the nearest turn.
            var mid = (start + end) / 2;
            return a.Turns.OrderBy(t => Math.Min(Math.Abs(t.Start - mid), Math.Abs(t.End - mid))).Select(t => (int?)t.Speaker).FirstOrDefault();
        }

        var lines = new List<MergedLine>();
        foreach (var seg in a.Segments.Where(s => s.Text.Trim().Length > 0).OrderBy(s => s.Start))
        {
            if (!useWords || seg.Words.Count == 0)
            {
                lines.Add(new MergedLine(seg.Start, seg.End, Name(SpeakerOf(seg.Start, seg.End)), seg.Text.Trim()));
                continue;
            }
            int? current = null;
            var sb = new StringBuilder();
            double from = 0, to = 0;
            foreach (var w in seg.Words)
            {
                var who = SpeakerOf(w.Start, Math.Max(w.End, w.Start + 0.01));
                if (sb.Length > 0 && who != current)
                {
                    lines.Add(new MergedLine(from, to, Name(current), sb.ToString().Trim()));
                    sb.Clear();
                }
                if (sb.Length == 0) { from = w.Start; current = who; }
                sb.Append(w.Word);
                to = w.End;
            }
            if (sb.Length > 0) lines.Add(new MergedLine(from, to, Name(current), sb.ToString().Trim()));
        }
        // Consecutive lines of the same speaker read better as one.
        var merged = new List<MergedLine>();
        foreach (var l in lines)
            if (merged.Count > 0 && merged[^1].Speaker == l.Speaker && l.Start - merged[^1].End < 1.5)
                merged[^1] = merged[^1] with { End = l.End, Text = merged[^1].Text + " " + l.Text };
            else merged.Add(l);
        return merged;
    }

    /// <summary>
    /// Share of spoken time attributed to the wrong speaker (#44 measure): true speakers and labels are paired one to one, largest
    /// overlap first, so merging two people into one label counts as an error.
    /// </summary>
    public static double SpeakerError(IReadOnlyList<MergedLine> lines, IReadOnlyList<(double Start, double End, string Speaker)> truth)
    {
        var overlap = new Dictionary<(string, string), double>();
        foreach (var t in truth)
        foreach (var l in lines)
        {
            var o = Math.Min(t.End, l.End) - Math.Max(t.Start, l.Start);
            if (o > 0) overlap[(t.Speaker, l.Speaker)] = overlap.GetValueOrDefault((t.Speaker, l.Speaker)) + o;
        }
        var total = truth.Sum(t => t.End - t.Start);
        var usedTruth = new HashSet<string>();
        var usedLabel = new HashSet<string>();
        var right = 0.0;
        foreach (var (key, value) in overlap.OrderByDescending(kv => kv.Value))
            if (!usedTruth.Contains(key.Item1) && !usedLabel.Contains(key.Item2))
            {
                usedTruth.Add(key.Item1);
                usedLabel.Add(key.Item2);
                right += value;
            }
        return total <= 0 ? 0 : Math.Round(1 - right / total, 4);
    }
}

public static class MeetingExport
{
    public static readonly string[] Formats = ["txt", "srt", "vtt", "json"];

    public static string Render(string format, string title, IReadOnlyList<(double Start, double End, string Speaker, string Text)> lines)
    {
        static string Ts(double s, char sep) => TimeSpan.FromSeconds(s).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) + sep + ((int)(s * 1000 % 1000)).ToString("000");
        var sb = new StringBuilder();
        switch (format)
        {
            case "srt":
                for (var i = 0; i < lines.Count; i++)
                    sb.Append(i + 1).Append('\n').Append(Ts(lines[i].Start, ',')).Append(" --> ").Append(Ts(lines[i].End, ',')).Append('\n')
                      .Append(lines[i].Speaker).Append(": ").Append(lines[i].Text).Append("\n\n");
                return sb.ToString();
            case "vtt":
                sb.Append("WEBVTT\n\n");
                foreach (var l in lines)
                    sb.Append(Ts(l.Start, '.')).Append(" --> ").Append(Ts(l.End, '.')).Append('\n').Append("<v ").Append(l.Speaker).Append('>').Append(l.Text).Append("\n\n");
                return sb.ToString();
            case "json":
                return JsonSerializer.Serialize(new { title, lines = lines.Select(l => new { start = l.Start, end = l.End, speaker = l.Speaker, text = l.Text }) },
                    new JsonSerializerOptions { WriteIndented = true });
            default:
                sb.Append(title).Append("\n\n");
                foreach (var l in lines) sb.Append('[').Append(TimeSpan.FromSeconds(l.Start).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)).Append("] ")
                    .Append(l.Speaker).Append(": ").Append(l.Text).Append('\n');
                return sb.ToString();
        }
    }
}

/// <summary>Meeting recordings, encrypted with the Data Protection keys on a persistent volume. Transcripts are in the database.</summary>
public sealed class MeetingAudioStore
{
    private readonly IDataProtector _protector;
    private readonly string? _root;

    public MeetingAudioStore(IDataProtectionProvider protection, IOptions<MeetingOptions> options, IOptions<SpeechOptions> speech, ILogger<MeetingAudioStore> logger)
    {
        _protector = protection.CreateProtector("Lots.MeetingAudio");
        var path = options.Value.AudioPath ?? (string.IsNullOrWhiteSpace(speech.Value.AudioPath) ? null : Path.Combine(speech.Value.AudioPath, "meetings"));
        if (!options.Value.Enabled || string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(path);
            _root = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Meetings are off: {Path} is not writable ({Error}). Mount a volume there.", path, ex.Message);
        }
    }

    public bool Enabled => _root is not null;

    public async Task SaveAsync(Guid id, byte[] audio, CancellationToken ct) =>
        await File.WriteAllBytesAsync(Path.Combine(_root!, id.ToString("N")), _protector.Protect(audio), ct);

    public async Task<byte[]?> ReadAsync(Guid id, CancellationToken ct)
    {
        var file = _root is null ? null : Path.Combine(_root, id.ToString("N"));
        if (file is null || !File.Exists(file)) return null;
        try { return _protector.Unprotect(await File.ReadAllBytesAsync(file, ct)); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    public void Delete(Guid id)
    {
        if (_root is not null) File.Delete(Path.Combine(_root, id.ToString("N")));
    }
}

/// <summary>
/// Processes uploaded meetings (#41) like runs are processed: claimed with a lease (renewed while working), so a replica that dies
/// mid-way leaves the meeting to another one after the lease expires, and processing starts over cleanly. Also purges old recordings.
/// </summary>
public sealed class MeetingWorker(IServiceScopeFactory scopes, IOptions<MeetingOptions> options, TimeProvider clock, ILogger<MeetingWorker> logger) : BackgroundService
{
    public static readonly TimeSpan LeaseTtl = TimeSpan.FromSeconds(60);
    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}"[..40];

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!options.Value.Enabled) return;
        var lastPurge = DateTimeOffset.MinValue;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!await ProcessNextAsync(_owner, stop)) await Task.Delay(TimeSpan.FromSeconds(3), stop);
                if (clock.GetUtcNow() - lastPurge > TimeSpan.FromHours(1))
                {
                    lastPurge = clock.GetUtcNow();
                    await PurgeAsync(stop);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogWarning("Meeting worker: {Error}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), stop).ContinueWith(_ => { });
            }
        }
    }

    /// <summary>Claims one queued (or abandoned) meeting and processes it. False when there was nothing to do.</summary>
    public async Task<bool> ProcessNextAsync(string owner, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var candidates = await db.Meetings.AsNoTracking()
            .Where(m => (m.Status == MeetingStatus.Queued || m.Status == MeetingStatus.Processing) && (m.LeaseUntilMs == null || m.LeaseUntilMs < now))
            .OrderBy(m => m.CreatedAt).Select(m => m.Id).Take(3).ToListAsync(ct);
        foreach (var id in candidates)
            if (await TryClaimAsync(db, id, owner, now, ct))
            {
                await ProcessAsync(scope.ServiceProvider, id, owner, ct);
                return true;
            }
        return false;
    }

    private static async Task<bool> TryClaimAsync(LotsDbContext db, Guid id, string owner, long now, CancellationToken ct)
    {
        var until = now + (long)LeaseTtl.TotalMilliseconds;
        if (db.Database.IsRelational())
            return await db.Meetings.Where(m => m.Id == id && (m.Status == MeetingStatus.Queued || m.Status == MeetingStatus.Processing)
                                                && (m.LeaseUntilMs == null || m.LeaseUntilMs < now))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.LeaseOwner, owner).SetProperty(m => m.LeaseUntilMs, until), ct) == 1;
        var row = await db.Meetings.SingleAsync(m => m.Id == id, ct);
        if (row.LeaseUntilMs is { } u && u >= now) return false;
        row.LeaseOwner = owner;
        row.LeaseUntilMs = until;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task ProcessAsync(IServiceProvider sp, Guid id, string owner, CancellationToken ct)
    {
        var db = sp.GetRequiredService<LotsDbContext>();
        var meeting = await db.Meetings.SingleAsync(m => m.Id == id, ct);
        if (meeting.CancelRequested)
        {
            meeting.Status = MeetingStatus.Cancelled;
            meeting.LeaseOwner = null;
            meeting.LeaseUntilMs = null;
            await db.SaveChangesAsync(ct);
            return;
        }
        meeting.Status = MeetingStatus.Processing;
        meeting.Attempts++;
        meeting.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        work.CancelAfter(TimeSpan.FromMinutes(options.Value.TimeoutMinutes));
        // Keep the lease while the voice service works; losing it (or a cancel request) stops this attempt.
        var renew = Task.Run(async () =>
        {
            using var s = scopes.CreateScope();
            var rdb = s.ServiceProvider.GetRequiredService<LotsDbContext>();
            while (!work.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), work.Token).ContinueWith(_ => { });
                if (work.IsCancellationRequested) break;
                var row = await rdb.Meetings.SingleOrDefaultAsync(m => m.Id == id, CancellationToken.None);
                if (row is null || row.LeaseOwner != owner || row.CancelRequested) { work.Cancel(); break; }
                row.LeaseUntilMs = clock.GetUtcNow().ToUnixTimeMilliseconds() + (long)LeaseTtl.TotalMilliseconds;
                await rdb.SaveChangesAsync(CancellationToken.None);
                rdb.ChangeTracker.Clear();
            }
        });

        try
        {
            var audio = await sp.GetRequiredService<MeetingAudioStore>().ReadAsync(id, work.Token)
                        ?? throw new InvalidOperationException("The recording is gone (deleted, or the encryption keys changed).");
            var vocabulary = await Features.Voice.Vocabulary.ForUserAsync(db, meeting.UserId, sp.GetRequiredService<IOptions<SpeechOptions>>().Value, work.Token);
            var analysis = await sp.GetRequiredService<IMeetingTranscriber>().AnalyseAsync(new MemoryStream(audio), meeting.FileName, meeting.ContentType,
                meeting.RequestedLanguage, meeting.Speakers, vocabulary, options.Value.WordAlignment, work.Token);
            var lines = MeetingMerge.Merge(analysis, options.Value.WordAlignment);
            await db.MeetingSegments.Where(s => s.MeetingId == id).ExecuteDeleteSafeAsync(db, ct); // a retry starts clean
            db.MeetingSegments.AddRange(lines.Select((l, i) => new MeetingSegmentRecord
            {
                MeetingId = id, Seq = i, StartMs = (long)(l.Start * 1000), EndMs = (long)(l.End * 1000), Speaker = l.Speaker, Text = l.Text,
            }));
            meeting.Status = MeetingStatus.Done;
            meeting.Language = analysis.Language;
            meeting.DurationSeconds = analysis.Duration;
            meeting.SpeakerCount = lines.Select(l => l.Speaker).Distinct().Count();
            meeting.Error = null;
            meeting.ProcessedAt = clock.GetUtcNow();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var cancelled = (await db.Meetings.AsNoTracking().Where(m => m.Id == id).Select(m => m.CancelRequested).SingleAsync(CancellationToken.None));
            if (cancelled) meeting.Status = MeetingStatus.Cancelled;
            else if (ex is SpeechUnavailableException && meeting.Attempts < options.Value.MaxAttempts)
                meeting.Status = MeetingStatus.Queued; // the voice service is away: try again later
            else
            {
                meeting.Status = MeetingStatus.Failed;
                meeting.Error = Security.SecretRedactor.Redact(ex is OperationCanceledException ? $"Took longer than {options.Value.TimeoutMinutes} minutes." : ex.Message);
            }
            logger.LogInformation("Meeting {Meeting} {Status}: {Error}", id, meeting.Status, ex.Message);
        }
        finally
        {
            await work.CancelAsync();
            await renew;
        }
        meeting.UpdatedAt = clock.GetUtcNow();
        meeting.LeaseOwner = null;
        meeting.LeaseUntilMs = meeting.Status == MeetingStatus.Queued ? clock.GetUtcNow().AddSeconds(30).ToUnixTimeMilliseconds() : null; // back off
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Recordings older than the retention go; their transcripts stay.</summary>
    public async Task PurgeAsync(CancellationToken ct)
    {
        if (options.Value.AudioRetentionDays <= 0) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<MeetingAudioStore>();
        var cutoff = clock.GetUtcNow().AddDays(-options.Value.AudioRetentionDays);
        foreach (var m in await db.Meetings.Where(m => !m.AudioDeleted && m.CreatedAt < cutoff && m.Status != MeetingStatus.Processing).ToListAsync(ct))
        {
            store.Delete(m.Id);
            m.AudioDeleted = true;
        }
        await db.SaveChangesAsync(ct);
    }
}

internal static class MeetingDbExtensions
{
    /// <summary>ExecuteDelete where the provider supports it (Postgres), tracked removal otherwise (in-memory tests).</summary>
    public static async Task ExecuteDeleteSafeAsync(this IQueryable<MeetingSegmentRecord> query, LotsDbContext db, CancellationToken ct)
    {
        if (db.Database.IsRelational()) await query.ExecuteDeleteAsync(ct);
        else db.MeetingSegments.RemoveRange(await query.ToListAsync(ct));
    }
}
