using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lots.Shell.Core.Models;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Core.Knowledge;

public sealed record ConflictOption(string ChunkId, string SourceId, Guid DocumentId, string Title, string ContentHash);

public sealed record ConflictNotice(Guid Id, IReadOnlyList<int> Options, string Summary);

/// <summary>
/// Detects contradicting passages at retrieval time (#48): when the top passages come from different documents, a model call
/// judges whether they disagree about the question. A conflict is recorded once (by its options) so votes accumulate on it.
/// </summary>
public sealed partial class ConflictDetector(IModelClient model, ModelCatalog? catalog, IServiceScopeFactory scopes, TimeProvider clock, ILogger<ConflictDetector> logger)
{
    public const string JudgeAlias = "judge";
    private const int MaxPassages = 4;

    public async Task<ConflictNotice?> CheckAsync(string question, IReadOnlyList<KnowledgeHit> hits, CancellationToken ct)
    {
        // One passage per document, best first: two chunks of the same page are not a conflict between sources.
        var candidates = hits.Select((h, i) => (Hit: h, K: i + 1)).GroupBy(x => x.Hit.DocumentId).Select(g => g.First()).Take(MaxPassages).ToList();
        if (candidates.Count < 2) return null;

        var prompt = new StringBuilder("Do any of these passages contradict each other about the question (different facts, values, steps or ")
            .Append("versions for the same thing)? Passages that cover different aspects are NOT a contradiction. Reply with JSON only: ")
            .Append("{\"conflict\": true|false, \"options\": [passage numbers that disagree], \"summary\": \"one sentence: what differs\"}.\n\nQUESTION: ")
            .Append(question).Append('\n');
        foreach (var (hit, k) in candidates)
            prompt.Append($"\nPASSAGE {k} ({hit.Title}, updated {hit.UpdatedAt:yyyy-MM-dd}):\n").Append(hit.Text).Append('\n');

        string reply;
        try
        {
            var alias = catalog?.Aliases.ContainsKey(JudgeAlias) == true ? JudgeAlias : null;
            var response = await model.CompleteAsync([new ChatMessage("user", prompt.ToString())], [], new ModelCallOptions(Fast: true, Alias: alias), ct);
            reply = response.Message.Content ?? "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Conflict check failed: {Error}", ex.Message);
            return null; // a failed check never blocks retrieval
        }

        if (Parse(reply) is not { Conflict: true } verdict) return null;
        var options = verdict.Options.Where(k => candidates.Any(c => c.K == k)).Distinct().Order().ToList();
        if (options.Count < 2) return null;

        var chosen = options.Select(k => candidates.First(c => c.K == k).Hit)
            .Select(h => new ConflictOption(h.ChunkId, h.SourceId, h.DocumentId, h.Title, h.ContentHash)).ToList();
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", chosen.Select(o => o.ChunkId).Order()))))[..40];

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var record = await db.KnowledgeConflicts.SingleOrDefaultAsync(c => c.Fingerprint == fingerprint, ct);
        if (record is null)
        {
            record = new KnowledgeConflictRecord
            {
                Id = Guid.NewGuid(), Fingerprint = fingerprint, Question = Cap(question, 500), Summary = Cap(verdict.Summary, 500),
                OptionsJson = JsonSerializer.Serialize(chosen), DetectedAt = clock.GetUtcNow(),
            };
            db.KnowledgeConflicts.Add(record);
            await db.SaveChangesAsync(ct);
        }
        return new ConflictNotice(record.Id, options, record.Summary);
    }

    public sealed record Verdict(bool Conflict, List<int> Options, string Summary);

    public static Verdict? Parse(string reply)
    {
        var m = JsonObject().Match(reply);
        if (!m.Success) return null;
        try
        {
            using var doc = JsonDocument.Parse(m.Value);
            var root = doc.RootElement;
            var conflict = root.TryGetProperty("conflict", out var c) && c.ValueKind == JsonValueKind.True;
            var options = root.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
                ? o.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32()).ToList() : [];
            var summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "";
            return new Verdict(conflict, options, summary);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max];

    [GeneratedRegex(@"\{.*\}", RegexOptions.Singleline)] private static partial Regex JsonObject();
}

public sealed record OptionTally(string Option, double Weight, int Votes);

public sealed record ConflictTally(IReadOnlyList<OptionTally> Options, int Votes, int StaleVotes, string? Suggestion, bool Swing);

/// <summary>
/// Vote aggregation (#48). Weighted by role (Knowledge:VoteWeights) and recency (older than 90 days counts half); a vote whose option's
/// document changed since it was cast does not count. A clear majority becomes a suggestion to the source owner, never a decision.
/// </summary>
public static class ConflictVoting
{
    public const string Neither = "neither";
    public const int MaxVotesPerHour = 30;
    public const double MajorityShare = 0.7;
    public const int MinVotesForSuggestion = 3;

    public static ConflictTally Tally(IEnumerable<ConflictVoteRecord> votes, IReadOnlyDictionary<string, string?> currentHashes,
        IReadOnlyDictionary<string, double> roleWeights, DateTimeOffset now)
    {
        var all = votes.ToList();
        var valid = all.Where(v => v.Option == Neither || (currentHashes.TryGetValue(v.Option, out var h) && h is not null && h == v.ContentHash)).ToList();
        double Weight(ConflictVoteRecord v)
        {
            var roles = v.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var w = roles.Select(r => roleWeights.TryGetValue(r, out var x) ? x : 1.0).DefaultIfEmpty(1.0).Max();
            return now - v.At > TimeSpan.FromDays(90) ? w / 2 : w;
        }
        var options = valid.GroupBy(v => v.Option)
            .Select(g => new OptionTally(g.Key, Math.Round(g.Sum(Weight), 2), g.Count())).OrderByDescending(o => o.Weight).ToList();
        var total = options.Sum(o => o.Weight);
        var top = options.FirstOrDefault();
        var suggestion = top is not null && top.Option != Neither && valid.Count >= MinVotesForSuggestion && top.Weight / total >= MajorityShare ? top.Option : null;
        // Many votes in a short time is how poisoning looks: flag it for the owner instead of trusting it.
        var recent = valid.Count(v => now - v.At < TimeSpan.FromHours(24));
        var swing = valid.Count >= 5 && recent > valid.Count / 2;
        return new ConflictTally(options, valid.Count, all.Count - valid.Count, suggestion, swing);
    }
}
