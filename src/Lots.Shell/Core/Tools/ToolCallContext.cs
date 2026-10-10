using Lots.Shell.Core.Policy;

namespace Lots.Shell.Core.Tools;

/// <summary>
/// Who a tool call is made for, set by the <see cref="ToolInvoker"/> around each executed call. Built-in tools that filter by
/// identity (knowledge search, meeting transcripts) read it; it is set only after policy allowed the call.
/// </summary>
public sealed record ToolCallContext(Principal Principal, string Profile)
{
    /// <summary>Set by a tool whose result is more sensitive than the profile declares, e.g. passages from a confidential source (#89).</summary>
    public DataClass? ResultClass { get; set; }

    private static readonly AsyncLocal<ToolCallContext?> Holder = new();

    public static ToolCallContext? Current => Holder.Value;

    public static IDisposable Enter(ToolCallContext context)
    {
        var previous = Holder.Value;
        Holder.Value = context;
        return new Scope(previous);
    }

    private sealed class Scope(ToolCallContext? previous) : IDisposable
    {
        public void Dispose() => Holder.Value = previous;
    }
}
