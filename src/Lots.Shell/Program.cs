using Lots.Shell.Core.Knowledge;
using Lots.Shell.Core.Mcp;
using Microsoft.AspNetCore.DataProtection;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Models;
using Lots.Shell.Core.Runs;
using Lots.Shell.Core.Speech;
using Lots.Shell.Core.Tools;
using FastEndpoints.Swagger;
using OpenTelemetry.Resources;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using FastEndpoints;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddLotsAuth();
builder.Services.AddFastEndpoints();
// The public API contract (#109): an OpenAPI document generated from the endpoints, served as JSON (no UI: the CSP allows no
// third-party scripts). docs/api/openapi.json is the reviewed copy; a test fails when they differ.
builder.Services.SwaggerDocument(o =>
{
    o.ShortSchemaNames = true;
    o.EnableJWTBearerAuth = true;
    o.DocumentSettings = s =>
    {
        s.DocumentName = "v1";
        s.Title = "Lots API";
        s.Version = "1";
        s.Description = "Runs, approvals, audit, knowledge and administration. Authenticate with an OIDC access token or a " +
                        "personal API token (Authorization: Bearer lots_pat_...). See docs/api.md.";
    };
});
builder.Services.AddModelClient(builder.Configuration);
builder.Services.AddSpeech(builder.Configuration);
builder.Services.AddSingleton<Lots.Shell.Features.Voice.AcknowledgementCache>();
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
// Profiles are config as code: loaded from YAML at startup; an invalid manifest stops the shell.
builder.Services.AddSingleton(sp => ProfileRegistry.LoadDirectory(sp.GetRequiredService<IConfiguration>()["Profiles:Path"] ?? "profiles"));
// Egress (#86): every connection the shell opens because configuration says so is checked against Egress:* rules.
builder.Services.Configure<Lots.Shell.Core.Net.EgressOptions>(builder.Configuration.GetSection(Lots.Shell.Core.Net.EgressOptions.Section));
builder.Services.AddSingleton<Lots.Shell.Core.Net.ShellEgress>();
builder.Services.AddSingleton(sp => new TokenExchangeClient(
    new HttpClient(sp.GetRequiredService<Lots.Shell.Core.Net.ShellEgress>().Handler(Lots.Shell.Core.Net.EgressPurpose.Identity)), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<CredentialStatusRegistry>();
builder.Services.AddHttpClient(nameof(UserConnections), h => h.Timeout = TimeSpan.FromSeconds(20))
    .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<Lots.Shell.Core.Net.ShellEgress>().Handler(Lots.Shell.Core.Net.EgressPurpose.Identity));
builder.Services.AddSingleton<UserConnections>();
builder.Services.AddSingleton<IToolSource>(sp =>
    new McpToolSource(() => sp.GetRequiredService<ProfileRegistry>().Servers, sp.GetRequiredService<ILoggerFactory>(),
        sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<TokenExchangeClient>(),
        sp.GetRequiredService<UserConnections>(), sp.GetRequiredService<CredentialStatusRegistry>(), sp.GetRequiredService<Lots.Shell.Core.Net.ShellEgress>()));

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
builder.Services.AddSingleton<RunStreams>(); // streamed answers (#95)
// Channels (#107): Slack, mail to run, the OpenAI-compatible endpoint.
builder.Services.Configure<Lots.Shell.Core.Channels.ChannelOptions>(builder.Configuration.GetSection(Lots.Shell.Core.Channels.ChannelOptions.Section));
builder.Services.AddHttpClient(nameof(Lots.Shell.Core.Channels.SlackClient), h => h.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<Lots.Shell.Core.Net.ShellEgress>().Handler(Lots.Shell.Core.Net.EgressPurpose.Webhook));
builder.Services.AddSingleton<Lots.Shell.Core.Channels.SlackClient>();
builder.Services.Configure<Lots.Shell.Core.Memory.MemoryOptions>(builder.Configuration.GetSection(Lots.Shell.Core.Memory.MemoryOptions.Section));
builder.Services.AddSingleton<IToolSource, Lots.Shell.Core.Memory.MemoryToolSource>(); // remember (#99): only where a profile declares it
builder.Services.Configure<DelegationOptions>(builder.Configuration.GetSection(DelegationOptions.Section));
builder.Services.AddSingleton<IToolSource, DelegateToolSource>(); // delegate (#103): only where a profile declares it and lists delegates
builder.Services.Configure<Lots.Shell.Core.Attachments.AttachmentOptions>(builder.Configuration.GetSection(Lots.Shell.Core.Attachments.AttachmentOptions.Section));
builder.Services.AddKnowledge(builder.Configuration);
builder.Services.AddSingleton<Lots.Shell.Features.Usage.PriceTable>();
builder.Services.AddSingleton<Lots.Shell.Core.Speech.IAudioStore, Lots.Shell.Core.Speech.FileAudioStore>();
builder.Services.AddHostedService<Lots.Shell.Core.Speech.AudioRetentionWorker>();
builder.Services.Configure<Lots.Shell.Features.Privacy.RetentionOptions>(builder.Configuration.GetSection(Lots.Shell.Features.Privacy.RetentionOptions.Section));
builder.Services.AddSingleton<Lots.Shell.Features.Privacy.RetentionWorker>();
builder.Services.AddSingleton<Lots.Shell.Core.Audit.AuditSealer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Lots.Shell.Core.Audit.AuditSealer>()); // hash chain (#81)
builder.Services.Configure<Lots.Shell.Core.Audit.AuditForwardOptions>(builder.Configuration.GetSection(Lots.Shell.Core.Audit.AuditForwardOptions.Section));
builder.Services.AddHttpClient(nameof(Lots.Shell.Core.Audit.AuditForwarder), h => h.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<Lots.Shell.Core.Net.ShellEgress>().Handler(Lots.Shell.Core.Net.EgressPurpose.Webhook));
builder.Services.AddHostedService<Lots.Shell.Core.Audit.AuditForwarder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Lots.Shell.Features.Privacy.RetentionWorker>());
builder.Services.AddHostedService<Lots.Shell.Features.Conversations.ConversationSummaryWorker>();
// Cron schedules (#101): every replica ticks, one claims each occurrence. Schedules:Enabled=false turns firing off.
builder.Services.AddSingleton<Lots.Shell.Core.Schedules.ScheduleWorker>();
if (builder.Configuration.GetValue("Schedules:Enabled", true))
    builder.Services.AddHostedService(sp => sp.GetRequiredService<Lots.Shell.Core.Schedules.ScheduleWorker>());
builder.Services.Configure<Lots.Shell.Core.Quotas.QuotaOptions>(builder.Configuration.GetSection(Lots.Shell.Core.Quotas.QuotaOptions.Section));
builder.Services.AddScoped<Lots.Shell.Core.Quotas.QuotaService>();
builder.Services.AddScoped<Lots.Shell.Core.Config.ConfigService>();
builder.Services.Configure<Lots.Shell.Core.Notifications.NotificationOptions>(builder.Configuration.GetSection(Lots.Shell.Core.Notifications.NotificationOptions.Section));
builder.Services.AddHttpClient(nameof(Lots.Shell.Core.Notifications.NotificationWorker), h => h.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<Lots.Shell.Core.Net.ShellEgress>().Handler(Lots.Shell.Core.Net.EgressPurpose.Webhook));
builder.Services.AddSingleton<Lots.Shell.Core.Notifications.NotificationWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Lots.Shell.Core.Notifications.NotificationWorker>());
builder.Services.AddSingleton<Lots.Shell.Core.Notifications.ApprovalExpiryWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Lots.Shell.Core.Notifications.ApprovalExpiryWorker>());
builder.Services.AddHostedService<Lots.Shell.Core.Config.ConfigSyncWorker>(); // profiles applied through the admin API, on every replica
builder.Services.Configure<Lots.Shell.Core.Config.GitOpsOptions>(builder.Configuration.GetSection(Lots.Shell.Core.Config.GitOpsOptions.Section));
builder.Services.AddSingleton<Lots.Shell.Core.Config.GitOpsStatus>();
builder.Services.AddSingleton<Lots.Shell.Core.Config.GitOpsSyncWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Lots.Shell.Core.Config.GitOpsSyncWorker>()); // GitOps:Path set = Git is the source of truth
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
    })
    .WithMetrics(m =>
    {
        // #75: /metrics for Prometheus; OTLP as well when an endpoint is configured.
        m.AddMeter(Lots.Shell.Core.Telemetry.LotsMetrics.MeterName, Lots.Shell.Core.Telemetry.LotsMetrics.MeterName + ".Dependencies",
            Lots.Shell.Core.Telemetry.LotsMetrics.MeterName + ".Dependencies.Gpu").AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter();
        // Exemplars (#140): a histogram bucket links to a trace that landed in it (Prometheus with exemplar storage, OpenMetrics scrape).
        m.SetExemplarFilter(OpenTelemetry.Metrics.ExemplarFilterType.TraceBased);
        // OTLP metrics too only when asked: a deployment that scrapes /metrics would otherwise count everything twice.
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]) && builder.Configuration.GetValue("Telemetry:OtlpMetrics", false))
            m.AddOtlpExporter();
    });
builder.Services.AddSingleton<Lots.Shell.Core.Telemetry.QueueGauges>();
builder.Services.AddHostedService<Lots.Shell.Core.Telemetry.QueueGaugeWorker>();
builder.Services.AddHttpClient(nameof(Lots.Shell.Core.Telemetry.DependencyMonitor), h => h.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddSingleton<Lots.Shell.Core.Telemetry.DependencyMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Lots.Shell.Core.Telemetry.DependencyMonitor>()); // #82/#83

// Secrets never reach logs, traces or tool results (#87): known values are registered, credential shapes are masked.
Lots.Shell.Core.Security.SecretRedactor.RegisterEnvironment();
Lots.Shell.Core.Security.SecretRedactor.RegisterConnectionString(builder.Configuration.GetConnectionString("Lots"));
Lots.Shell.Core.Telemetry.JsonLogging.AddLotsLogging(builder); // JSON lines with trace correlation (#138), before redaction wraps the providers
Lots.Shell.Core.Security.SecurityChecks.AddSecretRedaction(builder.Logging);
// Personal data in log lines (#90): deployment-wide, e.g. Privacy:RedactLogs:0=email.
Lots.Shell.Core.Security.PiiRedactor.LogKinds = (builder.Configuration.GetSection("Privacy:RedactLogs").Get<string[]>() ?? [])
    .Select(k => Lots.Shell.Core.Security.PiiRedactor.TryParse(k, out var kind) ? kind
        : throw new InvalidOperationException($"Privacy:RedactLogs: unknown kind '{k}' ({Lots.Shell.Core.Security.PiiRedactor.Choices})."))
    .Distinct().ToList();

var app = builder.Build();

// A profile naming a model alias that does not exist is a configuration error: stop instead of silently using another model.
{
    var catalog = app.Services.GetRequiredService<ModelCatalog>();
    var unknown = app.Services.GetRequiredService<ProfileRegistry>().All
        .Where(p => p.Model is { } m && !catalog.Aliases.ContainsKey(m)).Select(p => $"{p.Name} -> {p.Model}").ToList();
    if (unknown.Count > 0)
        throw new InvalidOperationException("Profiles refer to unknown model aliases (Models:Aliases): " + string.Join(", ", unknown));
}

_ = app.Services.GetRequiredService<ProfileRegistry>(); // fail fast on invalid profiles
AuthSetup.Validate(app.Configuration); // fail fast unless Auth:Mode is explicit
// Dev auth and header identities only in Development (#87); elsewhere the shell refuses to start unless explicitly allowed.
foreach (var warning in Lots.Shell.Core.Security.SecurityChecks.Enforce(app.Configuration, app.Environment))
    app.Logger.LogWarning("Insecure setting: {Warning}", warning);

if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<LotsDbContext>().Database.MigrateAsync();
}

if (!AuthSetup.IsOidc(app.Configuration))
    app.Logger.LogWarning("Auth:Mode is Dev: every request is authenticated as the configured dev user. Local development only.");

app.UseMiddleware<Lots.Shell.Core.Security.SecurityHeadersMiddleware>(); // CSP, framing, HSTS (#88)
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions // the browser app, built into wwwroot
{
    // index.html must be revalidated so a deploy reaches browsers at once; the hashed bundles it points to never change.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
        ctx.Context.Request.Path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "no-cache",
});
app.UseAuthentication();
app.UseMiddleware<Lots.Shell.Core.Security.ApiTokenScopeMiddleware>(); // API tokens only reach what their scopes allow (#88)
app.UseAuthorization();
app.UseMiddleware<Lots.Shell.Core.Notifications.UserDirectoryMiddleware>(); // e-mail and roles from the login, for approval routing (#136)
app.UseFastEndpoints();
app.UseOpenApi(c => c.Path = "/openapi/{documentName}.json"); // anonymous: the contract is not a secret
// Metrics carry no user identities; restrict who can reach /metrics with a network policy (the chart's ServiceMonitor scrapes in-cluster).
if (app.Configuration.GetValue("Metrics:Enabled", true))
{
    Lots.Shell.Core.Telemetry.LotsMetrics.RegisterGauges(app.Services.GetRequiredService<Lots.Shell.Core.Telemetry.QueueGauges>());
    app.MapPrometheusScrapingEndpoint("/metrics").AllowAnonymous();
}
app.Run();

public partial class Program;
