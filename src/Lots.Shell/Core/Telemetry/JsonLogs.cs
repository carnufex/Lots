using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lots.Shell.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace Lots.Shell.Core.Telemetry;

/// <summary>
/// A user id in telemetry (ADR 0019): a keyed hash, never the id in clear. Stable across restarts and replicas when
/// <c>Telemetry:UserHashKey</c> is set; otherwise a random per-process key, so hashes still never reveal the user.
/// </summary>
public static class UserHash
{
    private static byte[] _key = RandomNumberGenerator.GetBytes(32);

    public static void Configure(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key)) _key = SHA256.HashData(Encoding.UTF8.GetBytes(key));
    }

    public static string Of(string userId) => Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(userId)))[..16];
}

/// <summary>The fields every log line of a run carries (ADR 0019), as a logging scope.</summary>
public static class RunLogScope
{
    public static IDisposable? Begin(ILogger logger, Guid runId, string profile, int? profileVersion, string userId, Guid? conversationId) =>
        logger.BeginScope(new Dictionary<string, object?>
        {
            ["lots.run.id"] = runId.ToString(),
            ["lots.profile"] = profile,
            ["lots.profile.version"] = profileVersion,
            ["lots.user.hash"] = UserHash.Of(userId),
            ["lots.conversation.id"] = conversationId?.ToString(),
        });
}

/// <summary>
/// One JSON object per line on stdout (#138): time, level, category, message, trace and span ids, the event's own fields and the
/// scope fields. Everything that can carry text goes through the secret and personal-data redaction first: the formatted message,
/// every field value, scope values and the exception.
/// </summary>
public sealed class LotsJsonFormatter(IOptionsMonitor<ConsoleFormatterOptions> options) : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "lots-json";

    // Log lines are not HTML: keep quotes, '+' and non-ASCII letters readable.
    private static readonly JsonWriterOptions Writer = new() { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public override void Write<TState>(in LogEntry<TState> entry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = entry.Formatter(entry.State, entry.Exception);
        if (message is null && entry.Exception is null) return;
        using var buffer = new MemoryStream();
        var seen = new HashSet<string>(StringComparer.Ordinal); // a field written once: scopes repeat trace/span ids and run fields
        using (var json = new Utf8JsonWriter(buffer, Writer))
        {
            json.WriteStartObject();
            json.WriteString("time", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture));
            json.WriteString("level", Level(entry.LogLevel));
            json.WriteString("category", entry.Category);
            json.WriteString("msg", Clean(message));
            if (entry.EventId.Id != 0) json.WriteNumber("event_id", entry.EventId.Id);
            if (Activity.Current is { } activity)
            {
                json.WriteString("trace_id", activity.TraceId.ToHexString());
                json.WriteString("span_id", activity.SpanId.ToHexString());
                seen.Add("trace_id");
                seen.Add("span_id");
            }
            if (entry.State is IEnumerable<KeyValuePair<string, object?>> fields)
                foreach (var (key, value) in fields)
                    if (key != "{OriginalFormat}") Field(json, seen, key, value);
            if (options.CurrentValue.IncludeScopes && scopeProvider is not null)
                scopeProvider.ForEachScope((scope, w) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                        foreach (var (key, value) in pairs) Field(w, seen, key, value);
                }, json);
            if (entry.Exception is { } ex) json.WriteString("exception", Clean(ex.ToString()));
            json.WriteEndObject();
        }
        textWriter.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static void Field(Utf8JsonWriter json, HashSet<string> seen, string key, object? value)
    {
        var name = key.StartsWith("lots.", StringComparison.Ordinal) ? key : ToSnake(key);
        if (value is null || !seen.Add(name)) return;
        switch (value)
        {
            case null: break;
            case bool b: json.WriteBoolean(name, b); break;
            case int or long or short or byte: json.WriteNumber(name, Convert.ToInt64(value)); break;
            case double or float or decimal: json.WriteNumber(name, Convert.ToDouble(value)); break;
            default: json.WriteString(name, Clean(value.ToString())); break;
        }
    }

    internal static string Clean(string? text) => text is null ? "" : PiiRedactor.Redact(SecretRedactor.Redact(text), PiiRedactor.LogKinds);

    private static string ToSnake(string key)
    {
        var sb = new StringBuilder(key.Length + 4);
        for (var i = 0; i < key.Length; i++)
        {
            if (char.IsUpper(key[i]) && i > 0 && key[i - 1] != '_') sb.Append('_');
            sb.Append(char.ToLowerInvariant(key[i]));
        }
        return sb.ToString();
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace", LogLevel.Debug => "debug", LogLevel.Information => "info", LogLevel.Warning => "warn",
        LogLevel.Error => "error", LogLevel.Critical => "fatal", _ => "none",
    };
}

/// <summary>OTLP log export gets the same redaction as stdout (#138): message, body and every attribute.</summary>
public sealed class RedactingLogProcessor : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord record)
    {
        if (record.FormattedMessage is { } m) record.FormattedMessage = LotsJsonFormatter.Clean(m);
        if (record.Body is { } b) record.Body = LotsJsonFormatter.Clean(b);
        if (record.Attributes is { Count: > 0 } attributes)
            record.Attributes = attributes.Select(kv => kv.Value is string s ? new KeyValuePair<string, object?>(kv.Key, LotsJsonFormatter.Clean(s)) : kv).ToList();
    }
}

public static class JsonLogging
{
    /// <summary>
    /// JSON lines unless <c>Logging:Format</c> says <c>text</c>; the default is text in Development and JSON everywhere else.
    /// Scopes are on, so the run fields reach every line. With an OTLP endpoint logs are exported too, redacted.
    /// </summary>
    public static void AddLotsLogging(this WebApplicationBuilder builder)
    {
        var format = builder.Configuration["Logging:Format"] ?? (builder.Environment.IsDevelopment() ? "text" : "json");
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            builder.Logging.AddConsole(o => o.FormatterName = LotsJsonFormatter.FormatterName)
                .AddConsoleFormatter<LotsJsonFormatter, ConsoleFormatterOptions>(o => o.IncludeScopes = true);
        }
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            builder.Logging.AddOpenTelemetry(o =>
            {
                o.IncludeScopes = true;
                o.IncludeFormattedMessage = true;
                o.AddProcessor(new RedactingLogProcessor()).AddOtlpExporter();
            });
        UserHash.Configure(builder.Configuration["Telemetry:UserHashKey"]);
        Tracing.CaptureContent = builder.Configuration.GetValue("Telemetry:CaptureContent", builder.Environment.IsDevelopment());
    }
}
