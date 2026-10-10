using System.Collections.Concurrent;
using Lots.Shell.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Lots.Shell.Core.Runs;

/// <summary>
/// The text a model is writing right now, per run (#95). The worker publishes deltas; readers on the same replica get them from
/// memory at once, readers on another replica from <c>runs.Partial</c>, which is written at most every 300 ms. Cleared when the
/// model call ends: the finished message is then part of the run as usual.
/// </summary>
public sealed class RunStreams(IServiceScopeFactory scopes, TimeProvider clock, ILogger<RunStreams> logger)
{
    private static readonly TimeSpan FlushEvery = TimeSpan.FromMilliseconds(300);

    private sealed class Entry
    {
        public readonly System.Text.StringBuilder Text = new();
        public int Version;
        public DateTimeOffset FlushedAt;
        public int FlushedVersion;
        public bool Flushing;
        public Task Pending = Task.CompletedTask;
    }

    private readonly ConcurrentDictionary<Guid, Entry> _live = new();

    public void Publish(Guid runId, string delta)
    {
        var e = _live.GetOrAdd(runId, _ => new Entry());
        bool flush;
        lock (e)
        {
            e.Text.Append(delta);
            e.Version++;
            flush = !e.Flushing && clock.GetUtcNow() - e.FlushedAt >= FlushEvery;
            if (flush) e.Flushing = true;
        }
        if (flush) lock (e) e.Pending = FlushAsync(runId, e);
    }

    /// <summary>The partial text and a version that changes with every delta, if this replica is streaming the run.</summary>
    public (string Text, int Version)? Get(Guid runId)
    {
        if (!_live.TryGetValue(runId, out var e)) return null;
        lock (e) return (e.Text.ToString(), e.Version);
    }

    public async Task ClearAsync(Guid runId)
    {
        if (!_live.TryRemove(runId, out var e)) return;
        Task pending;
        lock (e) pending = e.Pending;
        await pending; // a flush still on its way must not land after the clear and leave stale text behind
        bool flushed;
        lock (e) flushed = e.FlushedVersion > 0;
        if (flushed) await WriteAsync(runId, null);
    }

    private async Task FlushAsync(Guid runId, Entry e)
    {
        string text;
        int version;
        lock (e) (text, version) = (e.Text.ToString(), e.Version);
        await WriteAsync(runId, text);
        lock (e)
        {
            e.FlushedAt = clock.GetUtcNow();
            e.FlushedVersion = version;
            e.Flushing = false;
        }
    }

    private async Task WriteAsync(Guid runId, string? text)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
            if (!db.Database.IsRelational()) return; // single-process tests read from memory
            await db.Runs.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Partial, text));
        }
        catch (Exception ex)
        {
            logger.LogDebug("Could not store the partial answer of run {Run}: {Error}", runId, ex.Message); // readers fall back to polling
        }
    }
}
