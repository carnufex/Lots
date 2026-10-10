using System.Net;
using System.Text;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Core.Knowledge;

public sealed class KnowledgeOptions
{
    public const string Section = "Knowledge";

    /// <summary>Directory sources must live under one of these roots (default: /knowledge, a mounted volume).</summary>
    public List<string> DirectoryRoots { get; set; } = ["/knowledge"];

    /// <summary>Largest file or web page indexed.</summary>
    public int MaxDocumentBytes { get; set; } = 5 * 1024 * 1024;

    public int ChunkChars { get; set; } = 1200;
    public int ChunkOverlapChars { get; set; } = 150;

    /// <summary>Sources defined as code. They are created or updated at startup and are read-only through the API.</summary>
    public List<SourceConfig> Sources { get; set; } = [];

    /// <summary>Users may create personal sources only they can read (#56). Their size is capped per user.</summary>
    public int PersonalMaxDocuments { get; set; } = 200;
}

public sealed class SourceConfig
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string Kind { get; set; } = SourceKinds.Directory;
    public string? Location { get; set; }
    public List<string> Readers { get; set; } = [];
    /// <summary>Data class of the passages (#89): public|internal|confidential|restricted.</summary>
    public string Sensitivity { get; set; } = "internal";
}

public sealed record IndexResult(int Documents, int Embedded, int Unchanged, int Removed, int Chunks);

/// <summary>
/// Indexes one source: fetch -> extract -> chunk -> embed -> store (ADR 0016). Documents whose content hash and embedding model are
/// unchanged are skipped; documents that disappeared from a directory or URL list are deleted.
/// </summary>
public sealed class KnowledgeIndexer(IKnowledgeStore store, IEmbeddingModel embeddings, IHttpClientFactory http, IOptions<KnowledgeOptions> options, TimeProvider clock)
{
    private readonly KnowledgeOptions _options = options.Value;

    public async Task<IndexResult> IndexAsync(KnowledgeSource source, CancellationToken ct)
    {
        var model = embeddings.Model;
        var existing = (await store.DocumentsAsync(source.Id, ct)).ToDictionary(d => d.ExternalId);
        var documents = await CollectAsync(source, ct);

        int embedded = 0, unchanged = 0, chunkCount = 0;
        foreach (var (externalId, title, url, content) in documents)
        {
            var hash = KnowledgeText.Hash(content);
            if (existing.TryGetValue(externalId, out var known) && known.ContentHash == hash && known.Model == model)
            {
                unchanged++;
                chunkCount += known.Chunks;
                continue;
            }

            var chunks = Chunker.Split(content, _options.ChunkChars, _options.ChunkOverlapChars);
            // The heading path is part of what is embedded: "Postgres > Restart" says what a short chunk is about.
            var result = chunks.Count == 0
                ? new EmbeddingResult([], model, 0, 0, TimeSpan.Zero)
                : await embeddings.EmbedAsync(chunks.Select(c => string.IsNullOrEmpty(c.Heading) ? c.Text : c.Heading + "\n" + c.Text).ToList(), ct);
            if (result.Model != model)
                throw new InvalidOperationException($"The embedding alias answered with model '{result.Model}' instead of '{model}'; not mixing models in one index.");

            var stored = chunks.Select((c, i) => new StoredChunk(KnowledgeText.ChunkId(source.Id, externalId, c.Seq, c.Text), c.Seq, c.Heading, c.Text, result.Vectors[i])).ToList();
            var doc = new KnowledgeDocument(known?.Id ?? Guid.NewGuid(), source.Id, externalId, title, url, content, hash, clock.GetUtcNow());
            await store.ReplaceDocumentAsync(doc, stored, model, ct);
            embedded++;
            chunkCount += stored.Count;
        }

        var keep = documents.Select(d => d.ExternalId).ToHashSet();
        var removed = existing.Keys.Count(k => !keep.Contains(k));
        if (removed > 0) await store.DeleteDocumentsExceptAsync(source.Id, keep, ct);
        return new IndexResult(documents.Count, embedded, unchanged, removed, chunkCount);
    }

    private async Task<List<(string ExternalId, string Title, string? Url, string Content)>> CollectAsync(KnowledgeSource source, CancellationToken ct)
    {
        switch (source.Kind)
        {
            case SourceKinds.Upload:
                return (await store.DocumentContentsAsync(source.Id, ct)).Select(d => (d.ExternalId, d.Title, d.Url, d.Content)).ToList();

            case SourceKinds.Directory:
            {
                var root = AllowedDirectory(source.Location);
                var list = new List<(string, string, string?, string)>();
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    if (!TextExtraction.Supports(file)) continue;
                    var info = new FileInfo(file);
                    if (info.Length > _options.MaxDocumentBytes) continue;
                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    if (relative.Split('/').Any(p => p.StartsWith('.'))) continue; // .git and other hidden folders
                    var text = TextExtraction.Extract(file, await File.ReadAllBytesAsync(file, ct));
                    list.Add((relative, TextExtraction.TitleOf(text, Path.GetFileNameWithoutExtension(file)), null, text));
                }
                return list;
            }

            case SourceKinds.Url:
            {
                var list = new List<(string, string, string?, string)>();
                var client = http.CreateClient(nameof(KnowledgeIndexer));
                foreach (var line in (source.Location ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!Uri.TryCreate(line, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                        throw new InvalidOperationException($"Not an http(s) URL: {line}");
                    using var res = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                    res.EnsureSuccessStatusCode();
                    var bytes = await ReadCappedAsync(res, ct);
                    var type = res.Content.Headers.ContentType?.MediaType ?? "";
                    var name = type.Contains("html") ? "page.html" : uri.AbsolutePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? "page.md" : "page.txt";
                    var text = TextExtraction.Extract(name, bytes);
                    var title = type.Contains("html") && HtmlTitle(Encoding.UTF8.GetString(bytes)) is { } t ? t : TextExtraction.TitleOf(text, uri.Host + uri.AbsolutePath);
                    list.Add((uri.ToString(), title, uri.ToString(), text));
                }
                return list;
            }

            default:
                throw new InvalidOperationException($"Unknown source kind '{source.Kind}'.");
        }
    }

    /// <summary>A directory source may only point inside a configured root; no reading arbitrary paths of the container.</summary>
    public string AllowedDirectory(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) throw new InvalidOperationException("A directory source needs a location.");
        var full = Path.GetFullPath(location);
        var ok = _options.DirectoryRoots.Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .Any(r => (full + Path.DirectorySeparatorChar).StartsWith(r, StringComparison.Ordinal));
        if (!ok) throw new InvalidOperationException($"'{location}' is outside the allowed knowledge directories ({string.Join(", ", _options.DirectoryRoots)}).");
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Directory '{location}' does not exist.");
        return full;
    }

    private async Task<byte[]> ReadCappedAsync(HttpResponseMessage res, CancellationToken ct)
    {
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > _options.MaxDocumentBytes) throw new InvalidOperationException($"{res.RequestMessage?.RequestUri} is larger than {_options.MaxDocumentBytes} bytes.");
        }
        return buffer.ToArray();
    }

    private static string? HtmlTitle(string html) =>
        System.Text.RegularExpressions.Regex.Match(html, @"<title[^>]*>(.*?)</title>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline)
            is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;
}

/// <summary>
/// Creates the knowledge tables and applies sources defined as code (retrying until the database is reachable, so the shell starts
/// and answers liveness without it), then indexes queued sources. A lease makes each source belong to one replica at a time.
/// <c>Knowledge:WorkerEnabled=false</c> keeps the initialisation but leaves indexing to other replicas.
/// </summary>
public sealed class KnowledgeWorker(IServiceScopeFactory scopes, IServiceProvider services, IKnowledgeStore store, IConfiguration config, ILogger<KnowledgeWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(30);
    private readonly string _owner = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        for (var delay = TimeSpan.FromSeconds(2); !stop.IsCancellationRequested; delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2)))
        {
            try
            {
                await services.InitialiseKnowledgeAsync(stop);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                logger.LogWarning("Knowledge initialisation failed, retrying in {Delay}: {Error}", delay, ex.Message);
                await Task.Delay(delay, stop).ContinueWith(_ => { });
            }
        }
        if (!config.GetValue("Knowledge:WorkerEnabled", true)) return;

        while (!stop.IsCancellationRequested)
        {
            try
            {
                var id = await store.ClaimQueuedAsync(_owner, Lease, stop);
                if (id is not null)
                {
                    await IndexOneAsync(id, stop);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Knowledge worker iteration failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stop).ContinueWith(_ => { });
        }
    }

    private async Task IndexOneAsync(string id, CancellationToken stop)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var source = await store.GetSourceAsync(id, stop);
            if (source is null) return;
            var indexer = scope.ServiceProvider.GetRequiredService<KnowledgeIndexer>();
            var embeddings = scope.ServiceProvider.GetRequiredService<IEmbeddingModel>();
            var result = await indexer.IndexAsync(source, stop);
            await store.SetStatusAsync(id, SourceStatus.Ready, null, embeddings.Model, stop);
            logger.LogInformation("Indexed knowledge source {Source}: {Result}", id, result);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Indexing knowledge source {Source} failed", id);
            await store.SetStatusAsync(id, SourceStatus.Failed, ex.Message, null, CancellationToken.None);
        }
        finally
        {
            await store.ReleaseAsync(id, _owner);
        }
    }
}
