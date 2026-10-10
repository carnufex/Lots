using System.Text.Json;
using Lots.Shell.Core.Mcp;
using Lots.Shell.Core.Policy;
using Lots.Shell.Core.Profiles;
using Lots.Shell.Core.Security;
using Lots.Shell.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lots.Shell.Tests.Features;

/// <summary>#87: secrets are referenced, never stored; resolved values and credential shapes are masked everywhere they could leak.</summary>
public class SecretsTests
{
    [Theory]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz012345", "Authorization: Bearer [redacted]")]
    [InlineData("token eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJhbGljZSJ9.c2lnbmF0dXJlLXZhbHVl ok", "token [redacted] ok")]
    [InlineData("DB_PASSWORD=hunter2hunter2 next", "DB_PASSWORD=[redacted] next")]
    [InlineData("{\"client_secret\": \"s3cr3t-value-123\"}", "{\"client_secret\": \"[redacted]\"}")]
    [InlineData("pushed with ghp_0123456789abcdefghijklmnopqrstuvwxyzAB", "pushed with [redacted]")]
    [InlineData("key AKIAIOSFODNN7EXAMPLE used", "key [redacted] used")]
    public void Credential_shapes_are_masked(string input, string expected) => Assert.Equal(expected, SecretRedactor.Redact(input));

    [Fact]
    public void Ordinary_text_is_left_alone()
    {
        const string text = "Backups run nightly; the password policy requires 12 characters. Bearer tokens expire after 5 minutes.";
        Assert.Equal(text, SecretRedactor.Redact(text));
    }

    [Fact]
    public void Resolved_secret_references_are_masked_wherever_they_appear()
    {
        var value = "opaque-" + Guid.NewGuid().ToString("N");
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, value + "\n"); // e.g. a Vault agent sink or a mounted Kubernetes Secret
            Assert.Equal(value, SecretReference.Resolve("file:" + file));
            Assert.Equal($"container env: API={SecretRedactor.Mask}", SecretRedactor.Redact($"container env: API={value}"));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Secret_named_environment_variables_are_registered()
    {
        var value = "env-" + Guid.NewGuid().ToString("N");
        SecretRedactor.RegisterEnvironment(new Dictionary<string, string>
        {
            ["CMDB_AGENT_TOKEN"] = value, ["LOTS_PORT"] = "8088-8088-8088", ["Speech__ApiKeyEnv"] = "LOTS_VOICE_KEY_NAME",
        });
        Assert.Equal("x [redacted] y", SecretRedactor.Redact($"x {value} y"));
        Assert.Equal("port 8088-8088-8088", SecretRedactor.Redact("port 8088-8088-8088"));
        Assert.Equal("set LOTS_VOICE_KEY_NAME", SecretRedactor.Redact("set LOTS_VOICE_KEY_NAME")); // a reference, not a secret
    }

    [Fact]
    public async Task A_tool_that_echoes_a_secret_returns_it_masked_to_trace_and_model()
    {
        var value = "tool-" + Guid.NewGuid().ToString("N");
        SecretRedactor.Register(value);
        var registry = TestProfiles.Registry(("dump_env", ToolRisk.Read));
        var invoker = new ToolInvoker([new EchoTools($"PATH=/bin\nAPI_KEY_VALUE {value}")], registry);
        var result = await invoker.InvokeDetailedAsync(new Lots.Shell.Core.Models.ToolCall("1", "dump_env", "{}"), new Principal("u", ["operator"]), TestProfiles.Name, default);
        Assert.DoesNotContain(value, result.Text);
        Assert.DoesNotContain(value, result.ModelText);
        Assert.Contains(SecretRedactor.Mask, result.Text);
    }

    [Theory]
    [InlineData("    password: hunter2hunter2")]
    [InlineData("    headers: { Authorization: Bearer abcdefghijklmnopqrstuvwxyz }")]
    [InlineData("    token: eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJhbGljZSJ9.c2lnbmF0dXJlLXZhbHVl")]
    public void A_profile_with_a_literal_secret_is_rejected(string line)
    {
        var yaml = "name: p\nversion: 1\nservers:\n  - name: s\n    url: http://localhost:1/mcp\n" + line + "\ntools: []\nroles: []\n";
        var ex = Assert.Throws<ProfileException>(() => ProfileParser.Parse(yaml, "p.yaml"));
        Assert.Contains(ex.Errors, e => e.Contains("looks like a secret"));
    }

    [Fact]
    public void A_profile_that_references_secrets_is_accepted()
    {
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../../examples/profiles/cmdb.yaml"));
        Assert.False(SecretRedactor.LooksLikeSecret(yaml, out _));
    }

    [Fact]
    public void Dev_auth_and_header_identities_are_refused_outside_development()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Mode"] = "Dev", ["Auth:Dev:AllowHeaders"] = "true",
        }).Build();
        var ex = Assert.Throws<InvalidOperationException>(() => SecurityChecks.Enforce(config, new Env("Production")));
        Assert.Contains("Auth:Mode is Dev", ex.Message);
        Assert.Contains("AllowHeaders", ex.Message);
        Assert.Equal(2, SecurityChecks.Enforce(config, new Env("Development")).Count); // allowed, but reported

        var allowed = new ConfigurationBuilder().AddConfiguration(config)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Security:AllowInsecureSettings"] = "true" }).Build();
        Assert.All(SecurityChecks.Enforce(allowed, new Env("Production")), w => Assert.Contains("allowed by Security:AllowInsecureSettings", w));
    }

    [Fact]
    public void Oidc_over_plain_http_is_refused_outside_development_except_on_loopback()
    {
        IConfiguration Oidc(string authority) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:Mode"] = "Oidc", ["Auth:Oidc:Authority"] = authority,
        }).Build();
        Assert.Throws<InvalidOperationException>(() => SecurityChecks.Enforce(Oidc("http://idp.example/"), new Env("Production")));
        Assert.Empty(SecurityChecks.Enforce(Oidc("https://idp.example/"), new Env("Production")));
        Assert.Empty(SecurityChecks.Enforce(Oidc("http://localhost:9000/"), new Env("Production")));
    }

    [Fact]
    public void Log_messages_and_exceptions_are_redacted_before_they_are_written()
    {
        var value = "log-" + Guid.NewGuid().ToString("N");
        SecretRedactor.Register(value);
        var sink = new Sink();
        var logger = new RedactingLoggerProvider(sink).CreateLogger("t");
        logger.LogWarning(new InvalidOperationException($"token {value} rejected"), "calling backend with {Token}", value);
        var (message, exception) = Assert.Single(sink.Lines);
        Assert.Equal("calling backend with [redacted]", message);
        Assert.DoesNotContain(value, exception);
    }

    private sealed class EchoTools(string output) : IToolSource
    {
        public Task<IReadOnlyList<ToolDescriptor>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ToolDescriptor>>(
            [new ToolDescriptor("dump_env", "", JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone())]);
        public Task<string?> ServerOfAsync(string toolName, CancellationToken ct) => Task.FromResult<string?>("sample");
        public Task<string> CallAsync(string name, string argumentsJson, CancellationToken ct) => Task.FromResult(output);
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "lots";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class Sink : ILoggerProvider, ILogger
    {
        public List<(string Message, string Exception)> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((formatter(state, exception), exception?.ToString() ?? ""));
    }
}
