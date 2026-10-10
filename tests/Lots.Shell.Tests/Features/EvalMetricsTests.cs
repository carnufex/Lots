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

public class VoiceEvalTests
{
    [Theory]
    [InlineData("Hej, jag heter Christopher.", "hej jag heter christopher", 0.0)]
    [InlineData("Hej, jag heter Christopher.", "Hej, jag heter Christoffer.", 0.25)]
    [InlineData("Vad hände med backupen?", "vad hände", 2.0 / 4)]
    [InlineData("två containrar", "två små containrar", 0.5)]
    [InlineData("", "", 0.0)]
    public void Word_error_rate(string reference, string heard, double wer)
    {
        Assert.Equal(wer, Wer.Of(reference, heard), 3);
    }

    [Fact]
    public void Swedish_letters_survive_normalisation()
    {
        Assert.Equal(["så", "här", "går", "det", "what's"], Wer.Words("Så här går det – what’s!"));
    }

    [Fact]
    public void Turn_timing_comes_from_the_timeline()
    {
        var t = VoiceLatency.FromEvents([("stt", 0, 400, null), ("llm", 500, 1200, null), ("tool", 1700, 60, null), ("llm", 1800, 900, null), ("tts", 2900, 3000, 350)]);

        Assert.Equal(new VoiceTurnTiming(400, 2100, 60, 350, 2900), t);
        Assert.Null(VoiceLatency.FromEvents([("llm", 0, 100, null)]).SpeechToAnswerAudio); // typed turn: no end of speech
    }

    [Fact]
    public void Stages_are_checked_against_the_budget()
    {
        var budget = VoiceLatency.ParseBudget("""{"stt":{"p50":300,"p95":800},"model":{"p50":800}}""");
        var turns = new[] { new VoiceTurnTiming(200, 900, 0, 300, 2000), new VoiceTurnTiming(900, 3000, 0, 400, 5000) };

        var stages = VoiceLatency.Summarise(turns, budget).ToDictionary(s => s.Name);

        Assert.False(stages["stt"].WithinBudget); // p95 900 > 800
        Assert.False(stages["model"].WithinBudget);
        Assert.Equal(0, stages["tools"].Count);
        Assert.True(stages["tools"].WithinBudget); // no data is not a failure of the stage
        Assert.True(stages["firstAudio"].WithinBudget); // no budget given
    }

    [Fact]
    public void Ratings_map_back_to_their_hidden_voice()
    {
        var clips = new[]
        {
            new ListeningClip("C01", "sv-nst", "sv", "a", "Hej", 150, 300, "hej", 0, null),
            new ListeningClip("C02", "cb-default", "sv", "a", "Hej", 900, 2000, "hej", 0, "gpu-low"),
        };
        var ratings = Listening.ParseRatings("code,naturalness,clarity,note\nC01,3,4,\nC02,5,5,\"warm, \"\"human\"\"\"\nC03,9,1,\n");

        var report = Listening.Report(clips, ratings);

        Assert.Equal(2, ratings.Count); // out of range dropped
        Assert.Equal("warm, \"human\"", ratings[1].Note);
        Assert.Contains("| cb-default | sv | 1 | 5.0 | 5.0 |", report);
        Assert.Contains("| sv-nst | sv | 1 | 3.0 | 4.0 |", report);
        Assert.DoesNotContain("cb-default", Listening.Sheet(clips, 1)); // blind
    }

    [Fact]
    public void A_streamed_wav_gets_its_real_sizes()
    {
        var wav = new byte[44 + 100];
        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        System.Text.Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.GetBytes(-1).CopyTo(wav, 40);

        var fixedWav = WavFix.WithSizes(wav);

        Assert.Equal(136, BitConverter.ToInt32(fixedWav, 4));
        Assert.Equal(100, BitConverter.ToInt32(fixedWav, 40));
    }
}
