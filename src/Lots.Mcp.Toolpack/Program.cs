using Lots.Mcp.Toolpack;
using ModelContextProtocol.Server;
using Npgsql;

// Optional tool packs (#63), each off unless configured. The shell still decides per call whether a tool may run: a tool offered here is
// unclassified (not callable) until a profile declares it with a risk class.
var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var mcp = builder.Services.AddMcpServer().WithHttpTransport();
var packs = new List<string>();

string Secret(string reference) =>
    reference.StartsWith("file:", StringComparison.Ordinal) ? File.ReadAllText(reference[5..]).Trim()
    : Environment.GetEnvironmentVariable(reference.Replace("env:", "")) ?? throw new InvalidOperationException($"secret '{reference}' is not set");

if (config.GetSection("Fetch:AllowedHosts").Get<string[]>() is { Length: > 0 } fetchHosts)
{
    builder.Services.AddSingleton(new EgressPolicy(fetchHosts, config.GetValue("Fetch:AllowPrivateNetworks", false), allowedPorts: EgressPolicy.WebPorts));
    mcp.WithTools<FetchTools>();
    packs.Add("fetch");
}

if (config["Files:Root"] is { Length: > 0 } root)
{
    builder.Services.AddSingleton(new FileSandbox(root));
    mcp.WithTools<FileTools>();
    packs.Add("files");
}

if (config["Sql:ConnectionStringRef"] is { Length: > 0 } sqlRef)
{
    // The connection string comes from a secret reference and should use a database role that can only read.
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(Secret(sqlRef)));
    mcp.WithTools<SqlTools>();
    packs.Add("sql");
}

if (config["Kubernetes:Url"] is { Length: > 0 } || config.GetValue("Kubernetes:InCluster", false))
{
    builder.Services.AddSingleton(_ => new KubernetesApi(config, Secret));
    mcp.WithTools<KubernetesTools>();
    packs.Add("kubernetes");
}

if (config["Prometheus:Url"] is { Length: > 0 })
{
    builder.Services.AddHttpClient("prometheus", c => c.Timeout = TimeSpan.FromSeconds(30));
    builder.Services.AddHttpClient("alertmanager", c => c.Timeout = TimeSpan.FromSeconds(15));
    mcp.WithTools<MonitoringTools>();
    packs.Add("monitoring");
}

if (config.GetSection("Git:Repos").Get<string[]>() is { Length: > 0 })
{
    builder.Services.AddHttpClient("git", c => c.Timeout = TimeSpan.FromSeconds(20));
    builder.Services.AddSingleton<Func<string, string>>(Secret);
    mcp.WithTools<GitTools>();
    packs.Add("git");
}

if (config["OpenApi:Spec"] is { Length: > 0 } spec)
{
    var text = spec.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? await new HttpClient().GetStringAsync(spec) : File.ReadAllText(spec);
    var baseUrl = new Uri(config["OpenApi:BaseUrl"] ?? throw new InvalidOperationException("OpenApi:BaseUrl is required with OpenApi:Spec"));
    var egress = new EgressPolicy([baseUrl.IdnHost], config.GetValue("OpenApi:AllowPrivateNetworks", false), allowedPorts: null);
    var authRef = config["OpenApi:AuthHeaderRef"]; // e.g. env:CRM_AUTH ("Bearer ...") or file:/run/secrets/crm
    var include = config.GetSection("OpenApi:Operations").Get<string[]>() ?? [];
    var tools = OpenApiImport.Load(text, config["OpenApi:Prefix"] ?? "")
        .Where(o => include.Length == 0 || include.Contains(o.Name))
        .Select(o => (McpServerTool)new OpenApiTool(o, baseUrl, egress, () => authRef is null ? null : Secret(authRef))).ToList();
    mcp.WithTools(tools);
    packs.Add($"openapi({tools.Count})");
}

var app = builder.Build();
app.Logger.LogInformation("Tool packs enabled: {Packs}", packs.Count == 0 ? "none" : string.Join(", ", packs));
app.MapGet("/health", () => Results.Ok(new { status = "ok", packs }));
app.MapMcp("/mcp");
app.Run();

public partial class Program;
