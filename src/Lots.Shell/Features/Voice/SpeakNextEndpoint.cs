using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Runs;
using Lots.Shell.Core.Speech;
using Lots.Shell.Features.Runs;
using Lots.Shell.Features.Settings;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Features.Voice;

/// <param name="From">Characters of the answer already spoken.</param>
/// <param name="Hash">The hash the previous call returned for those characters; a mismatch means the answer started over.</param>
public sealed record SpeakNextRequest(Guid Id, int From = 0, string? Hash = null, string? Language = null);

/// <summary>
/// Speaks a run's answer while it is being written (#37, ADR 0025): the next complete sentences of the run's own answer text after
/// <see cref="SpeakNextRequest.From"/>, so the first sentence plays while the model writes the rest. Like <c>/speak</c> it reads only the
/// run's answer (the streaming text of the answering model call, or the final answer), never tool output or free text (ADR 0013).
/// 200 with audio and <c>X-Spoken-To</c>/<c>X-Spoken-Hash</c>; 204 when no new sentence is complete yet (<c>X-Run-Done</c> says whether
/// to stop asking); <c>X-Speak-Reset</c> when the text no longer continues what was spoken (a new model message).
/// </summary>
public sealed partial class SpeakNextEndpoint(ITextToSpeech tts, IOptions<SpeechOptions> options, LotsDbContext db, ICurrentPrincipal who, IConfiguration config,
    TimeProvider clock, RunStreams streams) : Endpoint<SpeakNextRequest>
{
    public override void Configure() => Post("/runs/{Id}/speak/next");

    public static string HashOf(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    /// <summary>
    /// The next piece to speak from <paramref name="from"/>: whole sentences (at least <paramref name="minChars"/> unless the first),
    /// or everything left once the answer is final. Null end = nothing complete yet.
    /// </summary>
    public static int? NextEnd(string text, int from, bool final, int minChars = 40)
    {
        if (from >= text.Length) return null;
        var ends = SentenceEnd().Matches(text, from).Select(m => m.Index + m.Length).Where(e => e < text.Length).ToList();
        // A final answer is still spoken in sentence-sized pieces (the next one synthesises while this one plays); the tail goes last.
        if (final) return from == 0 && ends.Count > 0 ? ends[0] : ends.FirstOrDefault(e => e - from >= minChars) is > 0 and var e2 ? e2 : text.Length;
        if (ends.Count == 0) return null;
        // Speak the first sentence as soon as it exists (that is the latency win), later ones in chunks of a sensible size.
        if (from == 0) return ends[0];
        var end = ends.FirstOrDefault(e => e - from >= minChars);
        return end > 0 ? end : null;
    }

    [GeneratedRegex(@"[.!?…:;](?=\s)|\n+")]
    private static partial Regex SentenceEnd();

    public override async Task HandleAsync(SpeakNextRequest req, CancellationToken ct)
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

        var final = run.Status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled;
        // The answer as it stands: the final one, or the text the answering model call is streaming (this replica, else the shared copy).
        var text = run.Status == RunStatus.Completed ? run.FinalAnswer ?? "" : final ? "" : streams.Get(run.Id)?.Text ?? run.Partial ?? "";
        if (text.Length > o.MaxTextChars) text = text[..o.MaxTextChars];
        var from = Math.Clamp(req.From, 0, int.MaxValue);
        if (from > 0 && (from > text.Length || (req.Hash is { } h && h != HashOf(text[..from]))))
        {
            // The text does not continue what was spoken: the model started a new message (it wrote, then called a tool).
            HttpContext.Response.Headers["X-Speak-Reset"] = "1";
            from = 0;
        }
        var end = NextEnd(text, from, run.Status == RunStatus.Completed);
        if (end is null || text[from..end.Value].Trim().Length == 0)
        {
            HttpContext.Response.Headers["X-Run-Done"] = final ? "true" : "false";
            HttpContext.Response.Headers["X-Spoken-To"] = (end ?? from).ToString();
            await Send.NoContentAsync(ct);
            return;
        }

        var piece = text[from..end.Value].Trim();
        var language = string.IsNullOrWhiteSpace(req.Language) ? LanguageGuess.Of(text) : req.Language.Trim().ToLowerInvariant();
        if (!SpeechOptions.Languages.Contains(language)) language = "sv";
        var sw = Stopwatch.StartNew();
        try
        {
            using var audio = await tts.SynthesizeAsync(piece, language, UserSettings.VoiceOf(await UserSettings.OfAsync(db, me.UserId, ct)), ct);
            using var buffer = new MemoryStream();
            await audio.Content.CopyToAsync(buffer, ct);
            db.VoiceUsage.Add(new VoiceUsageRecord
            {
                Id = Guid.NewGuid(), At = clock.GetUtcNow(), UserId = me.UserId, Direction = "Tts", Language = language, Characters = piece.Length,
                LatencyMs = sw.ElapsedMilliseconds, DurationMs = sw.ElapsedMilliseconds, Provider = SpeechProviderName.Of(o), Outcome = "ok",
                RunId = run.Id, ConversationId = run.ConversationId,
            });
            await db.SaveChangesAsync(CancellationToken.None);
            var store = HttpContext.RequestServices.GetRequiredService<IAudioStore>();
            if (run.ConversationId is { } conv && store.Enabled)
                await store.SaveAsync(conv, run.Id, run.UserId, "agent", audio.ContentType, buffer.ToArray(), clock.GetUtcNow(), CancellationToken.None);
            HttpContext.Response.Headers["X-Spoken-To"] = end.Value.ToString();
            HttpContext.Response.Headers["X-Spoken-Hash"] = HashOf(text[..end.Value]);
            HttpContext.Response.Headers["X-Run-Done"] = (run.Status == RunStatus.Completed && end.Value >= text.Length).ToString().ToLowerInvariant();
            if (audio.Fallback is not null) HttpContext.Response.Headers["X-Voice-Fallback"] = audio.Fallback;
            Core.Telemetry.LotsMetrics.SpeechRequests.Add(1, new("direction", "tts"), new("outcome", audio.Fallback is null ? "ok" : "fallback-" + audio.Fallback));
            await Send.BytesAsync(buffer.ToArray(), contentType: audio.ContentType, cancellation: ct);
        }
        catch (SpeechUnavailableException)
        {
            Core.Telemetry.LotsMetrics.SpeechRequests.Add(1, new("direction", "tts"), new("outcome", "error"));
            AddError("Voice is unavailable right now.");
            await Send.ErrorsAsync(503, ct);
        }
    }
}
