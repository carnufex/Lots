using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Core.Speech;

/// <summary>
/// Speech provider for any OpenAI-shaped audio API: <c>POST /v1/audio/transcriptions</c> (multipart) and
/// <c>POST /v1/audio/speech</c>. Our own voice service is the first such provider (ADR 0012).
/// </summary>
public sealed class OpenAiCompatibleSpeech(HttpClient http, IOptions<SpeechOptions> options) : ISpeechToText, ITextToSpeech, IVoiceRegistry
{
    private readonly SpeechOptions _options = options.Value;

    public async Task<Transcript> TranscribeAsync(AudioInput audio, string? language, IReadOnlyList<string>? vocabulary, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(audio.Content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(MediaType(audio.ContentType));
        form.Add(file, "file", audio.FileName);
        form.Add(new StringContent(_options.SttModel), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        if (!string.IsNullOrEmpty(language)) form.Add(new StringContent(language), "language");
        if (PromptOf(vocabulary, _options.MaxVocabularyChars) is { Length: > 0 } prompt) form.Add(new StringContent(prompt), "prompt");

        try
        {
            using var response = await http.PostAsync("audio/transcriptions", form, ct);
            if (!response.IsSuccessStatusCode)
                throw new SpeechUnavailableException($"Speech-to-text provider returned {(int)response.StatusCode}.");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var segments = new List<TranscriptSegment>();
            if (root.TryGetProperty("segments", out var segs))
                foreach (var s in segs.EnumerateArray())
                    segments.Add(new TranscriptSegment(
                        s.TryGetProperty("start", out var a) ? a.GetDouble() : 0,
                        s.TryGetProperty("end", out var b) ? b.GetDouble() : 0,
                        (s.TryGetProperty("text", out var t) ? t.GetString() : null)?.Trim() ?? "",
                        s.TryGetProperty("speaker", out var sp) ? sp.GetString() : null));

            return new Transcript(
                root.GetProperty("text").GetString()?.Trim() ?? "",
                root.TryGetProperty("language", out var l) ? l.GetString() : language,
                root.TryGetProperty("duration", out var d) && d.TryGetDouble(out var dv) ? dv : null,
                segments);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SpeechUnavailableException("Speech-to-text provider is unreachable.", ex);
        }
    }

    /// <summary>The words as one comma separated prompt, whole words only, never longer than <paramref name="maxChars"/>.</summary>
    public static string PromptOf(IReadOnlyList<string>? words, int maxChars)
    {
        if (words is null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var w in words)
        {
            var add = (sb.Length == 0 ? "" : ", ") + w;
            if (sb.Length + add.Length > maxChars) break;
            sb.Append(add);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The bare media type. Browsers record as e.g. <c>audio/webm;codecs=opus</c>, which .NET's strict header parser
    /// rejects; the provider decodes by content, so the parameters are not needed.
    /// </summary>
    public static string MediaType(string contentType)
    {
        var bare = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(bare, @"^[a-z0-9.+-]+/[a-z0-9.+-]+$") ? bare : "application/octet-stream";
    }

    public Task<SpeechAudio> SynthesizeAsync(string text, string language, CancellationToken ct) =>
        SynthesizeAsync(text, language, null, ct);

    public async Task<SpeechAudio> SynthesizeAsync(string text, string language, SpeechVoice? choice, CancellationToken ct)
    {
        var fallback = _options.Voices.GetValueOrDefault(language)
                       ?? throw new SpeechUnavailableException($"No voice configured for '{language}'.");
        try
        {
            return await SendAsync(text, language, choice?.VoiceId ?? fallback, choice, ct);
        }
        catch (UnknownVoiceException) when (choice?.VoiceId is not null)
        {
            // The provider does not know the user's voice (e.g. it only has the fast voices): speak with the default one.
            return await SendAsync(text, language, fallback, choice, ct);
        }
    }

    private async Task<SpeechAudio> SendAsync(string text, string language, string voice, SpeechVoice? choice, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "audio/speech")
        {
            Content = JsonContent.Create(new
            {
                model = _options.TtsModel, input = text, voice, response_format = "wav", language,
                expressiveness = choice?.Expressiveness, pace = choice?.Pace,
            }),
        };

        HttpResponseMessage? response = null;
        try
        {
            // ResponseHeadersRead: the body is streamed to the caller while the provider is still synthesizing.
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && choice?.VoiceId == voice)
                throw new UnknownVoiceException();
            if (!response.IsSuccessStatusCode)
                throw new SpeechUnavailableException($"Text-to-speech provider returned {(int)response.StatusCode}.");
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "audio/wav";
            return new SpeechAudio(await response.Content.ReadAsStreamAsync(ct), contentType, response)
            {
                Fallback = response.Headers.TryGetValues("X-Voice-Fallback", out var why) ? why.FirstOrDefault() : null,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            response?.Dispose();
            throw new SpeechUnavailableException("Text-to-speech provider is unreachable.", ex);
        }
        catch
        {
            response?.Dispose();
            throw;
        }
    }

    private sealed class UnknownVoiceException : Exception;

    public async Task<double> RegisterAsync(string voiceId, AudioInput clip, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(clip.Content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(MediaType(clip.ContentType));
        form.Add(file, "audio", clip.FileName);
        try
        {
            using var response = await http.PutAsync($"voices/{voiceId}", form, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                using var bad = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                throw new VoiceRejectedException(bad.RootElement.TryGetProperty("detail", out var d) ? d.GetString() ?? "The clip was rejected." : "The clip was rejected.");
            }
            if (!response.IsSuccessStatusCode)
                throw new SpeechUnavailableException($"Voice registration returned {(int)response.StatusCode}.");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("seconds", out var sec) ? sec.GetDouble() : 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SpeechUnavailableException("The voice service is unreachable.", ex);
        }
    }

    public async Task<bool?> ExistsAsync(string voiceId, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync($"voices/{voiceId}", ct);
            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.OK => true,
                System.Net.HttpStatusCode.NotFound => false,
                _ => null, // e.g. 405 from a voice service without the endpoint
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SpeechUnavailableException("The voice service is unreachable.", ex);
        }
    }

    public async Task DeleteAsync(string voiceId, CancellationToken ct)
    {
        try
        {
            using var response = await http.DeleteAsync($"voices/{voiceId}", ct);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                throw new SpeechUnavailableException($"Voice deletion returned {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SpeechUnavailableException("The voice service is unreachable.", ex);
        }
    }
}

public static class SpeechRegistration
{
    /// <summary>Registers the provider. Both interfaces share the configured base address, key and timeout.</summary>
    public static IServiceCollection AddSpeech(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SpeechOptions>(config.GetSection(SpeechOptions.Section));

        void Configure(IServiceProvider sp, HttpClient http)
        {
            var o = sp.GetRequiredService<IOptions<SpeechOptions>>().Value;
            if (!o.Enabled) return;
            http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
            var key = string.IsNullOrEmpty(o.ApiKeyEnv) ? null : Environment.GetEnvironmentVariable(o.ApiKeyEnv);
            if (!string.IsNullOrEmpty(key)) http.DefaultRequestHeaders.Authorization = new("Bearer", key);
        }

        services.AddHttpClient<ISpeechToText, OpenAiCompatibleSpeech>(Configure);
        services.AddHttpClient<ITextToSpeech, OpenAiCompatibleSpeech>(Configure);
        services.AddHttpClient<IVoiceRegistry, OpenAiCompatibleSpeech>(Configure);
        return services;
    }
}
