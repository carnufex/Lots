using Lots.Shell.Core.Mcp;
using Microsoft.AspNetCore.DataProtection;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Runs;
using Lots.Shell.Core.Speech;
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
builder.Services.AddSpeech(builder.Configuration);
builder.Services.AddSingleton<Lots.Shell.Features.Voice.AcknowledgementCache>();
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
// Profiles are config as code: loaded from YAML at startup; an invalid manifest stops the shell.
builder.Services.AddSingleton(sp => ProfileRegistry.LoadDirectory(sp.GetRequiredService<IConfiguration>()["Profiles:Path"] ?? "profiles"));
builder.Services.AddSingleton(sp => new TokenExchangeClient(new HttpClient(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IToolSource>(sp =>
    new McpToolSource(sp.GetRequiredService<ProfileRegistry>().Servers, sp.GetRequiredService<ILoggerFactory>(),
        sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<TokenExchangeClient>()));

// Encrypts the login tokens kept on delegated runs. Set DataProtection:KeysPath to a persistent volume so tokens
// survive restarts; otherwise keys are ephemeral and an unreadable token simply makes delegated calls fail.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("lots");
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
builder.Services.AddSingleton<SubjectTokenVault>();
builder.Services.AddScoped<ToolInvoker>();
builder.Services.AddScoped<AgentRunner>();
builder.Services.AddScoped<RunLeases>();
builder.Services.AddScoped<RunControl>();
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
app.UseStaticFiles(new StaticFileOptions // the browser app, built into wwwroot
{
    // index.html must be revalidated so a deploy reaches browsers at once; the hashed bundles it points to never change.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
        ctx.Context.Request.Path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "no-cache",
});
app.UseAuthentication();
app.UseAuthorization();
app.UseFastEndpoints();
app.Run();

public partial class Program;
