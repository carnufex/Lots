using Lots.Evals;

namespace Lots.Shell.Tests.Features;

public class EvalScoringTests
{
    private static RunOutcome Outcome(string status = "Completed", string? answer = "lots-shell is running", params string[] tools) =>
        new(status, answer, null, tools, 10, 20);

    [Fact]
    public void Passes_when_tools_and_facts_match()
    {
        var c = new EvalCase("x", "q", ["list_containers"], ["Lots-Shell"]);

        var r = Scoring.Score(c, Outcome(tools: "list_containers"));

        Assert.True(r.Passed);
    }

    [Fact]
    public void Fails_on_missing_tool_missing_fact_or_failed_run()
    {
        var c = new EvalCase("x", "q", ["get_container_logs"], ["healthy"]);

        var r = Scoring.Score(c, Outcome(status: "Failed", tools: "list_containers"));

        Assert.False(r.Passed);
        Assert.Equal(3, r.Failures.Count);
    }

    [Fact]
    public void Report_summarises_and_explains_failures()
    {
        var pass = Scoring.Score(new EvalCase("a", "q"), Outcome());
        var fail = Scoring.Score(new EvalCase("b", "q2", ["t"]), Outcome());

        var report = Scoring.Report([pass, fail]);

        Assert.Contains("1/2 passed", report);
        Assert.Contains("expected tool call not made: t", report);
    }
}
