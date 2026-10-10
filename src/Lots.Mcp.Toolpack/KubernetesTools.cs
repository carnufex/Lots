using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Lots.Mcp.Toolpack;

/// <summary>
/// Read-only Kubernetes API access. In a cluster it uses the pod's service account (give it a read-only ClusterRole without
/// secrets); elsewhere <c>Kubernetes:Url</c> (e.g. a kubectl proxy) with an optional token reference. Only GET requests, and
/// Secrets (and ConfigMap data) are never returned.
/// </summary>
public sealed class KubernetesApi
{
    private const string ServiceAccount = "/var/run/secrets/kubernetes.io/serviceaccount";
    private readonly HttpClient _http;

    public KubernetesApi(IConfiguration config, Func<string, string> secret)
    {
        var url = config["Kubernetes:Url"];
        HttpMessageHandler handler = new HttpClientHandler();
        string? token = null;
        if (url is null && File.Exists(Path.Combine(ServiceAccount, "token")))
        {
            url = $"https://{Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST")}:{Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT")}";
            token = File.ReadAllText(Path.Combine(ServiceAccount, "token")).Trim();
            var ca = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(Path.Combine(ServiceAccount, "ca.crt"));
            handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, cert, chain, errors) =>
                {
                    if (errors == System.Net.Security.SslPolicyErrors.None) return true;
                    if (cert is null || chain is null) return false;
                    chain.ChainPolicy.TrustMode = System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(ca);
                    return chain.Build(cert);
                },
            };
        }
        if (config["Kubernetes:TokenRef"] is { Length: > 0 } tokenRef) token = secret(tokenRef);
        _http = new HttpClient(handler) { BaseAddress = new Uri((url ?? throw new InvalidOperationException("Kubernetes:Url is required outside a cluster")).TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(20) };
        if (token is not null) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public KubernetesApi(HttpClient http) => _http = http;

    public async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        using var res = await _http.GetAsync(path, ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Kubernetes API answered {(int)res.StatusCode} for {path}", null, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement.Clone();
    }

    public async Task<string> GetTextAsync(string path, CancellationToken ct)
    {
        using var res = await _http.GetAsync(path, ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"Kubernetes API answered {(int)res.StatusCode}", null, res.StatusCode);
        return await res.Content.ReadAsStringAsync(ct);
    }
}

[McpServerToolType]
public sealed class KubernetesTools(KubernetesApi api)
{
    /// <summary>The kinds the tools may read and where their API lives. Secrets are deliberately absent.</summary>
    public static readonly Dictionary<string, (string Group, string Resource, bool Namespaced)> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pods"] = ("api/v1", "pods", true),
        ["services"] = ("api/v1", "services", true),
        ["events"] = ("api/v1", "events", true),
        ["pvcs"] = ("api/v1", "persistentvolumeclaims", true),
        ["nodes"] = ("api/v1", "nodes", false),
        ["namespaces"] = ("api/v1", "namespaces", false),
        ["deployments"] = ("apis/apps/v1", "deployments", true),
        ["statefulsets"] = ("apis/apps/v1", "statefulsets", true),
        ["daemonsets"] = ("apis/apps/v1", "daemonsets", true),
        ["jobs"] = ("apis/batch/v1", "jobs", true),
        ["cronjobs"] = ("apis/batch/v1", "cronjobs", true),
        ["ingresses"] = ("apis/networking.k8s.io/v1", "ingresses", true),
    };

    public static string PathFor(string kind, string? ns, string? name = null, string? labelSelector = null)
    {
        if (!Kinds.TryGetValue(kind, out var k)) throw new ArgumentException($"kind must be one of {string.Join(", ", Kinds.Keys)}");
        var path = k.Namespaced && !string.IsNullOrEmpty(ns) ? $"{k.Group}/namespaces/{Uri.EscapeDataString(ns)}/{k.Resource}" : $"{k.Group}/{k.Resource}";
        if (name is not null) path += "/" + Uri.EscapeDataString(name);
        if (!string.IsNullOrEmpty(labelSelector)) path += "?labelSelector=" + Uri.EscapeDataString(labelSelector);
        return path;
    }

    [McpServerTool(Name = "k8s_list", ReadOnly = true, Destructive = false),
     Description("Lists Kubernetes objects of one kind (pods, deployments, statefulsets, daemonsets, services, ingresses, jobs, cronjobs, pvcs, nodes, namespaces, events) with their status. Secrets are not available.")]
    public async Task<string> List(
        [Description("Kind, e.g. pods")] string kind,
        [Description("Namespace (empty = all)")] string? @namespace = null,
        [Description("Label selector, e.g. app=web")] string? labelSelector = null,
        CancellationToken ct = default)
    {
        try
        {
            var list = await api.GetAsync(PathFor(kind, @namespace, labelSelector: labelSelector), ct);
            var items = list.GetProperty("items").EnumerateArray().ToList();
            if (items.Count == 0) return $"No {kind}.";
            var sb = new StringBuilder();
            foreach (var item in items.Take(300)) sb.AppendLine(Summarise(kind, item));
            if (items.Count > 300) sb.AppendLine($"[{items.Count - 300} more]");
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) when (ex is ArgumentException or HttpRequestException) { return "Error: " + ex.Message; }
    }

    [McpServerTool(Name = "k8s_describe", ReadOnly = true, Destructive = false),
     Description("Shows one Kubernetes object: status, conditions, containers and its recent events.")]
    public async Task<string> Describe(string kind, string name, [Description("Namespace (for namespaced kinds)")] string? @namespace = null, CancellationToken ct = default)
    {
        try
        {
            var obj = await api.GetAsync(PathFor(kind, @namespace, name), ct);
            var sb = new StringBuilder(Summarise(kind, obj)).Append('\n');
            if (obj.TryGetProperty("status", out var status) && status.TryGetProperty("conditions", out var conditions))
                foreach (var c in conditions.EnumerateArray())
                    sb.AppendLine($"condition {S(c, "type")}={S(c, "status")} {S(c, "reason")} {S(c, "message")}".TrimEnd());
            if (kind.Equals("pods", StringComparison.OrdinalIgnoreCase) && status.ValueKind == JsonValueKind.Object && status.TryGetProperty("containerStatuses", out var cs))
                foreach (var c in cs.EnumerateArray())
                    sb.AppendLine($"container {S(c, "name")} | ready={S(c, "ready")} | restarts={S(c, "restartCount")} | image={S(c, "image")}" +
                                  (c.TryGetProperty("state", out var st) ? $" | state={string.Join(",", st.EnumerateObject().Select(p => p.Name + (p.Value.TryGetProperty("reason", out var r) ? ":" + r : "")))}" : ""));
            if (Kinds[kind].Namespaced && @namespace is not null)
            {
                var events = await api.GetAsync($"api/v1/namespaces/{Uri.EscapeDataString(@namespace)}/events?fieldSelector=involvedObject.name={Uri.EscapeDataString(name)}", ct);
                foreach (var e in events.GetProperty("items").EnumerateArray().TakeLast(15))
                    sb.AppendLine($"event {S(e, "type")} {S(e, "reason")}: {S(e, "message")} (x{S(e, "count")}, last {S(e, "lastTimestamp")})");
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex) when (ex is ArgumentException or HttpRequestException) { return "Error: " + ex.Message; }
    }

    [McpServerTool(Name = "k8s_logs", ReadOnly = true, Destructive = false),
     Description("Returns the last lines of a pod's logs (optionally one container, or the previous crashed instance).")]
    public async Task<string> Logs(string pod, string @namespace, string? container = null, int tail = 100, bool previous = false, CancellationToken ct = default)
    {
        try
        {
            var path = $"api/v1/namespaces/{Uri.EscapeDataString(@namespace)}/pods/{Uri.EscapeDataString(pod)}/log?tailLines={Math.Clamp(tail, 1, 1000)}&timestamps=true"
                       + (container is null ? "" : "&container=" + Uri.EscapeDataString(container)) + (previous ? "&previous=true" : "");
            var text = await api.GetTextAsync(path, ct);
            return string.IsNullOrWhiteSpace(text) ? "(no log output)" : text.Length > 30_000 ? text[^30_000..] : text;
        }
        catch (HttpRequestException ex) { return "Error: " + ex.Message; }
    }

    public static string Summarise(string kind, JsonElement o)
    {
        var meta = o.GetProperty("metadata");
        var name = (meta.TryGetProperty("namespace", out var ns) ? ns.GetString() + "/" : "") + S(meta, "name");
        o.TryGetProperty("status", out var st);
        o.TryGetProperty("spec", out var spec);
        return kind.ToLowerInvariant() switch
        {
            "pods" => $"{name} | phase={S(st, "phase")} | ready={Ready(st)} | restarts={Restarts(st)} | node={S(spec, "nodeName")}",
            "deployments" or "statefulsets" => $"{name} | ready={S(st, "readyReplicas", "0")}/{S(spec, "replicas")} | updated={S(st, "updatedReplicas", "0")}",
            "daemonsets" => $"{name} | ready={S(st, "numberReady")}/{S(st, "desiredNumberScheduled")}",
            "nodes" => $"{name} | ready={Condition(st, "Ready")} | version={(st.ValueKind == JsonValueKind.Object && st.TryGetProperty("nodeInfo", out var ni) ? S(ni, "kubeletVersion") : "")}",
            "events" => $"{S(o.GetProperty("involvedObject"), "kind")}/{S(o.GetProperty("involvedObject"), "name")} | {S(o, "type")} {S(o, "reason")}: {S(o, "message")} | last={S(o, "lastTimestamp")}",
            "jobs" => $"{name} | succeeded={S(st, "succeeded", "0")} | failed={S(st, "failed", "0")} | active={S(st, "active", "0")}",
            "cronjobs" => $"{name} | schedule={S(spec, "schedule")} | lastSchedule={S(st, "lastScheduleTime")}",
            "pvcs" => $"{name} | phase={S(st, "phase")} | capacity={(st.ValueKind == JsonValueKind.Object && st.TryGetProperty("capacity", out var cap) ? S(cap, "storage") : "")}",
            "services" => $"{name} | type={S(spec, "type")} | clusterIP={S(spec, "clusterIP")}",
            "ingresses" => $"{name} | hosts={(spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("rules", out var rules) ? string.Join(",", rules.EnumerateArray().Select(r => S(r, "host"))) : "")}",
            _ => $"{name} | phase={S(st, "phase")}",
        };
    }

    private static string S(JsonElement e, string p, string fallback = "") =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : fallback;

    private static string Ready(JsonElement st) =>
        st.ValueKind == JsonValueKind.Object && st.TryGetProperty("containerStatuses", out var cs)
            ? $"{cs.EnumerateArray().Count(c => c.GetProperty("ready").GetBoolean())}/{cs.GetArrayLength()}" : "0/0";

    private static string Restarts(JsonElement st) =>
        st.ValueKind == JsonValueKind.Object && st.TryGetProperty("containerStatuses", out var cs) ? cs.EnumerateArray().Sum(c => c.GetProperty("restartCount").GetInt32()).ToString() : "0";

    private static string Condition(JsonElement st, string type) =>
        st.ValueKind == JsonValueKind.Object && st.TryGetProperty("conditions", out var cs)
            ? cs.EnumerateArray().Where(c => S(c, "type") == type).Select(c => S(c, "status")).FirstOrDefault() ?? "?" : "?";
}
