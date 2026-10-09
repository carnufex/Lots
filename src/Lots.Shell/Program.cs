using Lots.Shell.Core.Mcp;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Runs;
using Lots.Shell.Core.Tools;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using FastEndpoints;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFastEndpoints();
builder.Services.AddModelClient(builder.Configuration);
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<McpOptions>(builder.Configuration.GetSection(McpOptions.Section));
builder.Services.AddSingleton<IToolSource, McpToolSource>();
builder.Services.AddScoped<ToolInvoker>();
builder.Services.AddScoped<AgentRunner>();
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

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<LotsDbContext>().Database.MigrateAsync();
}

app.UseFastEndpoints();
app.Run();

public partial class Program;
