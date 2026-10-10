using System.Text.RegularExpressions;
using FastEndpoints;
using Lots.Shell.Core.Knowledge;
using Lots.Shell.Core.Policy;
using Lots.Shell.Features.Conversations;
using Microsoft.Extensions.Options;

namespace Lots.Shell.Features.Knowledge;

public sealed record SourceDto(
    string Id, string Name, string Kind, string? Location, IReadOnlyList<string> Readers, string Owner, string Status, string? Error,
    DateTimeOffset? IndexedAt, int Documents, int Chunks, string? EmbedModel, string ManagedBy, bool CanManage, bool Personal);

public sealed record KnowledgeOverview(string Backend, bool VectorExtension, string? Note, bool EmbeddingsConfigured, string EmbedModel, IReadOnlyList<SourceDto> Sources);

public sealed record DocumentDto(Guid Id, string ExternalId, string Title, string? Url, int Chars, DateTimeOffset UpdatedAt);

public sealed record HitDto(
    string ChunkId, string SourceId, string SourceName, string Title, string? Url, DateTimeOffset UpdatedAt, string Heading, string Text,
    double Score, int? VectorRank, int? TextRank);

internal static partial class KnowledgeRules
{
    public static bool CanManage(KnowledgeSource s, Principal me, IConfiguration config) =>
        ConversationViews.IsAdmin(me, config) || (IsPersonal(s) && s.Owner == me.UserId);

    /// <summary>Defined in configuration or applied from Git: read-only here (principle 7).</summary>
    public static bool IsAsCode(KnowledgeSource s) => s.ManagedBy is "config" or "gitops";

    /// <summary>A personal source (#56): only its owner can read it.</summary>
    public static bool IsPersonal(KnowledgeSource s) => s.Readers.Count == 1 && s.Readers[0] == "user:" + s.Owner;

    public static SourceDto ToDto(KnowledgeSource s, Principal me, IConfiguration config) => new(
        s.Id, s.Name, s.Kind, ConversationViews.IsAdmin(me, config) ? s.Location : null, s.Readers, s.Owner, s.Status, s.Error, s.IndexedAt,
        s.Documents, s.Chunks, s.EmbedModel, s.ManagedBy, CanManage(s, me, config) && !IsAsCode(s), IsPersonal(s));

    public static HitDto ToDto(KnowledgeHit h) =>
        new(h.ChunkId, h.SourceId, h.SourceName, h.Title, h.Url, h.UpdatedAt, h.Heading, h.Text, h.Score, h.VectorRank, h.TextRank);

    public static string Slug(string s)
    {
        var slug = NonSlug().Replace(s.ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "source" : slug[..Math.Min(slug.Length, 48)];
    }

    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex NonSlug();
}

/// <summary>Knowledge status and the sources the caller may read (admins: all).</summary>
public sealed class KnowledgeOverviewEndpoint(IKnowledgeStore store, KnowledgeStatus status, IEmbeddingModel embeddings, ICurrentPrincipal who, IConfiguration config)
    : EndpointWithoutRequest<KnowledgeOverview>
{
    public override void Configure() => Get("/knowledge");

    public override async Task HandleAsync(CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var admin = ConversationViews.IsAdmin(me, config);
        var sources = (await store.ListSourcesAsync(ct)).Where(s => admin || KnowledgeAccess.CanRead(s, me)).Select(s => KnowledgeRules.ToDto(s, me, config)).ToList();
        var info = status.Info;
        await Send.OkAsync(new KnowledgeOverview(info.Backend, info.VectorExtension, admin ? info.Note : null, embeddings.Configured, embeddings.Model, sources), ct);
    }
}

public sealed record CreateSourceRequest(string? Id, string Name, string Kind = SourceKinds.Upload, string? Location = null, List<string>? Readers = null, bool Personal = false);

/// <summary>
/// Creates or redefines a source. Admins may create any source; everyone else only a personal upload source that only they can read
/// (#56). Sources defined in configuration are read-only here.
/// </summary>
public sealed class CreateSourceEndpoint(IKnowledgeStore store, KnowledgeIndexer indexer, ICurrentPrincipal who, IConfiguration config)
    : Endpoint<CreateSourceRequest, SourceDto>
{
    public override void Configure() => Post("/knowledge/sources");

    public override async Task HandleAsync(CreateSourceRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var admin = ConversationViews.IsAdmin(me, config);
        if (string.IsNullOrWhiteSpace(req.Name)) AddError(x => x.Name, "Name is required.");
        if (!SourceKinds.All.Contains(req.Kind)) AddError(x => x.Kind, $"Kind must be one of {string.Join(", ", SourceKinds.All)}.");

        List<string> readers;
        string id;
        if (req.Personal)
        {
            if (req.Kind != SourceKinds.Upload) AddError(x => x.Kind, "Personal sources hold uploaded documents only.");
            readers = ["user:" + me.UserId];
            id = req.Id ?? $"my-{KnowledgeRules.Slug(me.UserId)}-{KnowledgeRules.Slug(req.Name ?? "")}";
        }
        else
        {
            if (!admin)
            {
                await Send.ForbiddenAsync(ct);
                return;
            }
            readers = KnowledgeAccess.Normalise(req.Readers ?? [], e => AddError(x => x.Readers!, e));
            if (readers.Count == 0) AddError(x => x.Readers!, "At least one reader (*, role:<name> or user:<id>) is required.");
            id = KnowledgeRules.Slug(req.Id ?? req.Name ?? "");
            if (req.Kind == SourceKinds.Directory)
                try { indexer.AllowedDirectory(req.Location); }
                catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException) { AddError(x => x.Location!, ex.Message); }
            if (req.Kind == SourceKinds.Url && string.IsNullOrWhiteSpace(req.Location)) AddError(x => x.Location!, "List the URLs, one per line.");
        }
        ThrowIfAnyErrors();

        var existing = await store.GetSourceAsync(id, ct);
        if (existing is not null && (KnowledgeRules.IsAsCode(existing) || !KnowledgeRules.CanManage(existing, me, config)))
        {
            AddError(KnowledgeRules.IsAsCode(existing) ? "This source is managed as code (configuration or Git) and is read-only here." : "That source id is taken.");
            await Send.ErrorsAsync(409, ct);
            return;
        }

        await store.UpsertSourceAsync(new KnowledgeSource(id, req.Name!.Trim(), req.Kind, req.Personal ? null : req.Location, readers,
            existing?.Owner ?? me.UserId), ct);
        await Send.OkAsync(KnowledgeRules.ToDto((await store.GetSourceAsync(id, ct))!, me, config), ct);
    }
}

public sealed record SourceRequest(string Id);

public sealed class DeleteSourceEndpoint(IKnowledgeStore store, ICurrentPrincipal who, IConfiguration config) : Endpoint<SourceRequest>
{
    public override void Configure() => Delete("/knowledge/sources/{Id}");

    public override async Task HandleAsync(SourceRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var s = await store.GetSourceAsync(req.Id, ct);
        if (s is null || !KnowledgeRules.CanManage(s, me, config))
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        if (KnowledgeRules.IsAsCode(s))
        {
            AddError("This source is defined in configuration; remove it there.");
            await Send.ErrorsAsync(409, ct);
            return;
        }
        await store.DeleteSourceAsync(s.Id, ct);
        await Send.NoContentAsync(ct);
    }
}

public sealed class ReindexSourceEndpoint(IKnowledgeStore store, ICurrentPrincipal who, IConfiguration config) : Endpoint<SourceRequest>
{
    public override void Configure() => Post("/knowledge/sources/{Id}/reindex");

    public override async Task HandleAsync(SourceRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var s = await store.GetSourceAsync(req.Id, ct);
        if (s is null || !KnowledgeRules.CanManage(s, me, config))
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await store.QueueAsync(s.Id, ct);
        await Send.ResponseAsync(new { id = s.Id, status = SourceStatus.Queued }, 202, ct);
    }
}

public sealed class ListDocumentsEndpoint(IKnowledgeStore store, ICurrentPrincipal who, IConfiguration config) : Endpoint<SourceRequest, List<DocumentDto>>
{
    public override void Configure() => Get("/knowledge/sources/{Id}/documents");

    public override async Task HandleAsync(SourceRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var s = await store.GetSourceAsync(req.Id, ct);
        if (s is null || !(KnowledgeAccess.CanRead(s, me) || ConversationViews.IsAdmin(me, config)))
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var docs = await store.DocumentContentsAsync(s.Id, ct);
        await Send.OkAsync(docs.Select(d => new DocumentDto(d.Id, d.ExternalId, d.Title, d.Url, d.Content.Length, d.UpdatedAt)).ToList(), ct);
    }
}

public sealed record AddTextRequest(string Id, string Title, string Text);

public sealed class AddFileRequest
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    public IFormFile? File { get; set; }
}

/// <summary>Stores a document in an upload source (replacing one with the same name) and queues the source for indexing.</summary>
internal static class DocumentUpload
{
    public static async Task<(KnowledgeSource? Source, int Status, string? Error)> CheckAsync(
        IKnowledgeStore store, string sourceId, Principal me, IConfiguration config, KnowledgeOptions options, string externalId, CancellationToken ct)
    {
        var s = await store.GetSourceAsync(sourceId, ct);
        if (s is null || !KnowledgeRules.CanManage(s, me, config)) return (null, 404, null);
        if (s.Kind != SourceKinds.Upload) return (null, 409, $"Documents can only be added to upload sources; this one is a {s.Kind} source.");
        if (KnowledgeRules.IsPersonal(s) && !ConversationViews.IsAdmin(me, config) && s.Documents >= options.PersonalMaxDocuments
            && (await store.DocumentsAsync(s.Id, ct)).All(d => d.ExternalId != externalId))
            return (null, 409, $"A personal source holds at most {options.PersonalMaxDocuments} documents.");
        return (s, 200, null);
    }

    public static async Task<DocumentDto> StoreAsync(IKnowledgeStore store, KnowledgeSource s, string externalId, string title, string text, CancellationToken ct)
    {
        var id = await store.PutDocumentAsync(s.Id, externalId, title, null, text, ct);
        await store.QueueAsync(s.Id, ct);
        return new DocumentDto(id, externalId, title, null, text.Length, DateTimeOffset.UtcNow);
    }
}

/// <summary>Adds a text document (JSON).</summary>
public sealed class AddTextEndpoint(IKnowledgeStore store, IOptions<KnowledgeOptions> options, ICurrentPrincipal who, IConfiguration config)
    : Endpoint<AddTextRequest, DocumentDto>
{
    public override void Configure() => Post("/knowledge/sources/{Id}/documents");

    public override async Task HandleAsync(AddTextRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title)) AddError(x => x.Title, "A title is required.");
        if (string.IsNullOrWhiteSpace(req.Text)) AddError(x => x.Text, "Text is required.");
        if (req.Text?.Length > options.Value.MaxDocumentBytes) AddError(x => x.Text, $"Longer than {options.Value.MaxDocumentBytes} characters.");
        ThrowIfAnyErrors();
        var title = req.Title.Trim();
        var (s, status, error) = await DocumentUpload.CheckAsync(store, req.Id, who.Get(HttpContext), config, options.Value, title, ct);
        if (s is null)
        {
            if (error is null) await Send.NotFoundAsync(ct);
            else { AddError(error); await Send.ErrorsAsync(status, ct); }
            return;
        }
        await Send.OkAsync(await DocumentUpload.StoreAsync(store, s, title, title, req.Text, ct), ct);
    }
}

/// <summary>Uploads a file (multipart field <c>file</c>; md, txt, html, docx). Text is extracted with headings kept.</summary>
public sealed class AddFileEndpoint(IKnowledgeStore store, IOptions<KnowledgeOptions> options, ICurrentPrincipal who, IConfiguration config)
    : Endpoint<AddFileRequest, DocumentDto>
{
    public override void Configure()
    {
        Post("/knowledge/sources/{Id}/files");
        AllowFileUploads();
    }

    public override async Task HandleAsync(AddFileRequest req, CancellationToken ct)
    {
        if (req.File is not { } file)
        {
            AddError(x => x.File!, "Send the document as the multipart field 'file'.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        if (!TextExtraction.Supports(file.FileName)) AddError(x => x.File!, $"Supported: {string.Join(", ", TextExtraction.SupportedExtensions)}.");
        if (file.Length > options.Value.MaxDocumentBytes) AddError(x => x.File!, $"Larger than {options.Value.MaxDocumentBytes} bytes.");
        ThrowIfAnyErrors();

        var externalId = Path.GetFileName(file.FileName);
        var (s, status, error) = await DocumentUpload.CheckAsync(store, req.Id, who.Get(HttpContext), config, options.Value, externalId, ct);
        if (s is null)
        {
            if (error is null) await Send.NotFoundAsync(ct);
            else { AddError(error); await Send.ErrorsAsync(status, ct); }
            return;
        }
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        string text;
        try { text = TextExtraction.Extract(file.FileName, buffer.ToArray()); }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            AddError(x => x.File!, "Could not read the document: " + ex.Message);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        var title = req.Title ?? TextExtraction.TitleOf(text, Path.GetFileNameWithoutExtension(file.FileName));
        await Send.OkAsync(await DocumentUpload.StoreAsync(store, s, externalId, title, text, ct), ct);
    }
}

public sealed record DocumentRequest(string Id, Guid DocumentId);

public sealed class DeleteDocumentEndpoint(IKnowledgeStore store, ICurrentPrincipal who, IConfiguration config) : Endpoint<DocumentRequest>
{
    public override void Configure() => Delete("/knowledge/sources/{Id}/documents/{DocumentId}");

    public override async Task HandleAsync(DocumentRequest req, CancellationToken ct)
    {
        var me = who.Get(HttpContext);
        var s = await store.GetSourceAsync(req.Id, ct);
        if (s is null || !KnowledgeRules.CanManage(s, me, config) || s.Kind != SourceKinds.Upload)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await store.DeleteDocumentAsync(s.Id, req.DocumentId, ct);
        await Send.NoContentAsync(ct);
    }
}

public sealed record SearchRequest(string Q, int? K = null, string? Source = null);

/// <summary>Try-it search with the same identity filter the agent's tool uses: results are only what the caller may read.</summary>
public sealed class SearchKnowledgeEndpoint(IKnowledgeStore store, IEmbeddingModel embeddings, ICurrentPrincipal who) : Endpoint<SearchRequest, List<HitDto>>
{
    public override void Configure() => Get("/knowledge/search");

    public override async Task HandleAsync(SearchRequest req, CancellationToken ct)
    {
        if (!embeddings.Configured)
        {
            AddError("No embedding model is configured (Models:Aliases:embed).");
            await Send.ErrorsAsync(503, ct);
            return;
        }
        if (string.IsNullOrWhiteSpace(req.Q))
        {
            AddError(x => x.Q, "q is required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }
        var hits = await KnowledgeToolSource.SearchAsync(store, embeddings, req.Q, KnowledgeAccess.TokensOf(who.Get(HttpContext)),
            Math.Clamp(req.K ?? 8, 1, 20), req.Source, ct);
        await Send.OkAsync(hits.Select(KnowledgeRules.ToDto).ToList(), ct);
    }
}

public sealed record ChunkRequest(string Id);

/// <summary>One cited passage, for the sources list under an answer. Not found unless the caller may read it.</summary>
public sealed class GetChunkEndpoint(IKnowledgeStore store, ICurrentPrincipal who) : Endpoint<ChunkRequest, HitDto>
{
    public override void Configure() => Get("/knowledge/chunks/{Id}");

    public override async Task HandleAsync(ChunkRequest req, CancellationToken ct)
    {
        var hit = await store.ChunkAsync(req.Id, KnowledgeAccess.TokensOf(who.Get(HttpContext)), ct);
        if (hit is null) await Send.NotFoundAsync(ct);
        else await Send.OkAsync(KnowledgeRules.ToDto(hit), ct);
    }
}
