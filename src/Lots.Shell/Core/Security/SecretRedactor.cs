using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Lots.Shell.Core.Security;

/// <summary>
/// Keeps secrets out of logs, stored traces, tool results and error messages (#87). Two layers: every secret value the shell
/// resolves (secret references, secret-named environment variables, the database password, fetched backend tokens) is registered
/// and replaced wherever it appears; and well-known credential shapes (JWTs, bearer tokens, private keys, cloud and Git tokens,
/// <c>password=…</c> pairs) are masked even when the shell never saw the value.
/// </summary>
public static partial class SecretRedactor
{
    public const string Mask = "[redacted]";

    // Shorter values would match ordinary words; a secret that short is not protected by masking anyway.
    private const int MinLength = 8;
    private const int MaxTransient = 2000;
    private static readonly ConcurrentDictionary<string, byte> Static = new();
    private static readonly ConcurrentDictionary<string, byte> Transient = new();
    private static readonly ConcurrentQueue<string> TransientOrder = new();
    private static volatile string[] _snapshot = [];
    private static int _dirty;

    /// <summary>A long-lived secret (client secret, API key, password): masked for the life of the process.</summary>
    public static void Register(string? value)
    {
        if (value is { Length: >= MinLength } v && !string.IsNullOrWhiteSpace(v) && Static.TryAdd(v.Trim(), 0)) Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>A short-lived token (fetched or exchanged): masked too, but only the most recent ones are kept.</summary>
    public static void RegisterToken(string? value)
    {
        if (value is not { Length: >= MinLength } v || !Transient.TryAdd(v, 0)) return;
        TransientOrder.Enqueue(v);
        while (TransientOrder.Count > MaxTransient && TransientOrder.TryDequeue(out var old)) Transient.TryRemove(old, out _);
        Interlocked.Exchange(ref _dirty, 1);
    }

    private static string[] Known()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 1)
            // Longest first, so a value containing another registered value is masked as a whole.
            _snapshot = Static.Keys.Concat(Transient.Keys).Distinct().OrderByDescending(k => k.Length).ToArray();
        return _snapshot;
    }

    /// <summary>Registers the values of environment variables whose names say they hold a secret.</summary>
    public static void RegisterEnvironment(System.Collections.IDictionary? variables = null)
    {
        variables ??= Environment.GetEnvironmentVariables();
        foreach (System.Collections.DictionaryEntry e in variables)
            if (e.Key is string name && SecretName().IsMatch(name) && !ReferenceName().IsMatch(name) && e.Value is string value)
                Register(value);
    }

    /// <summary>Registers the password of an ADO.NET connection string.</summary>
    public static void RegisterConnectionString(string? connectionString)
    {
        if (connectionString is null) return;
        foreach (var part in connectionString.Split(';'))
            if (part.Split('=', 2) is [var key, var value] && key.Trim().ToLowerInvariant() is "password" or "pwd")
                Register(value.Trim());
    }

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var secret in Known())
            if (text.Contains(secret, StringComparison.Ordinal)) text = text.Replace(secret, Mask, StringComparison.Ordinal);
        text = PrivateKey().Replace(text, Mask);
        text = Jwt().Replace(text, Mask);
        text = Bearer().Replace(text, m => m.Groups[1].Value + Mask);
        text = KnownTokens().Replace(text, Mask);
        text = Assignment().Replace(text, m => m.Groups[1].Value + Mask);
        return text;
    }

    public static string? RedactOrNull(string? text) => text is null ? null : Redact(text);

    /// <summary>True when the text contains something shaped like a credential (patterns only, not registered values).</summary>
    public static bool LooksLikeSecret(string text, out int line)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (PrivateKey().IsMatch(lines[i]) || lines[i].Contains("-----BEGIN", StringComparison.Ordinal) || Jwt().IsMatch(lines[i])
                || Bearer().IsMatch(lines[i]) || KnownTokens().IsMatch(lines[i]) || Assignment().IsMatch(lines[i]))
            {
                line = i + 1;
                return true;
            }
        line = 0;
        return false;
    }

    [GeneratedRegex(@"(?i)(key|token|secret|password|passwd|credential)")]
    private static partial Regex SecretName();

    // Speech__ApiKeyEnv, DataProtection__KeysPath, ...: these hold where a secret is, not the secret.
    [GeneratedRegex(@"(?i)(env|ref|path|file|id|name|url|uri|claim|roles?)$")]
    private static partial Regex ReferenceName();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?i)(\b(?:bearer|basic)\s+)[A-Za-z0-9._~+/=-]{16,}")]
    private static partial Regex Bearer();

    // GitHub, GitLab, Slack, AWS access key ids, OpenAI-style and Bitwarden machine-account tokens.
    [GeneratedRegex(@"\b(gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|glpat-[A-Za-z0-9_-]{20,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|sk-[A-Za-z0-9_-]{20,}|0\.[0-9a-f]{8}-[0-9a-f-]{27}\.[A-Za-z0-9+/=:.]{20,})")]
    private static partial Regex KnownTokens();

    // password=..., "client_secret": "...", api-key: ...  (the name stays, the value goes)
    [GeneratedRegex(@"(?i)((?<![a-z])(?:password|passwd|pwd|secret|client_secret|api[_-]?key|access[_-]?token|refresh[_-]?token)(?![a-z])[""']?\s*[:=]\s*[""']?)[^\s""',;&]{6,}")]
    private static partial Regex Assignment();
}
