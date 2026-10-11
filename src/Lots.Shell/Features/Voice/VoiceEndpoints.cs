using System.Diagnostics;
using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Speech;
using Lots.Shell.Features.Runs;
using Lots.Shell.Features.Settings;
using System.Text.Json;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Features.Voice;

public sealed class TranscribeRequest
{
    public IFormFile? Audio { get; set; }
    public string? Language { get; set; }

    /// <summary>The conversation this utterance belongs to (history and timing only).</summary>
    public Guid? ConversationId { get; set; }
}

public sealed record TranscribeResponse(string Text, string? Language, double? DurationSeconds);

/// <summary>
/// Dictation: audio in, text out. The text is only returned to the user to review and send; it carries no authority
/// (ADR 0013). Audio is not stored; only metadata is recorded.
/// </summary>
public sealed class TranscribeEndpoint(
    ISpeechToText stt, IOptions<SpeechOptions> options, LotsDbContext db, ICurrentPrincipal who, TimeProvider clock)
    : Endpoint<TranscribeRequest, TranscribeResponse>
{
    public override void Configure()
    {
        Post("/voice/transcribe");
        AllowFileUploads();
    }

    public override async Task HandleAsync(TranscribeRequest req, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            AddError("Voice is not configured.");
            await Send.ErrorsAsync(503, ct);
            return;
        }

        var language = string.IsNullOrWhiteSpace(req.Language) ? null : req.Language.Trim().ToLowerInvariant();
        if (language is not null && !SpeechOptions.Languages.Contains(language))
        {
            AddError("Language must be 'sv' or 'en'.");
            await Send.ErrorsAsync(400, ct);
            return;
        }
        if (req.Audio is null || req.Audio.Length == 0)
        {
            AddError("An audio file is required.");
            await Send.ErrorsAsync(400, ct);
            return;
        }
        if (req.Audio.Length > o.MaxAudioBytes)
        {
            AddError($"Audio is larger than {o.MaxAudioBytes / (1024 * 1024)} MB.");
            await Send.ErrorsAsync(413, ct);
            return;
        }

        var me = who.Get(HttpContext);
        try { await HttpContext.RequestServices.GetRequiredService<Lots.Shell.Core.Quotas.QuotaService>().CheckSpeechAsync(me, ct); }
        catch (Lots.Shell.Core.Quotas.QuotaExceededException ex)
        {
            AddError(ex.Message);
            await Send.ErrorsAsync(429, ct);
            return;
        }
        var sw = Stopwatch.StartNew();
        using var span = Lots.Shell.Core.Telemetry.Tracing.Source.StartActivity("speech_to_text", ActivityKind.Client);
        span?.SetTag("lots.speech.language", language ?? "auto");
        if (req.ConversationId is { } conversation) span?.SetTag("gen_ai.conversation.id", conversation.ToString());
        try
        {
            // Conversation turns keep the recording (ADR 0014); plain dictation does not (point 7).
            var store = HttpContext.RequestServices.GetRequiredService<IAudioStore>();
            byte[]? recording = null;
            Stream stream = req.Audio.OpenReadStream();
            if (req.ConversationId is not null && store.Enabled)
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                await stream.DisposeAsync();
                recording = buffer.ToArray();
                stream = new MemoryStream(recording);
            }
            await using var _ = stream;
            var words = await Vocabulary.ForUserAsync(db, me.UserId, o, ct);
            var transcript = await stt.TranscribeAsync(
                new AudioInput(stream, req.Audio.ContentType ?? "application/octet-stream", req.Audio.FileName ?? "audio"), language, words, ct);
            if (recording is not null && req.ConversationId is { } conv)
                await store.SaveAsync(conv, null, me.UserId, "user", (req.Audio.ContentType ?? "audio/webm").Split(';')[0], recording, clock.GetUtcNow(), CancellationToken.None);
            sw.Stop();
            span?.SetTag("lots.speech.audio_seconds", transcript.DurationSeconds);
            span?.SetTag("lots.speech.detected_language", transcript.Language);
            await RecordAsync(me.UserId, "Stt", transcript.Language ?? language, transcript.DurationSeconds, null, sw, "ok", null, req.ConversationId);
            await Send.OkAsync(new TranscribeResponse(transcript.Text, transcript.Language, transcript.DurationSeconds), ct);
        }
        catch (SpeechUnavailableException)
        {
            sw.Stop();
            await RecordAsync(me.UserId, "Stt", language, null, null, sw, "error", null, req.ConversationId);
            AddError("Voice is unavailable right now. Type your question instead.");
            await Send.ErrorsAsync(503, ct);
        }
    }

    private async Task RecordAsync(string user, string direction, string? lang, double? seconds, int? chars, Stopwatch sw, string outcome, Guid? runId, Guid? conversationId = null)
    {
        db.VoiceUsage.Add(new VoiceUsageRecord
        {
            Id = Guid.NewGuid(), At = clock.GetUtcNow(), UserId = user, Direction = direction, Language = lang,
            AudioSeconds = seconds, Characters = chars, LatencyMs = sw.ElapsedMilliseconds,
            Provider = SpeechProviderName.Of(options.Value), Outcome = outcome, RunId = runId, ConversationId = conversationId,
            DurationMs = sw.ElapsedMilliseconds,
        });
        await db.SaveChangesAsync(CancellationToken.None);
        Lots.Shell.Core.Telemetry.LotsMetrics.SpeechRequests.Add(1, new("direction", direction.ToLowerInvariant()), new("outcome", outcome));
        if (outcome == "ok") Lots.Shell.Core.Telemetry.LotsMetrics.SpeechLatency.Record(sw.Elapsed.TotalSeconds, new KeyValuePair<string, object?>("direction", direction.ToLowerInvariant()));
    }
}

public sealed record SpeakRequest(Guid Id, string? Language = null);

/// <summary>
/// Speaks the final answer of a run (ADR 0013): there is no free text-to-speech endpoint, so tool output, errors and
/// approval requests can never reach a speech provider. Owner or admin of the run only.
/// </summary>
public sealed class SpeakRunEndpoint(
    ITextToSpeech tts, IOptions<SpeechOptions> options, LotsDbContext db, ICurrentPrincipal who, IConfiguration config, TimeProvider clock)
    : Endpoint<SpeakRequest>
{
    public override void Configure() => Post("/runs/{Id}/speak");

    public override async Task HandleAsync(SpeakRequest req, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            AddError("Voice is not configured.");
            await Send.ErrorsAsync(503, ct);
            return;
        }

        var me = who.Get(HttpContext);
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == req.Id, ct);
        if (run is null || !RunAccess.CanRead(run, me, config))
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (run.Status != RunStatus.Completed || string.IsNullOrWhiteSpace(run.FinalAnswer))
        {
            AddError("This run has no final answer to read aloud.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        var language = string.IsNullOrWhiteSpace(req.Language) ? LanguageGuess.Of(run.FinalAnswer) : req.Language.Trim().ToLowerInvariant();
        if (!SpeechOptions.Languages.Contains(language))
        {
            AddError("Language must be 'sv' or 'en'.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var text = run.FinalAnswer.Length <= o.MaxTextChars ? run.FinalAnswer : run.FinalAnswer[..o.MaxTextChars];
        var sw = Stopwatch.StartNew();
        // Part of the run's trace, so a turn shows model, tools and speech together.
        using var span = Lots.Shell.Core.Telemetry.Tracing.Source.StartActivity("text_to_speech", ActivityKind.Client,
            run.TraceId is { Length: 32 } tid ? new ActivityContext(ActivityTraceId.CreateFromString(tid), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded) : default);
        span?.SetTag("lots.run.id", run.Id.ToString());
        span?.SetTag("lots.speech.language", language);
        span?.SetTag("lots.speech.characters", text.Length);
        try
        {
            using var audio = await tts.SynthesizeAsync(text, language, UserSettings.VoiceOf(await UserSettings.OfAsync(db, me.UserId, ct)), ct);
            var firstAudioMs = sw.ElapsedMilliseconds; // time to first byte available: the body streams from here
            var usage = new VoiceUsageRecord
            {
                Id = Guid.NewGuid(), At = clock.GetUtcNow(), UserId = me.UserId, Direction = "Tts", Language = language,
                Characters = text.Length, LatencyMs = firstAudioMs, Provider = SpeechProviderName.Of(o), Outcome = "ok",
                RunId = run.Id, ConversationId = run.ConversationId,
            };
            db.VoiceUsage.Add(usage);
            await db.SaveChangesAsync(CancellationToken.None);
            var store = HttpContext.RequestServices.GetRequiredService<IAudioStore>();
            if (audio.Fallback is not null) HttpContext.Response.Headers["X-Voice-Fallback"] = audio.Fallback; // the UI says why the voice sounds different
            var timed = new FirstByteStream(audio.Content, sw, run.ConversationId is not null && store.Enabled ? new MemoryStream() : null);
            await Send.StreamAsync(timed, contentType: audio.ContentType, cancellation: ct);
            if (run.ConversationId is { } conv && timed.Copied is { } spoken)
                await store.SaveAsync(conv, run.Id, run.UserId, "agent", audio.ContentType, spoken, clock.GetUtcNow(), CancellationToken.None);
            usage.LatencyMs = timed.FirstByteMs ?? firstAudioMs; // time to the first audio bytes (the response headers come earlier)
            usage.DurationMs = sw.ElapsedMilliseconds; // the whole synthesis, now that the body has been streamed
            span?.AddEvent(new ActivityEvent("first_audio", DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(usage.DurationMs.Value - usage.LatencyMs)));
            span?.SetTag("lots.speech.first_audio_ms", usage.LatencyMs);
            await db.SaveChangesAsync(CancellationToken.None);
            Lots.Shell.Core.Telemetry.LotsMetrics.SpeechRequests.Add(1, new("direction", "tts"), new("outcome", audio.Fallback is null ? "ok" : "fallback-" + audio.Fallback));
            if (audio.Fallback is not null) span?.SetTag("lots.speech.fallback", audio.Fallback);
            Lots.Shell.Core.Telemetry.LotsMetrics.SpeechLatency.Record(usage.LatencyMs / 1000.0, new KeyValuePair<string, object?>("direction", "tts"));
        }
        catch (SpeechUnavailableException)
        {
            sw.Stop();
            db.VoiceUsage.Add(new VoiceUsageRecord
            {
                Id = Guid.NewGuid(), At = clock.GetUtcNow(), UserId = me.UserId, Direction = "Tts", Language = language,
                Characters = text.Length, LatencyMs = sw.ElapsedMilliseconds, Provider = SpeechProviderName.Of(o), Outcome = "error", RunId = run.Id, ConversationId = run.ConversationId,
            });
            await db.SaveChangesAsync(CancellationToken.None);
            Lots.Shell.Core.Telemetry.LotsMetrics.SpeechRequests.Add(1, new("direction", "tts"), new("outcome", "error"));
            AddError("Voice is unavailable right now.");
            await Send.ErrorsAsync(503, ct);
        }
    }
}

/// <summary>Passes a stream through and notes when the first bytes arrived (the user hears audio from that moment).</summary>
internal sealed class FirstByteStream(Stream inner, Stopwatch clock, MemoryStream? copy = null) : Stream
{
    private const int MaxCopy = 20 * 1024 * 1024;
    public long? FirstByteMs { get; private set; }

    /// <summary>What was streamed, when a copy was asked for (conversation audio, ADR 0014); null if it grew too large.</summary>
    public byte[]? Copied => copy is { Length: > 0 and <= MaxCopy } ? copy.ToArray() : null;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await inner.ReadAsync(buffer, ct);
        if (n > 0) FirstByteMs ??= clock.ElapsedMilliseconds;
        if (n > 0 && copy is not null && copy.Length <= MaxCopy) copy.Write(buffer.Span[..n]);
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        await ReadAsync(buffer.AsMemory(offset, count), ct);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        if (n > 0) FirstByteMs ??= clock.ElapsedMilliseconds;
        if (n > 0 && copy is not null && copy.Length <= MaxCopy) copy.Write(buffer, offset, n);
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal static class SpeechProviderName
{
    /// <summary>Host of the configured provider: shows in the usage records which service handled a call.</summary>
    public static string Of(SpeechOptions o) =>
        Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var u) ? u.Host : "unconfigured";
}

/// <summary>Dictation vocabulary rules and lookup. A hint for spelling only; it carries no authority and no secrets.</summary>
public static class Vocabulary
{
    public const int MaxWords = 100;
    public const int MaxWordLength = 60;

    /// <summary>Trims, removes control characters and duplicates (case-insensitively), keeps order. Returns an error text or null.</summary>
    public static (List<string> Words, string? Error) Normalize(IEnumerable<string>? input)
    {
        var words = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in input ?? [])
        {
            var w = new string((raw ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim().Trim(',', ';').Trim();
            if (w.Length == 0 || !seen.Add(w)) continue;
            if (w.Length > MaxWordLength) return (words, $"A word is longer than {MaxWordLength} characters.");
            words.Add(w);
        }
        return words.Count > MaxWords ? (words, $"At most {MaxWords} words.") : (words, null);
    }

    public static async Task<List<string>> OfUserAsync(LotsDbContext db, string userId, CancellationToken ct)
    {
        var row = await db.UserVocabulary.AsNoTracking().SingleOrDefaultAsync(v => v.UserId == userId, ct);
        return row is null ? [] : JsonSerializer.Deserialize<List<string>>(row.WordsJson) ?? [];
    }

    /// <summary>The user's words first, then the deployment-wide ones, without duplicates.</summary>
    public static async Task<List<string>> ForUserAsync(LotsDbContext db, string userId, SpeechOptions options, CancellationToken ct)
    {
        var shared = (options.Vocabulary ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Normalize((await OfUserAsync(db, userId, ct)).Concat(shared)).Words;
    }
}

public sealed record VocabularyDto(IReadOnlyList<string> Words, IReadOnlyList<string> Shared);

public sealed record SetVocabularyRequest(List<string>? Words);

public sealed class GetVocabularyEndpoint(LotsDbContext db, ICurrentPrincipal who, IOptions<SpeechOptions> options)
    : EndpointWithoutRequest<VocabularyDto>
{
    public override void Configure() => Get("/voice/vocabulary");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var shared = Vocabulary.Normalize((options.Value.Vocabulary ?? "").Split(',')).Words;
        await Send.OkAsync(new VocabularyDto(await Vocabulary.OfUserAsync(db, me.UserId, ct), shared), ct);
    }
}

/// <summary>Replaces the caller's own vocabulary. Everyone edits only their own list.</summary>
public sealed class SetVocabularyEndpoint(LotsDbContext db, ICurrentPrincipal who, IOptions<SpeechOptions> options, TimeProvider clock)
    : Endpoint<SetVocabularyRequest, VocabularyDto>
{
    public override void Configure() => Put("/voice/vocabulary");

    public override async Task HandleAsync(SetVocabularyRequest req, CancellationToken ct)
    {
        var (words, error) = Vocabulary.Normalize(req.Words);
        if (error is not null)
        {
            AddError(error);
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var me = who.Get(HttpContext);
        var row = await db.UserVocabulary.SingleOrDefaultAsync(v => v.UserId == me.UserId, ct);
        if (row is null) db.UserVocabulary.Add(row = new UserVocabularyRecord { UserId = me.UserId });
        row.WordsJson = JsonSerializer.Serialize(words);
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        var shared = Vocabulary.Normalize((options.Value.Vocabulary ?? "").Split(',')).Words;
        await Send.OkAsync(new VocabularyDto(words, shared), ct);
    }
}

public sealed record AckRequest(string? Language = null);

/// <summary>
/// A short fixed acknowledgement ("Jag kollar.") for conversation mode. Phrases come from configuration and the synthesized
/// audio is cached, so the first word of a reply starts almost instantly and costs nothing after the first use.
/// </summary>
public sealed class AcknowledgementEndpoint(
    ITextToSpeech tts, IOptions<SpeechOptions> options, AcknowledgementCache cache, LotsDbContext db, ICurrentPrincipal who)
    : Endpoint<AckRequest>
{
    public override void Configure() => Get("/voice/ack");

    public override async Task HandleAsync(AckRequest req, CancellationToken ct)
    {
        var o = options.Value;
        var language = string.IsNullOrWhiteSpace(req.Language) ? "sv" : req.Language.Trim().ToLowerInvariant();
        if (!o.Enabled || !SpeechOptions.Languages.Contains(language) || o.Acknowledgements.GetValueOrDefault(language) is not { Length: > 0 } phrases)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var phrase = phrases[Random.Shared.Next(phrases.Length)];
        var voice = UserSettings.VoiceOf(await UserSettings.OfAsync(db, who.Get(HttpContext).UserId, ct)); // same voice as the answer
        try
        {
            var bytes = await cache.GetAsync(tts, language, phrase, voice, ct);
            await Send.BytesAsync(bytes.Audio, contentType: bytes.ContentType, cancellation: ct);
        }
        catch (SpeechUnavailableException)
        {
            await Send.NotFoundAsync(ct); // the client simply skips the acknowledgement
        }
    }
}

/// <summary>Synthesized acknowledgement audio, kept in memory (a handful of short clips).</summary>
public sealed class AcknowledgementCache
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string, string, double, double), (byte[] Audio, string ContentType)> _items = new();

    public async Task<(byte[] Audio, string ContentType)> GetAsync(ITextToSpeech tts, string language, string phrase, SpeechVoice voice, CancellationToken ct)
    {
        var key = (language, phrase, voice.VoiceId ?? "", voice.Expressiveness ?? 0, voice.Pace ?? 0);
        if (_items.TryGetValue(key, out var hit)) return hit;
        using var audio = await tts.SynthesizeAsync(phrase, language, voice, ct);
        using var ms = new MemoryStream();
        await audio.Content.CopyToAsync(ms, ct);
        // A fallback voice (the GPU was busy, #84) is fine once, but is not kept: the next request gets the real voice.
        return audio.Fallback is null ? _items.GetOrAdd(key, (ms.ToArray(), audio.ContentType)) : (ms.ToArray(), audio.ContentType);
    }
}

/// <summary>
/// Synthesizes the configured acknowledgements in the default voice after startup (#37), so the first conversation's
/// "Jag kollar." starts within the latency budget instead of waiting several seconds for synthesis.
/// </summary>
public sealed class AcknowledgementWarmup(IServiceScopeFactory scopes, AcknowledgementCache cache, IOptions<SpeechOptions> options,
    ILogger<AcknowledgementWarmup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var o = options.Value;
        if (!o.Enabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stop);
            using var scope = scopes.CreateScope();
            var tts = scope.ServiceProvider.GetRequiredService<ITextToSpeech>();
            var voice = UserSettings.VoiceOf(null);
            foreach (var (language, phrases) in o.Acknowledgements)
                foreach (var phrase in phrases)
                    await cache.GetAsync(tts, language, phrase, voice, stop);
            logger.LogInformation("Acknowledgements ready");
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            logger.LogInformation("Acknowledgement warm-up skipped: {Error}", ex.Message); // the first request synthesizes instead
        }
        catch (OperationCanceledException) { }
    }
}

public sealed record VoiceStatusDto(bool Enabled, bool GpuLow, long? GpuFreeBytes, long? GpuTotalBytes, bool? ExpressiveLoaded, int ExpressiveWaiting);

/// <summary>Whether the expressive voice is available right now (#84), so the UI can say why answers use the fast voice.</summary>
public sealed class VoiceStatusEndpoint(IOptions<SpeechOptions> options) : EndpointWithoutRequest<VoiceStatusDto>
{
    public override void Configure() => Get("/voice/status");

    public override Task HandleAsync(CancellationToken ct) => Send.OkAsync(new VoiceStatusDto(options.Value.Enabled,
        Lots.Shell.Core.Telemetry.VoiceGpu.Low, Lots.Shell.Core.Telemetry.VoiceGpu.FreeBytes, Lots.Shell.Core.Telemetry.VoiceGpu.TotalBytes,
        Lots.Shell.Core.Telemetry.VoiceGpu.ExpressiveLoaded, Lots.Shell.Core.Telemetry.VoiceGpu.ExpressiveWaiting), ct);
}
