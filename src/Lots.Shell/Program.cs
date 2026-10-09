using Lots.Shell.Core.Mcp;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Runs;
using Lots.Shell.Core.Tools;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using FastEndpoints;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddLotsAuth();
builder.Services.AddFastEndpoints();
builder.Services.AddModelClient(builder.Configuration);
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
// Profiles are config as code: loaded from YAML at startup; an invalid manifest stops the shell.
builder.Services.AddSingleton(sp => ProfileRegistry.LoadDirectory(sp.GetRequiredService<IConfiguration>()["Profiles:Path"] ?? "profiles"));
builder.Services.AddSingleton<IToolSource>(sp =>
    new McpToolSource(sp.GetRequiredService<ProfileRegistry>().Servers, sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddScoped<ToolInvoker>();
builder.Services.AddScoped<AgentRunner>();
builder.Services.AddScoped<RunLeases>();
if (builder.Configuration.GetValue("Agent:RunWorkerEnabled", true))
    builder.Services.AddHostedService<RunWorker>();
builder.Services.AddDbContext<LotsDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Lots")));

// Traces follow the OpenTelemetry GenAI semantic conventions; exported only when an OTLP endpoint is configured.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("lots-shell"))
    .WithTracing(t =>
    {
        t.AddSource(AgentRunner.Telemetry.Name).AddAspNetCoreInstrumentation();
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            t.AddOtlpExporter();
    });

var app = builder.Build();

_ = app.Services.GetRequiredService<ProfileRegistry>(); // fail fast on invalid profiles
AuthSetup.Validate(app.Configuration); // fail fast unless Auth:Mode is explicit

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<LotsDbContext>().Database.MigrateAsync();
}

if (!AuthSetup.IsOidc(app.Configuration))
    app.Logger.LogWarning("Auth:Mode is Dev: every request is authenticated as the configured dev user. Local development only.");

app.UseDefaultFiles();
app.UseStaticFiles(); // the browser app, built into wwwroot
app.UseAuthentication();
app.UseAuthorization();
app.UseFastEndpoints();
app.Run();

public partial class Program;
