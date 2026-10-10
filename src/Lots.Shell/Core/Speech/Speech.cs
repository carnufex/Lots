namespace Lots.Shell.Core.Speech;

/// <summary>Audio handed to a speech-to-text provider.</summary>
public sealed record AudioInput(Stream Content, string ContentType, string FileName);

public sealed record TranscriptSegment(double Start, double End, string Text, string? Speaker = null);

public sealed record Transcript(string Text, string? Language, double? DurationSeconds, IReadOnlyList<TranscriptSegment> Segments);

/// <summary>Synthesized speech. <see cref="Content"/> yields bytes as the provider produces them (streaming).</summary>
public sealed class SpeechAudio(Stream content, string contentType, HttpResponseMessage? owner = null) : IDisposable
{
    public Stream Content { get; } = content;
    public string ContentType { get; } = contentType;
    public void Dispose()
    {
        Content.Dispose();
        owner?.Dispose();
    }
}

/// <summary>The speech provider cannot be reached or refused the request. The caller degrades to text.</summary>
public sealed class SpeechUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Provider-neutral speech to text. Implementations: our own voice service, later hosted APIs.</summary>
public interface ISpeechToText
{
    /// <param name="vocabulary">Words the speaker is likely to say (names, products, jargon): a spelling hint for the provider.</param>
    Task<Transcript> TranscribeAsync(AudioInput audio, string? language, IReadOnlyList<string>? vocabulary, CancellationToken ct);
}

/// <summary>Provider-neutral text to speech.</summary>
public interface ITextToSpeech
{
    Task<SpeechAudio> SynthesizeAsync(string text, string language, CancellationToken ct);
}

public sealed class SpeechOptions
{
    public const string Section = "Speech";

    /// <summary>Base URL of an OpenAI-shaped audio API (our voice service). Empty = voice is not configured.</summary>
    public string BaseUrl { get; set; } = "";
    public string? ApiKeyEnv { get; set; }
    public string SttModel { get; set; } = "whisper";
    public string TtsModel { get; set; } = "piper";
    /// <summary>Voice per language, e.g. sv -> nst, en -> lessac.</summary>
    public Dictionary<string, string> Voices { get; set; } = new() { ["sv"] = "sv-nst", ["en"] = "en-lessac" };
    public int MaxAudioBytes { get; set; } = 15 * 1024 * 1024;
    public int MaxTextChars { get; set; } = 1500;

    /// <summary>
    /// Fixed phrases spoken right after the user stops talking, while the agent is working. A closed list from configuration:
    /// never user input, so it cannot be used as a free text-to-speech channel (ADR 0013).
    /// </summary>
    public Dictionary<string, string[]> Acknowledgements { get; set; } = new()
    {
        ["sv"] = ["Jag kollar.", "Ett ögonblick.", "Okej, jag tittar på det."],
        ["en"] = ["Let me check.", "One moment.", "Okay, looking into it."],
    };
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Language the UI preselects for dictation: sv, en or auto. A Swedish installation sets sv.</summary>
    public string DefaultLanguage { get; set; } = "auto";

    /// <summary>
    /// Words shared by everyone on this deployment (product names, jargon), comma separated, e.g. "Lots, Authentik, Longhorn".
    /// Users add their own on the website. All of it is sent to the speech provider, so it must never contain secrets.
    /// </summary>
    public string? Vocabulary { get; set; }

    /// <summary>Longest vocabulary prompt (characters) sent to the provider; user words come first.</summary>
    public int MaxVocabularyChars { get; set; } = 500;

    public bool Enabled => !string.IsNullOrWhiteSpace(BaseUrl);
    public static readonly IReadOnlyList<string> Languages = ["sv", "en"];
}

/// <summary>Cheap language guess for choosing a voice: Swedish letters or common Swedish function words, else English.</summary>
public static class LanguageGuess
{
    private static readonly HashSet<string> Swedish = new(StringComparer.OrdinalIgnoreCase)
    {
        "och", "att", "det", "är", "inte", "jag", "som", "på", "en", "ett", "för", "med", "av", "den", "har", "du",
        "kan", "vi", "om", "men", "så", "till", "var", "här", "vad", "hur", "ska", "får", "nu", "av",
    };

    private static readonly HashSet<string> English = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "is", "are", "not", "i", "that", "of", "to", "in", "it", "you", "for", "with", "this", "have",
        "can", "what", "how", "was", "were", "will", "your", "there", "they",
    };

    public static string Of(string text)
    {
        if (text.Any(c => c is 'å' or 'ä' or 'ö' or 'Å' or 'Ä' or 'Ö')) return "sv";
        var words = text.Split([' ', '\n', '\t', ',', '.', '!', '?', ';', ':'], StringSplitOptions.RemoveEmptyEntries);
        var sv = words.Count(Swedish.Contains);
        var en = words.Count(English.Contains);
        return sv > en ? "sv" : "en";
    }
}
