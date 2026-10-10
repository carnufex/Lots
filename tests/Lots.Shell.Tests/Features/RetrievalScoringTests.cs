using Lots.Evals;

namespace Lots.Shell.Tests.Features;

public class RetrievalScoringTests
{
    [Fact]
    public void Rank_is_the_first_passage_from_an_expected_document()
    {
        Assert.Equal(2, RetrievalScoring.RankOf(["Roadmap", "0014. Audio retention", "0014. Audio retention"], ["0014"]));
        Assert.Null(RetrievalScoring.RankOf(["Roadmap"], ["0014"]));
        Assert.Equal(1, RetrievalScoring.RankOf(["Our voice stack: sv/en"], ["voice stack"]));
    }

    [Fact]
    public void Summary_reports_recall_at_k_and_mrr()
    {
        RetrievalResult R(int? rank) => new(new RetrievalCase("c", "q", ["x"]), rank, [], null);

        var s = RetrievalScoring.Summarise([R(1), R(2), R(4), R(null)]);

        Assert.Equal((0.25, 0.5, 0.75), (s.RecallAt1, s.RecallAt3, s.RecallAt5));
        Assert.Equal((1 + 0.5 + 0.25) / 4, s.Mrr, 6);
        Assert.Null(s.CitationPrecision);
    }

    [Fact]
    public void Citations_resolve_through_the_sources_line_and_later_searches_win()
    {
        var map = RetrievalScoring.CitationMap(["Retrieved...\nSources: k1=aa, k2=bb\n[k1] ...", "x\nSources: k1=cc\n"]);

        Assert.Equal("cc", map["k1"]);
        Assert.Equal("bb", map["k2"]);
        Assert.Equal(["k2", "k1"], RetrievalScoring.Cited("See [k2] and [k1], again [k2]."));
    }
}
