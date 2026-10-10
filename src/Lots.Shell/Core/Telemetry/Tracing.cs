using System.Diagnostics;
using Lots.Shell.Persistence;

namespace Lots.Shell.Core.Telemetry;

/// <summary>Spans beyond the agent loop (#76): approval waits, retrieval, speech. Same source as the runner's spans.</summary>
public static class Tracing
{
    public static ActivitySource Source => Runs.AgentRunner.Telemetry;

    /// <summary>
    /// Whether spans carry content as events (prompts, answers, tool arguments and results), always redacted. Off by default outside
    /// Development (ADR 0019 point 2); <c>Telemetry:CaptureContent</c> turns it on.
    /// </summary>
    public static bool CaptureContent { get; set; }

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

    /// <summary>A content event on a span, redacted, only when content capture is on.</summary>
    public static void Content(Activity? span, string name, string role, string? text)
    {
        if (span is null || !CaptureContent || text is null) return;
        var clean = Security.PiiRedactor.Redact(Security.SecretRedactor.Redact(text), Security.PiiRedactor.LogKinds);
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
