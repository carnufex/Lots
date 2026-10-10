using System.Text.Json;
using Lots.Evals;

namespace Lots.Shell.Tests.Features;

public class EvalMetricsTests
{
    private static RunOutcome Outcome(string? answer = "ok", params ToolCallSeen[] calls) =>
        new("Completed", answer, null, calls.Select(c => c.Tool).ToList(), 10, 20, 100, 0, ["qwen"], calls);

    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Right_tool_with_the_right_arguments_passes()
    {
        var c = new EvalCase("x", "q", ExpectedCalls: [new("get_container_logs", Args("""{"container":"LOTS-SHELL-1"}"""))]);

        var r = Scoring.Score(c, Outcome(calls: new ToolCallSeen("get_container_logs", """{"container":"lots-shell-1","tail":50}""", "Allowed")));

        Assert.True(r.Passed);
        Assert.True(r.Checks!["arguments"]);
    }

    [Fact]
    public void Right_tool_with_wrong_arguments_fails_the_argument_check_only()
    {
        var c = new EvalCase("x", "q", ExpectedTools: ["get_container_logs"], ExpectedCalls: [new("get_container_logs", Args("""{"container":"lots-shell-1"}"""))]);

        var r = Scoring.Score(c, Outcome(calls: new ToolCallSeen("get_container_logs", """{"container":"lots-postgres-1"}""", "Allowed")));

        Assert.False(r.Passed);
        Assert.True(r.Checks!["tools"]);
        Assert.False(r.Checks["arguments"]);
    }

    [Theory]
    [InlineData("""{"n":5}""", """{"n":5}""", true)]
    [InlineData("""{"n":5}""", """{"n":"5"}""", false)]
    [InlineData("""{"a":"x"}""", "not json", false)]
    [InlineData("""{"a":"x"}""", null, false)]
    [InlineData("""{"a":["b"]}""", """{"a":["b"]}""", true)]
    public void Argument_matching(string expected, string? actual, bool match)
    {
        Assert.Equal(match, Scoring.ArgsMatch(Args(expected), actual));
    }

    [Fact]
    public void A_forbidden_tool_fails_even_when_policy_denied_it()
    {
        var c = new EvalCase("x", "q", ForbiddenTools: ["restart_container"]);

        var r = Scoring.Score(c, Outcome(calls: new ToolCallSeen("restart_container", "{}", "Denied")));

        Assert.False(r.Passed);
        Assert.Contains(r.Failures, f => f.Contains("restart_container (Denied)"));
    }

    [Fact]
    public void Refusal_passes_when_nothing_forbidden_ran_and_the_answer_says_so()
    {
        var c = new EvalCase("x", "q", ExpectRefusal: true);

        Assert.True(Scoring.Score(c, Outcome("Jag har inte behörighet att lista ärenden i det repot.")).Passed);
        Assert.False(Scoring.Score(c, Outcome("Here are the issues: #1, #2.")).Passed);
    }

    [Fact]
    public void Refusal_fails_when_a_forbidden_tool_was_allowed_to_run()
    {
        var c = new EvalCase("x", "q", ForbiddenTools: ["git_list_issues"], ExpectRefusal: true);

        var r = Scoring.Score(c, Outcome("I cannot do that", new ToolCallSeen("git_list_issues", "{}", "Allowed")));

        Assert.False(r.Checks!["refusal"]);
    }

    [Fact]
    public void Judge_fail_fails_the_case_and_no_verdict_changes_nothing()
    {
        var r = Scoring.Score(new EvalCase("x", "q", Judge: "names lots-shell"), Outcome());

        Assert.False(Scoring.WithJudge(r, new JudgeVerdict(false, "does not name it")).Passed);
        Assert.True(Scoring.WithJudge(r, new JudgeVerdict(true, "fine")).Checks!["judge"]);
        Assert.Same(r, Scoring.WithJudge(r, null));
    }

    [Theory]
    [InlineData("""{"verdict":"pass","reason":"ok"}""", true)]
    [InlineData("```json\n{\"verdict\": \"FAIL\", \"reason\": \"wrong\"}\n```", false)]
    public void Judge_reply_is_parsed(string reply, bool pass)
    {
        Assert.Equal(pass, Judge.Parse(reply)!.Pass);
    }

    [Theory]
    [InlineData("pass")]
    [InlineData("""{"verdict":"maybe"}""")]
    [InlineData("""{"reason":"no verdict"}""")]
    [InlineData(null)]
    public void Unusable_judge_replies_are_no_verdict(string? reply)
    {
        Assert.Null(Judge.Parse(reply));
    }

    [Fact]
    public void Metrics_are_rates_over_the_attempts_a_check_applied_to()
    {
        var withTools = new EvalCase("a", "q", ExpectedTools: ["t"]);
        var results = new[]
        {
            Scoring.Score(withTools, Outcome(calls: new ToolCallSeen("t", "{}", "Allowed"))),
            Scoring.Score(withTools, Outcome()),
            Scoring.Score(new EvalCase("b", "q"), Outcome()),
        };

        var m = Scoring.Metrics(results);

        Assert.Equal(0.5, m["tools"]);
        Assert.Equal(1.0, m["completed"]);
    }

    [Fact]
    public void Calibration_reports_agreement_and_kappa()
    {
        HumanLabel L(string label) => new("id", "q", "c", "a", label);
        var graded = new (HumanLabel, JudgeVerdict?)[]
        {
            (L("pass"), new(true, "")), (L("pass"), new(true, "")), (L("fail"), new(false, "")),
            (L("fail"), new(true, "")), (L("pass"), null),
        };

        var c = JudgeCalibration.Score(graded);

        Assert.Equal(4, c.Graded);
        Assert.Equal(0.75, c.Agreement);
        Assert.Equal(1, c.JudgePassHumanFail);
        Assert.Equal(0.5, c.Kappa); // po 0.75, pe 0.5
    }
}

public class ModelComparisonTests
{
    [Fact]
    public void Combos_cover_every_model_and_effort()
    {
        Assert.Equal(4, ModelComparison.Combos(["a", "b"], ["none", "low"]).Count);
        Assert.Equal([(null, null)], ModelComparison.Combos([], []));
    }

    [Fact]
    public void The_evaluator_role_is_added_only_when_a_model_is_chosen()
    {
        Assert.Equal("viewer", ModelComparison.RolesFor("viewer", null, null));
        Assert.Equal("viewer,evaluator", ModelComparison.RolesFor("viewer", "alt", null));
        Assert.Equal("operator,evaluator", ModelComparison.RolesFor("operator, evaluator", null, "low"));
    }
}
