using Microsoft.Extensions.Options;

namespace Lots.Shell.Core.Config;

public sealed class GitOpsOptions
{
    public const string Section = "GitOps";

    /// <summary>Directory holding the resources (a Git checkout kept current by a git-sync sidecar). Empty = GitOps sync off.</summary>
    public string? Path { get; set; }

    public int IntervalSeconds { get; set; } = 60;

    /// <summary>Delete Git-managed resources that are no longer in the directory.</summary>
    public bool Prune { get; set; } = true;
}

/// <summary>Result of the last sync and the current drift, for the admin UI (GET /admin/v1/gitops).</summary>
public sealed record GitOpsState(
    bool Enabled, string? Path, string? Revision, DateTimeOffset? LastSyncAt, string? LastResult, IReadOnlyList<ApplyResult> LastChanges, string? Error);

/// <summary>
/// Applies the resources in <see cref="GitOpsOptions.Path"/> as <c>gitops</c>-managed every interval (#68): Git is the source of truth,
/// the UI shows those resources read-only, and resources removed from Git are pruned. One replica applying is enough; concurrent
/// applies of the same content are harmless (unchanged resources are not written).
/// </summary>
public sealed class GitOpsSyncWorker(IServiceScopeFactory scopes, IOptions<GitOpsOptions> options, GitOpsStatus status, ILogger<GitOpsSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Path)) return;
        while (!stop.IsCancellationRequested)
        {
            await SyncOnceAsync(stop);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, o.IntervalSeconds)), stop).ContinueWith(_ => { });
        }
    }

    public async Task SyncOnceAsync(CancellationToken ct)
    {
        var o = options.Value;
        try
        {
            using var scope = scopes.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<ConfigService>();
            var (docs, revision, errors) = Read(o.Path!);
            if (errors.Count > 0)
            {
                status.Set(new GitOpsState(true, o.Path, revision, DateTimeOffset.UtcNow, "invalid", [], string.Join("; ", errors)));
                return;
            }
            var outcome = await service.ApplyAsync(docs, "gitops", ManagedBy.GitOps, dryRun: false, prune: o.Prune, ct);
            var changes = outcome.Results.Where(r => r.Action != "unchanged").ToList();
            status.Set(new GitOpsState(true, o.Path, revision, DateTimeOffset.UtcNow, outcome.HasErrors ? "invalid" : changes.Count > 0 ? "applied" : "in sync", changes,
                outcome.HasErrors ? string.Join("; ", outcome.Results.SelectMany(r => r.Errors.Select(e => $"{r.Kind} {r.Name}: {e}"))) : null));
            if (changes.Count > 0) logger.LogInformation("GitOps applied {Count} change(s) at {Revision}", changes.Count, revision);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("GitOps sync failed: {Error}", ex.Message);
            status.Set(new GitOpsState(true, o.Path, null, DateTimeOffset.UtcNow, "failed", [], ex.Message));
        }
    }

    /// <summary>All YAML files under the path as resources, plus the checked-out revision when the path is a Git working tree.</summary>
    public static (List<ResourceDocument> Docs, string? Revision, List<string> Errors) Read(string path)
    {
        var errors = new List<string>();
        if (!Directory.Exists(path)) return ([], null, [$"GitOps path '{path}' does not exist"]);
        var files = Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                        && !System.IO.Path.GetRelativePath(path, f).Split(System.IO.Path.DirectorySeparatorChar, '/').Any(p => p.StartsWith('.')))
            .Order(StringComparer.Ordinal).ToList();
        var yaml = string.Join("---\n", files.Select(f => File.ReadAllText(f).TrimEnd() + "\n"));
        return (ConfigService.Split(yaml, errors), Revision(path), errors);
    }

    /// <summary>HEAD of a Git checkout (git-sync keeps a plain working tree), without running git.</summary>
    private static string? Revision(string path)
    {
        // git-sync v4 publishes each commit as a worktree named after its hash, behind a symlink.
        if (new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.Name is { Length: 40 } hash && hash.All(Uri.IsHexDigit))
            return hash[..12];
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
        {
            var head = System.IO.Path.Combine(dir.FullName, ".git", "HEAD");
            if (!File.Exists(head)) continue;
            var text = File.ReadAllText(head).Trim();
            if (!text.StartsWith("ref: ", StringComparison.Ordinal)) return text[..Math.Min(12, text.Length)];
            var refFile = System.IO.Path.Combine(dir.FullName, ".git", text[5..]);
            return File.Exists(refFile) ? File.ReadAllText(refFile).Trim()[..12] : text[5..];
        }
        return null;
    }
}

public sealed class GitOpsStatus
{
    private volatile GitOpsState _state = new(false, null, null, null, null, [], null);
    public GitOpsState Current => _state;
    public void Set(GitOpsState state) => _state = state;
}
