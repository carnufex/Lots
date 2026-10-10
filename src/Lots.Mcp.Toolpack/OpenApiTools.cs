using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using YamlDotNet.Serialization;

namespace Lots.Mcp.Toolpack;

/// <summary>One API operation from an OpenAPI 3 document, as an MCP tool.</summary>
public sealed record ApiOperation(string Name, string Method, string Path, string Description, JsonObject InputSchema,
    IReadOnlyList<(string Name, string In)> Parameters, bool HasBody);

/// <summary>
/// OpenAPI-to-tools (#63): every operation with an operationId becomes a tool. GET/HEAD are hinted read-only and DELETE destructive,
/// but hints grant nothing: a tool is callable only when a profile declares it with a risk class (deny by default).
/// </summary>
public static partial class OpenApiImport
{
    public static List<ApiOperation> Load(string specText, string prefix = "")
    {
        var root = (specText.TrimStart().StartsWith('{') ? JsonNode.Parse(specText) : YamlToJson(specText))?.AsObject()
                   ?? throw new InvalidDataException("empty OpenAPI document");
        var ops = new List<ApiOperation>();
        foreach (var (path, item) in root["paths"]?.AsObject() ?? [])
        {
            if (item is not JsonObject pathItem) continue;
            var shared = pathItem["parameters"]?.AsArray() ?? [];
            foreach (var method in new[] { "get", "head", "post", "put", "patch", "delete" })
            {
                if (pathItem[method] is not JsonObject op || op["operationId"]?.GetValue<string>() is not { Length: > 0 } id) continue;
                var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
                var required = new JsonArray();
                var parameters = new List<(string, string)>();
                foreach (var p in shared.Concat(op["parameters"]?.AsArray() ?? []).Select(p => Resolve(root, p)).OfType<JsonObject>())
                {
                    var name = p["name"]?.GetValue<string>();
                    var where = p["in"]?.GetValue<string>();
                    if (name is null || where is not ("path" or "query" or "header")) continue;
                    if (where == "header") continue; // auth and transport headers are the server's business, not the model's
                    parameters.Add((name, where));
                    var ps = (Inline(root, p["schema"], 0) ?? new JsonObject { ["type"] = "string" }).AsObject();
                    if (p["description"] is { } d) ps["description"] = d.GetValue<string>();
                    schema["properties"]![name] = ps;
                    if (where == "path" || IsTrue(p["required"])) required.Add(name);
                }
                var body = op["requestBody"] is { } rb ? Resolve(root, rb)?["content"]?["application/json"]?["schema"] : null;
                if (body is not null)
                {
                    schema["properties"]!["body"] = Inline(root, body, 0);
                    if (IsTrue(Resolve(root, op["requestBody"])?["required"])) required.Add("body");
                }
                if (required.Count > 0) schema["required"] = required;
                var description = (op["summary"]?.GetValue<string>() ?? op["description"]?.GetValue<string>() ?? $"{method.ToUpperInvariant()} {path}").Trim();
                ops.Add(new ApiOperation(ToolName(prefix + id), method.ToUpperInvariant(), path, $"{description} ({method.ToUpperInvariant()} {path})",
                    schema, parameters, body is not null));
            }
        }
        return ops;
    }

    /// <summary>YAML documents converted to JSON carry booleans as strings.</summary>
    private static bool IsTrue(JsonNode? n) => n is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && s == "true");

    public static string ToolName(string s)
    {
        var snake = Snake().Replace(s, "$1_$2").ToLowerInvariant();
        var clean = NonName().Replace(snake, "_").Trim('_');
        return clean.Length <= 64 ? clean : clean[..64];
    }

    private static JsonNode? Resolve(JsonObject root, JsonNode? node)
    {
        for (var i = 0; i < 10 && node?["$ref"]?.GetValue<string>() is { } r && r.StartsWith("#/", StringComparison.Ordinal); i++)
        {
            node = root;
            foreach (var part in r[2..].Split('/')) node = node?[part.Replace("~1", "/").Replace("~0", "~")];
        }
        return node;
    }

    /// <summary>A copy of a schema with local $refs inlined (depth-limited, so recursive schemas end as plain objects).</summary>
    private static JsonNode? Inline(JsonObject root, JsonNode? schema, int depth)
    {
        var resolved = Resolve(root, schema);
        if (resolved is null) return null;
        if (depth > 6) return new JsonObject { ["type"] = "object" };
        switch (resolved)
        {
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (k, v) in o)
                    if (k != "$ref") copy[k] = v is JsonObject or JsonArray ? Inline(root, v, depth + 1) : v?.DeepClone();
                return copy;
            case JsonArray a:
                return new JsonArray(a.Select(x => x is JsonObject or JsonArray ? Inline(root, x, depth + 1) : x?.DeepClone()).ToArray());
            default:
                return resolved.DeepClone();
        }
    }

    private static JsonNode? YamlToJson(string yaml)
    {
        var obj = new DeserializerBuilder().Build().Deserialize<object>(yaml);
        var json = new SerializerBuilder().JsonCompatible().Build().Serialize(obj);
        return JsonNode.Parse(json);
    }

    [GeneratedRegex("([a-z0-9])([A-Z])")] private static partial Regex Snake();
    [GeneratedRegex("[^a-z0-9_]+")] private static partial Regex NonName();
}

/// <summary>Calls one API operation: path parameters substituted (escaped), query parameters appended, JSON body, egress-checked.</summary>
public sealed class OpenApiTool(ApiOperation op, Uri baseUrl, EgressPolicy egress, Func<string?> authHeader) : McpServerTool
{
    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement;

    public override Tool ProtocolTool { get; } = new()
    {
        Name = op.Name,
        Description = op.Description,
        InputSchema = JsonDocument.Parse(op.InputSchema.ToJsonString()).RootElement.Clone(),
        Annotations = new ToolAnnotations
        {
            ReadOnlyHint = op.Method is "GET" or "HEAD",
            DestructiveHint = op.Method == "DELETE",
            OpenWorldHint = true,
        },
    };

    public override IReadOnlyList<object> Metadata { get; } = [];

    public ApiOperation Operation => op;

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken ct = default)
    {
        var args = request.Params?.Arguments ?? new Dictionary<string, JsonElement>();
        try
        {
            var (uri, body) = Build(args);
            egress.CheckUri(uri);
            using var http = new HttpClient(egress.Handler()) { Timeout = TimeSpan.FromSeconds(30) };
            using var msg = new HttpRequestMessage(new HttpMethod(op.Method), uri);
            if (body is not null) msg.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (authHeader() is { } auth) msg.Headers.TryAddWithoutValidation("Authorization", auth);
            using var res = await http.SendAsync(msg, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (text.Length > 20_000) text = text[..20_000] + "\n[truncated]";
            return new CallToolResult
            {
                IsError = !res.IsSuccessStatusCode,
                Content = [new TextContentBlock { Text = $"HTTP {(int)res.StatusCode}\n{text}" }],
            };
        }
        catch (Exception ex) when (ex is EgressDeniedException or ArgumentException or HttpRequestException or TaskCanceledException)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = ex.InnerException is EgressDeniedException e ? e.Message : ex.Message }] };
        }
    }

    public (Uri Uri, string? Body) Build(IDictionary<string, JsonElement> args)
    {
        var path = op.Path;
        var query = new List<string>();
        foreach (var (name, where) in op.Parameters)
        {
            if (!args.TryGetValue(name, out var v) || v.ValueKind == JsonValueKind.Null)
            {
                if (where == "path") throw new ArgumentException($"missing path parameter '{name}'");
                continue;
            }
            var value = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
            if (where == "path") path = path.Replace("{" + name + "}", Uri.EscapeDataString(value)); // escaped: no ../ or ? smuggling
            else query.Add($"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}");
        }
        var basePath = baseUrl.AbsoluteUri.TrimEnd('/');
        var uri = new Uri(basePath + path + (query.Count > 0 ? "?" + string.Join("&", query) : ""));
        if (!uri.AbsoluteUri.StartsWith(basePath, StringComparison.Ordinal)) throw new ArgumentException("the request would leave the API's base URL");
        var body = op.HasBody && args.TryGetValue("body", out var b) ? b.GetRawText() : null;
        return (uri, body);
    }
}
