using System.Diagnostics;
using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Speech;
using Lots.Shell.Features.Runs;
using System.Text.Json;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Features.Voice;

public sealed class TranscribeRequest
{
    public IFormFile? Audio { get; set; }
    public string? Language { get; set; }
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
        var sw = Stopwatch.StartNew();
        try
        {
            await using var stream = req.Audio.OpenReadStream();
            var words = await Vocabulary.ForUserAsync(db, me.UserId, o, ct);
            var transcript = await stt.TranscribeAsync(
                new AudioInput(stream, req.Audio.ContentType ?? "application/octet-stream", req.Audio.FileName ?? "audio"), language, words, ct);
            sw.Stop();
            await RecordAsync(me.UserId, "Stt", transcript.Language ?? language, transcript.DurationSeconds, null, sw, "ok", null);
            await Send.OkAsync(new TranscribeResponse(transcript.Text, transcript.Language, transcript.DurationSeconds), ct);
        }
        catch (SpeechUnavailableException)
        {
            sw.Stop();
            await RecordAsync(me.UserId, "Stt", language, null, null, sw, "error", null);
            AddError("Voice is unavailable right now. Type your question instead.");
            await Send.ErrorsAsync(503, ct);
        }
    }

    private async Task RecordAsync(string user, string direction, string? lang, double? seconds, int? chars, Stopwatch sw, string outcome, Guid? runId)
    {
        db.VoiceUsage.Add(new VoiceUsageRecord
        {
            Id = Guid.NewGuid(), At = clock.GetUtcNow(), UserId = user, Direction = direction, Language = lang,
            AudioSeconds = seconds, Characters = chars, LatencyMs = sw.ElapsedMilliseconds,
            Provider = SpeechProviderName.Of(options.Value), Outcome = outcome, RunId = runId,
        });
        await db.SaveChangesAsync(CancellationToken.None);
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
        try
        {
            using var audio = await tts.SynthesizeAsync(text, language, ct);
            sw.Stop(); // time to first byte available: the body streams from here
            db.VoiceUsage.Add(new VoiceUsageRecord
            {
                Id = Guid.NewGuid(), At = clock.GetUtcNow(), UserId = me.UserId, Direction = "Tts", Language = language,
                Characters = text.Length, LatencyMs = sw.ElapsedMilliseconds, Provider = SpeechProviderName.Of(o), Outcome = "ok", RunId = run.Id,
            });
            await db.SaveChangesAsync(CancellationToken.None);
            await Send.StreamAsync(audio.Content, contentType: audio.ContentType, cancellation: ct);
        }
        catch (SpeechUnavailableException)
        {
            sw.Stop();
            db.VoiceUsage.Add(new VoiceUsageRecord
            {
                Id = Guid.NewGuid(), At = clock.GetUtcNow(), UserId = me.UserId, Direction = "Tts", Language = language,
                Characters = text.Length, LatencyMs = sw.ElapsedMilliseconds, Provider = SpeechProviderName.Of(o), Outcome = "error", RunId = run.Id,
            });
            await db.SaveChangesAsync(CancellationToken.None);
            AddError("Voice is unavailable right now.");
            await Send.ErrorsAsync(503, ct);
        }
    }
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
