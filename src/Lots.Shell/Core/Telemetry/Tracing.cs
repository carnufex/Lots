using System.Diagnostics;
using Lots.Shell.Persistence;

namespace Lots.Shell.Core.Telemetry;

/// <summary>Spans beyond the agent loop (#76): approval waits, retrieval, speech. Same source as the runner's spans.</summary>
public static class Tracing
{
    public static ActivitySource Source => Runs.AgentRunner.Telemetry;

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
