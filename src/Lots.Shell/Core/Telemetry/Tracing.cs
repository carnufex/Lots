using System.Diagnostics;
using Lots.Shell.Persistence;

namespace Lots.Shell.Core.Telemetry;

/// <summary>Spans beyond the agent loop (#76): approval waits, retrieval, speech. Same source as the runner's spans.</summary>
/// <summary>How much of a run's content telemetry carries (#145).</summary>
public enum ContentCapture { Off, Metadata, Redacted, Full }

public static class Tracing
{
    public static ActivitySource Source => Runs.AgentRunner.Telemetry;

    /// <summary>
    /// What telemetry may carry of a run's content (#145, ADR 0019 point 2): the deployment default (<c>Telemetry:Content</c>;
    /// metadata outside Development, redacted in Development). A profile may set its own with <c>telemetry.content</c>.
    /// </summary>
    public static ContentCapture DefaultContent { get; set; } = ContentCapture.Metadata;

    /// <summary><c>full</c> (content without personal-data masking) only where the deployment allows it (<c>Telemetry:AllowFullContent</c>).</summary>
    public static bool AllowFullContent { get; set; }

    /// <summary>The mode a run of this profile uses.</summary>
    public static ContentCapture ContentFor(Profiles.Profile? profile)
    {
        var mode = profile?.TelemetryContent ?? DefaultContent;
        return mode == ContentCapture.Full && !AllowFullContent ? ContentCapture.Redacted : mode;
    }

    /// <summary>A free-text attribute (a policy reason, an error message): left out entirely in <c>off</c> mode.</summary>
    public static void FreeText(Activity? span, string key, string? value, ContentCapture mode)
    {
        if (span is null || value is null || mode == ContentCapture.Off) return;
        span.SetTag(key, Security.SecretRedactor.Redact(value));
    }

    /// <summary>The attributes every span of a run carries (ADR 0019): run, profile and version, user hash, channel, conversation.</summary>
    public static void RunTags(Activity? span, RunRecord run, int? profileVersion)
    {
        if (span is null) return;
        span.SetTag("lots.run.id", run.Id.ToString());
        span.SetTag("lots.profile", run.Profile);
        if (profileVersion is { } v) span.SetTag("lots.profile.version", v);
        span.SetTag("lots.user.hash", UserHash.Of(run.UserId));
        span.SetTag("lots.channel", Channel(run));
        if (run.ConversationId is { } conv) span.SetTag("gen_ai.conversation.id", conv.ToString());
    }

    /// <summary>Where a run came from: voice, slack, email, api, schedule, webhook, delegate (a sub-agent) or web.</summary>
    public static string Channel(RunRecord run)
    {
        if (run.Voice) return "voice";
        if (run.ReplyJson is { } reply && reply.Contains("\"kind\":\"slack\"", StringComparison.Ordinal)) return "slack";
        if (run.ReplyJson is not null) return "email";
        return run.Trigger?.Split(':')[0] switch { null or "" => "web", var t => t };
    }

    private static readonly Security.PiiKind[] AllPii = Enum.GetValues<Security.PiiKind>();

    /// <summary>
    /// A content event on a span. <c>off</c>/<c>metadata</c>: nothing. <c>redacted</c>: secrets and every kind of personal data masked.
    /// <c>full</c>: secrets masked, the rest as is.
    /// </summary>
    public static void Content(Activity? span, string name, string role, string? text, ContentCapture mode)
    {
        if (span is null || text is null || mode is ContentCapture.Off or ContentCapture.Metadata) return;
        var clean = Security.SecretRedactor.Redact(text);
        if (mode == ContentCapture.Redacted) clean = Security.PiiRedactor.Redact(clean, AllPii);
        span.AddEvent(new ActivityEvent(name, tags: new ActivityTagsCollection
        {
            ["gen_ai.message.role"] = role,
            ["gen_ai.message.content"] = clean.Length <= 4000 ? clean : clean[..4000] + "…",
        }));
    }

    public static void Error(Activity? span, Exception ex)
    {
        if (span is null) return;
        span.SetStatus(ActivityStatusCode.Error, Security.SecretRedactor.Redact(ex.Message));
        span.SetTag("error.type", ex.GetType().Name);
    }

    /// <summary>
    /// The time a run waited for a human, as a span in the run's trace from request to decision (recorded when it ends, with its
    /// real start time), so a slow turn shows whether it was the model, a tool or an approver.
    /// </summary>
    public static void ApprovalWait(RunRecord run, ApprovalRecord approval, string outcome, DateTimeOffset decidedAt)
    {
        var parent = run.TraceId is { Length: 32 } t
            ? new ActivityContext(ActivityTraceId.CreateFromString(t), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded)
            : default;
        using var span = Source.StartActivity("approval_wait", ActivityKind.Internal, parent, startTime: approval.RequestedAt);
        if (span is null) return;
        span.SetTag("gen_ai.tool.name", approval.ToolName);
        span.SetTag("lots.run.id", run.Id.ToString());
        span.SetTag("lots.approval.outcome", outcome);
        span.SetTag("lots.approval.risk", approval.Risk);
        span.SetTag("lots.approval.required", approval.RequiredApprovals);
        span.SetEndTime(decidedAt.UtcDateTime);
    }
}
