using Lots.Shell.Core.Tools;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Lots.Shell.Core.Knowledge;

/// <summary>Holds what the store reported at startup (backend, pgvector) for the Knowledge page.</summary>
public sealed class KnowledgeStatus
{
    public StoreInfo Info { get; set; } = new("not initialised", false, null);
}

public static class KnowledgeSetup
{
    public static IServiceCollection AddKnowledge(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<KnowledgeOptions>(config.GetSection(KnowledgeOptions.Section));
        services.AddSingleton<KnowledgeStatus>();
        services.AddSingleton<IEmbeddingModel, OpenAiEmbeddingModel>();
        services.AddHttpClient(nameof(KnowledgeIndexer), h =>
        {
            h.Timeout = TimeSpan.FromSeconds(30);
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Lots-Knowledge/1.0");
        });
        services.AddSingleton<IKnowledgeStore>(sp =>
        {
            var cs = sp.GetRequiredService<IConfiguration>().GetConnectionString("Lots");
            using var scope = sp.CreateScope();
            // Same database as everything else; a non-relational EF provider (tests) gets the in-memory store.
            if (string.IsNullOrEmpty(cs) || !scope.ServiceProvider.GetRequiredService<Lots.Shell.Persistence.LotsDbContext>().Database.IsRelational())
                return new InMemoryKnowledgeStore(sp.GetRequiredService<TimeProvider>());
            return new PostgresKnowledgeStore(NpgsqlDataSource.Create(cs), sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<PostgresKnowledgeStore>>());
        });
        services.AddScoped<KnowledgeIndexer>();
        services.AddSingleton<IToolSource, KnowledgeToolSource>();
        services.AddHostedService<KnowledgeWorker>();
        return services;
    }

    /// <summary>Creates the tables and applies the sources defined in configuration (config as code; read-only in the API).</summary>
    public static async Task InitialiseKnowledgeAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var store = services.GetRequiredService<IKnowledgeStore>();
        services.GetRequiredService<KnowledgeStatus>().Info = await store.InitialiseAsync(ct);

        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<KnowledgeOptions>>().Value;
        var existing = (await store.ListSourcesAsync(ct)).ToDictionary(s => s.Id);
        foreach (var c in options.Sources)
        {
            var errors = new List<string>();
            var readers = KnowledgeAccess.Normalise(c.Readers, errors.Add);
            if (string.IsNullOrWhiteSpace(c.Id) || !SourceKinds.All.Contains(c.Kind) || readers.Count == 0 || errors.Count > 0)
                throw new InvalidOperationException($"Knowledge source '{c.Id}' in configuration is invalid: needs id, kind ({string.Join('|', SourceKinds.All)}) and readers. {string.Join("; ", errors)}");
            var wanted = new KnowledgeSource(c.Id, c.Name ?? c.Id, c.Kind, c.Location, readers, "config", ManagedBy: "config");
            // Re-queue only when the definition changed; an unchanged source keeps its index (re-index from the page).
            if (existing.TryGetValue(c.Id, out var old) && old.ManagedBy == "config" && old.Name == wanted.Name && old.Kind == wanted.Kind
                && old.Location == wanted.Location && old.Readers.SequenceEqual(wanted.Readers))
                continue;
            await store.UpsertSourceAsync(wanted, ct);
        }
    }
}
