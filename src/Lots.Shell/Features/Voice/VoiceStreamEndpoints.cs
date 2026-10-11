using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FastEndpoints;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Speech;
using Lots.Shell.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Features.Voice;

/// <summary>
/// Streaming dictation for the browser (#37, #45). A browser cannot put an Authorization header on a WebSocket, so it first asks for a
/// one-minute ticket bound to the signed-in user, then opens <c>/voice/stream?ticket=</c>. The shell relays audio to the voice service
/// (its key never reaches the browser), adds the user's vocabulary, applies the speech quota and records usage per utterance.
/// </summary>
public sealed class VoiceStreamTickets(IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly ITimeLimitedDataProtector _protector = protection.CreateProtector("lots.voice-stream.v1").ToTimeLimitedDataProtector();

    public string Issue(Principal me) => _protector.Protect(JsonSerializer.Serialize(new { u = me.UserId, r = me.Roles }), clock.GetUtcNow().AddMinutes(1));

    public Principal? Read(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket)) return null;
        try
        {
            using var doc = JsonDocument.Parse(_protector.Unprotect(ticket, out var expires));
            if (expires < clock.GetUtcNow()) return null;
            return new Principal(doc.RootElement.GetProperty("u").GetString()!, doc.RootElement.GetProperty("r").EnumerateArray().Select(x => x.GetString()!).ToList());
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException) { return null; }
    }
}

public sealed record StreamTicketDto(string Ticket, int ExpiresInSeconds);

public sealed class StreamTicketEndpoint(VoiceStreamTickets tickets, ICurrentPrincipal who, IOptions<SpeechOptions> speech) : EndpointWithoutRequest<StreamTicketDto>
{
    public override void Configure() => Post("/voice/stream/ticket");

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!speech.Value.Enabled)
        {
            AddError("Voice is not configured.");
            await Send.ErrorsAsync(503, ct);
            return;
        }
        await Send.OkAsync(new StreamTicketDto(tickets.Issue(who.Get(HttpContext)), 60), ct);
    }
}

public static class VoiceStreamRelay
{
    public static void MapVoiceStream(this WebApplication app) =>
        app.Map("/voice/stream", (Delegate)RelayAsync).AllowAnonymous(); // the ticket is the authentication

    private static async Task RelayAsync(HttpContext http, VoiceStreamTickets tickets, IOptions<SpeechOptions> speech, Core.Quotas.QuotaService quotas,
        IServiceScopeFactory scopes, TimeProvider clock, ILogger<VoiceStreamTickets> logger)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = 400;
            return;
        }
        var me = tickets.Read(http.Request.Query["ticket"]);
        var o = speech.Value;
        if (me is null || !o.Enabled)
        {
            http.Response.StatusCode = me is null ? 401 : 503;
            return;
        }
        try { await quotas.CheckSpeechAsync(me, http.RequestAborted); }
        catch (Core.Quotas.QuotaExceededException ex)
        {
            http.Response.StatusCode = 429;
            await http.Response.WriteAsync(ex.Message);
            return;
        }

        using var browser = await http.WebSockets.AcceptWebSocketAsync();
        using var voice = new ClientWebSocket();
        if (Environment.GetEnvironmentVariable(o.ApiKeyEnv ?? "") is { Length: > 0 } key) voice.Options.SetRequestHeader("Authorization", "Bearer " + key);
        string[] vocabulary;
        using (var scope = scopes.CreateScope())
            vocabulary = [.. await Vocabulary.ForUserAsync(scope.ServiceProvider.GetRequiredService<LotsDbContext>(), me.UserId, o, http.RequestAborted)];
        var language = http.Request.Query["language"].ToString() is "sv" or "en" ? http.Request.Query["language"].ToString() : "auto";
        var silence = int.TryParse(http.Request.Query["silence_ms"], out var s) ? Math.Clamp(s, 200, 2000) : 700;
        var target = new UriBuilder(new Uri(new Uri(o.BaseUrl.TrimEnd('/') + "/"), "audio/transcriptions/stream"))
        {
            Scheme = o.BaseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Query = $"language={language}&silence_ms={silence}" +
                    (OpenAiCompatibleSpeech.PromptOf(vocabulary, o.MaxVocabularyChars) is { Length: > 0 } p ? "&prompt=" + Uri.EscapeDataString(p) : ""),
        }.Uri;
        try { await voice.ConnectAsync(target, http.RequestAborted); }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException)
        {
            await SendAsync(browser, """{"type":"error","message":"Voice is unavailable right now."}""");
            await browser.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "voice unavailable", CancellationToken.None);
            return;
        }

        using var done = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        var up = Pump(browser, voice, done.Token, _ => Task.CompletedTask);
        var down = Pump(voice, browser, done.Token, async text =>
        {
            // One usage row per utterance (metadata only, no text), as for push-to-talk dictation.
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.GetProperty("type").GetString() != "final") return;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
            var seconds = doc.RootElement.TryGetProperty("audio_ms", out var ms) ? ms.GetDouble() / 1000 : 0;
            var latency = doc.RootElement.TryGetProperty("latency_ms", out var l) ? l.GetInt64() : 0;
            db.VoiceUsage.Add(new VoiceUsageRecord
            {
                Id = Guid.NewGuid(), At = clock.GetUtcNow(), UserId = me.UserId, Direction = "Stt", AudioSeconds = seconds, LatencyMs = latency,
                Language = doc.RootElement.TryGetProperty("language", out var lang) ? lang.GetString() : null, Provider = SpeechProviderName.Of(o), Outcome = "ok",
            });
            await db.SaveChangesAsync(CancellationToken.None);
            Core.Telemetry.LotsMetrics.SpeechRequests.Add(1, new("direction", "stt"), new("outcome", "ok"));
            Core.Telemetry.LotsMetrics.SpeechLatency.Record(latency / 1000.0, new KeyValuePair<string, object?>("direction", "stt"));
        });
        await Task.WhenAny(up, down);
        await done.CancelAsync();
        foreach (var socket in new WebSocket[] { browser, voice })
            if (socket.State == WebSocketState.Open)
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
                catch (WebSocketException) { }
        logger.LogDebug("Voice stream for {User} ended", Core.Telemetry.UserHash.Of(me.UserId));
    }

    private static async Task Pump(WebSocket from, WebSocket to, CancellationToken ct, Func<string, Task> onText)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && from.State == WebSocketState.Open && to.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await from.ReceiveAsync(buffer, ct);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, r.Count);
                    if (message.Length > 1024 * 1024) return; // no single frame needs to be this large
                } while (!r.EndOfMessage);
                await to.SendAsync(message.ToArray(), r.MessageType, true, ct);
                if (r.MessageType == WebSocketMessageType.Text) await onText(Encoding.UTF8.GetString(message.ToArray()));
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException) { }
    }

    private static Task SendAsync(WebSocket socket, string json) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
}
