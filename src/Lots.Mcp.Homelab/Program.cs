using Lots.Mcp.Homelab;

var builder = WebApplication.CreateBuilder(args);
// JSON lines with trace ids outside Development (#138), like the shell; Logging:Format=text switches it off.
if ((builder.Configuration["Logging:Format"] ?? (builder.Environment.IsDevelopment() ? "text" : "json")) == "json")
{
    builder.Logging.ClearProviders().AddJsonConsole(o => { o.IncludeScopes = true; o.UseUtcTimestamp = true; o.TimestampFormat = "O"; });
    builder.Logging.Configure(o => o.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
}

// Docker is reached only through a read-only socket proxy (never the raw socket).
var dockerUrl = builder.Configuration["Docker:Url"] ?? "http://docker-proxy:2375";
builder.Services.AddHttpClient<DockerApi>(c =>
{
    c.BaseAddress = new Uri(dockerUrl.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddMcpServer().WithHttpTransport().WithTools<HomelabTools>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapMcp("/mcp");
app.Run();

