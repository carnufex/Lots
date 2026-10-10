using Lots.Shell.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Core.Speech;

/// <summary>
/// Conversation audio (ADR 0014): both sides of voice turns, encrypted at rest with the Data Protection keys, kept
/// <c>Speech:AudioRetentionDays</c> (30; 0 = not stored) under <c>Speech:AudioPath</c> (a persistent volume). Metadata in
/// <c>conversation_audio</c>. Behind this contract so an object store can replace the directory.
/// </summary>
public interface IAudioStore
{
    bool Enabled { get; }
    Task SaveAsync(Guid conversationId, Guid? runId, string userId, string kind, string contentType, byte[] audio, DateTimeOffset at, CancellationToken ct);
    Task<(ConversationAudioRecord Meta, byte[] Audio)?> ReadAsync(Guid audioId, CancellationToken ct);
    Task<int> DeleteConversationAsync(Guid conversationId, CancellationToken ct);
    Task<int> PurgeAsync(DateTimeOffset olderThan, CancellationToken ct);
}

public sealed class FileAudioStore : IAudioStore
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IDataProtector _protector;
    private readonly string? _root;
    private readonly ILogger<FileAudioStore> _logger;

    public FileAudioStore(IServiceScopeFactory scopes, IDataProtectionProvider protection, IOptions<SpeechOptions> options, ILogger<FileAudioStore> logger)
    {
        _scopes = scopes;
        _logger = logger;
        _protector = protection.CreateProtector("Lots.ConversationAudio");
        if (options.Value.AudioRetentionDays <= 0 || string.IsNullOrWhiteSpace(options.Value.AudioPath)) return;
        try
        {
            Directory.CreateDirectory(options.Value.AudioPath);
            var probe = Path.Combine(options.Value.AudioPath, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            _root = options.Value.AudioPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Conversation audio is not stored: {Path} is not writable ({Error}). Mount a volume there.", options.Value.AudioPath, ex.Message);
        }
    }

    public bool Enabled => _root is not null;

    public async Task SaveAsync(Guid conversationId, Guid? runId, string userId, string kind, string contentType, byte[] audio, DateTimeOffset at, CancellationToken ct)
    {
        if (_root is null || audio.Length == 0) return;
        var id = Guid.NewGuid();
        var dir = Path.Combine(_root, conversationId.ToString("N"));
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, id.ToString("N")), _protector.Protect(audio), ct);
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        db.ConversationAudio.Add(new ConversationAudioRecord
        {
            Id = id, ConversationId = conversationId, RunId = runId, UserId = userId, Kind = kind, ContentType = contentType, Bytes = audio.Length, CreatedAt = at,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<(ConversationAudioRecord Meta, byte[] Audio)?> ReadAsync(Guid audioId, CancellationToken ct)
    {
        if (_root is null) return null;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var meta = await db.ConversationAudio.AsNoTracking().SingleOrDefaultAsync(a => a.Id == audioId, ct);
        if (meta is null) return null;
        var file = Path.Combine(_root, meta.ConversationId.ToString("N"), meta.Id.ToString("N"));
        if (!File.Exists(file)) return null;
        try { return (meta, _protector.Unprotect(await File.ReadAllBytesAsync(file, ct))); }
        catch (System.Security.Cryptography.CryptographicException)
        {
            _logger.LogWarning("Audio {Id} cannot be decrypted (Data Protection keys changed)", audioId);
            return null;
        }
    }

    public async Task<int> DeleteConversationAsync(Guid conversationId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var rows = await db.ConversationAudio.Where(a => a.ConversationId == conversationId).ToListAsync(ct);
        db.ConversationAudio.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        if (_root is not null && Directory.Exists(Path.Combine(_root, conversationId.ToString("N"))))
            Directory.Delete(Path.Combine(_root, conversationId.ToString("N")), recursive: true);
        return rows.Count;
    }

    public async Task<int> PurgeAsync(DateTimeOffset olderThan, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LotsDbContext>();
        var expired = await db.ConversationAudio.Where(a => a.CreatedAt < olderThan).ToListAsync(ct);
        foreach (var a in expired)
        {
            var file = _root is null ? null : Path.Combine(_root, a.ConversationId.ToString("N"), a.Id.ToString("N"));
            if (file is not null && File.Exists(file)) File.Delete(file);
        }
        db.ConversationAudio.RemoveRange(expired);
        await db.SaveChangesAsync(ct);
        return expired.Count;
    }
}

/// <summary>Deletes conversation audio past its retention every hour (ADR 0014 point 3).</summary>
public sealed class AudioRetentionWorker(IAudioStore store, IOptions<SpeechOptions> options, TimeProvider clock, ILogger<AudioRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var days = options.Value.AudioRetentionDays;
                var purged = await store.PurgeAsync(clock.GetUtcNow().AddDays(-Math.Max(days, 0)), stop);
                if (purged > 0) logger.LogInformation("Purged {Count} conversation audio clips older than {Days} days", purged, days);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                logger.LogWarning("Audio purge failed: {Error}", ex.Message);
            }
            await Task.Delay(TimeSpan.FromHours(1), stop).ContinueWith(_ => { });
        }
    }
}
