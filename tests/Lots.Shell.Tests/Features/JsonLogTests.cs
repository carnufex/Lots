using System.Diagnostics;
using System.Text.Json;
using Lots.Shell.Core.Security;
using Lots.Shell.Core.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Lots.Shell.Tests.Features;

/// <summary>#138: JSON log lines carry trace and run correlation, and a known secret never appears on any log path.</summary>
[Collection("Console")] // redirects Console.Out
public class JsonLogTests
{
    private const string Secret = "s3cr3t-registered-value-0042";

    private static List<JsonElement> Capture(Action<ILogger> write)
    {
        SecretRedactor.Register(Secret);
        var original = Console.Out;
        var sink = new StringWriter();
        Console.SetOut(sink);
        try
        {
            var services = new ServiceCollection().AddLogging(b =>
            {
                b.AddConsole(o => o.FormatterName = LotsJsonFormatter.FormatterName)
                    .AddConsoleFormatter<LotsJsonFormatter, ConsoleFormatterOptions>(o => o.IncludeScopes = true);
                SecurityChecks.AddSecretRedaction(b);
            });
            using (var provider = services.BuildServiceProvider())
                write(provider.GetRequiredService<ILoggerFactory>().CreateLogger("Lots.Test"));
            // disposing the provider flushes the console queue
        }
        finally
        {
            Console.SetOut(original);
        }
        // Other tests may write to the console meanwhile: keep only this test's JSON lines.
        return sink.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("{\"time\"", StringComparison.Ordinal))
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).Where(e => e.GetProperty("category").GetString() == "Lots.Test").ToList();
    }

    [Fact]
    public void A_secret_is_masked_in_message_fields_scopes_and_exceptions()
    {
        var lines = Capture(log =>
        {
            using var scope = log.BeginScope(new Dictionary<string, object?> { ["lots.note"] = "scope has " + Secret });
            log.LogWarning("Calling backend with token {Token}", Secret);
            log.LogError(new InvalidOperationException("failed with " + Secret), "Backend failed");
        });

        var all = string.Join("\n", lines.Select(l => l.GetRawText()));
        Assert.Equal(2, lines.Count);
        Assert.DoesNotContain(Secret, all);
        Assert.Contains("Backend failed", all);
        Assert.Equal("warn", lines[0].GetProperty("level").GetString());
        Assert.True(lines[0].TryGetProperty("token", out _)); // the field is there, masked
        Assert.True(lines[1].TryGetProperty("exception", out _));
    }

    [Fact]
    public void Lines_carry_trace_and_run_fields()
    {
        using var source = new ActivitySource("lots-test-logs");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "lots-test-logs", Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        var runId = Guid.NewGuid();
        string? traceId = null;

        var lines = Capture(log =>
        {
            using var activity = source.StartActivity("run");
            traceId = activity!.TraceId.ToHexString();
            using var scope = RunLogScope.Begin(log, runId, "homelab", 5, "alice", null);
            log.LogInformation("Tool {Tool} {Decision}", "list_containers", "Denied");
        });

        var line = Assert.Single(lines);
        Assert.Equal(traceId, line.GetProperty("trace_id").GetString());
        Assert.Equal(runId.ToString(), line.GetProperty("lots.run.id").GetString());
        Assert.Equal(5, line.GetProperty("lots.profile.version").GetInt32());
        Assert.Equal("Denied", line.GetProperty("decision").GetString());
        Assert.NotEqual("alice", line.GetProperty("lots.user.hash").GetString());
        Assert.Equal(UserHash.Of("alice"), line.GetProperty("lots.user.hash").GetString());
        Assert.DoesNotContain("alice", line.GetRawText());
    }
}

[CollectionDefinition("Console", DisableParallelization = true)]
public class ConsoleCollection;
