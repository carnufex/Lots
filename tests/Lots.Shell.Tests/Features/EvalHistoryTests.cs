using Lots.Evals;

namespace Lots.Shell.Tests.Features;

public class EvalHistoryTests
{
    private static readonly EvalDataset Set = new("demo", 2, "homelab", [new("a", "qa"), new("b", "qb"), new("c", "qc")]);

    private static EvalResult Attempt(string id, bool passed, long wall = 100) =>
        new(Set.Cases.Single(c => c.Id == id), passed, passed ? [] : ["wrong"], new RunOutcome("Completed", "x", null, ["t"], 10, 5, wall, 0.01, ["qwen"]));

    private static EvalRunRecord Record(params (string Id, bool Passed)[] cases) =>
        EvalHistory.Build(Set, cases.Select(c => Attempt(c.Id, c.Passed)).ToList(), DateTimeOffset.UnixEpoch, "http://x", 1);

    [Fact]
    public void Legacy_array_is_version_one_named_after_the_file()
    {
        var set = Datasets.Parse("""[{"id":"x","question":"q","profile":"homelab"}]""", "evals/homelab.json");

        Assert.Equal("homelab", set.Name);
        Assert.Equal(1, set.Version);
        Assert.Equal("homelab", set.Cases[0].Profile);
    }

    [Fact]
    public void Dataset_profile_applies_to_cases_without_one()
    {
        var set = Datasets.Parse("""{"dataset":"k","version":3,"profile":"kubernetes","cases":[{"id":"a","question":"q"},{"id":"b","question":"q","profile":"other"}]}""", "x.json");

        Assert.Equal(3, set.Version);
        Assert.Equal("kubernetes", set.Cases[0].Profile);
        Assert.Equal("other", set.Cases[1].Profile);
    }

    [Theory]
    [InlineData("""{"dataset":"d","cases":[]}""")]
    [InlineData("""{"dataset":"d","cases":[{"id":"a","question":"q"},{"id":"a","question":"q2"}]}""")]
    [InlineData("""{"dataset":"d","cases":[{"id":"a","question":""}]}""")]
    [InlineData("""{"dataset":"../etc","cases":[{"id":"a","question":"q"}]}""")]
    public void Broken_datasets_are_rejected(string json)
    {
        Assert.Throws<InvalidDataException>(() => Datasets.Parse(json, "x.json"));
    }

    [Fact]
    public void Repeated_attempts_pass_a_case_by_the_threshold()
    {
        var attempts = new[] { Attempt("a", true), Attempt("a", false), Attempt("b", false), Attempt("b", false), Attempt("c", true), Attempt("c", true) };

        var r = EvalHistory.Build(Set, attempts, DateTimeOffset.UnixEpoch, "http://x", 2, casePass: 0.5);

        Assert.True(r.Cases.Single(c => c.Id == "a").Passed);
        Assert.False(r.Cases.Single(c => c.Id == "b").Passed);
        Assert.Equal(2, r.Summary.Passed);
        Assert.Equal(["qwen"], r.Models);
    }

    [Fact]
    public void Compare_finds_regressions_fixes_and_changed_cases()
    {
        var before = Record(("a", true), ("b", false), ("c", true));
        var now = Record(("a", false), ("b", true), ("c", true));

        var diff = EvalHistory.Compare(before, now);

        Assert.Equal(["a"], diff.Regressions);
        Assert.Equal(["b"], diff.Fixed);
        Assert.Empty(diff.Added);
    }

    [Fact]
    public void Gate_fails_on_low_pass_rate_or_regressions()
    {
        var before = Record(("a", true), ("b", true), ("c", true));
        var now = Record(("a", false), ("b", true), ("c", true));
        var diff = EvalHistory.Compare(before, now);

        Assert.Empty(EvalHistory.Gate(now, diff, minPass: 0.6, maxRegressions: 1));
        Assert.Single(EvalHistory.Gate(now, diff, minPass: 0.6, maxRegressions: 0));
        Assert.Equal(2, EvalHistory.Gate(now, diff, minPass: 0.9, maxRegressions: 0).Count);
        Assert.Empty(EvalHistory.Gate(before, null, minPass: 1.0, maxRegressions: 0));
    }

    [Fact]
    public void History_round_trips_and_trend_lists_runs()
    {
        var root = Path.Combine(Path.GetTempPath(), "lots-eval-" + Guid.NewGuid().ToString("N"));
        try
        {
            EvalHistory.Save(root, Record(("a", true), ("b", true), ("c", true)));
            EvalHistory.Save(root, Record(("a", false), ("b", true), ("c", true)) with { StartedAt = DateTimeOffset.UnixEpoch.AddHours(1), Label = "new model/x" });

            var runs = EvalHistory.Load(root, "demo");

            Assert.Equal(2, runs.Count);
            Assert.Equal("new model/x", runs[1].Label);
            Assert.Contains("| 1 |", EvalHistory.Trend("demo", runs)); // the second run regressed one case
            Assert.Single(Directory.GetFiles(Path.Combine(root, "demo"), "*-new-model-x.json"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Percentile_is_nearest_rank()
    {
        Assert.Equal(0, EvalHistory.Percentile([], 50));
        Assert.Equal(2, EvalHistory.Percentile([3, 1, 2, 4], 50));
        Assert.Equal(4, EvalHistory.Percentile([3, 1, 2, 4], 95));
    }

    [Fact]
    public void Repository_datasets_parse()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "evals");
        foreach (var name in new[] { "homelab", "cmdb", "git", "kubernetes", "monitoring" })
        {
            var set = Datasets.Parse(File.ReadAllText(Path.Combine(dir, name + ".json")), name + ".json");
            Assert.Equal(name, set.Name);
            Assert.All(set.Cases, c => Assert.NotNull(c.Profile));
        }
    }
}
