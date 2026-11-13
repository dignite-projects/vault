using System;
using Dignite.Vault.Index.Indexing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>
/// The chunk enricher, when the host registers a chat model under <see cref="VaultIndexConsts.ChatClientKey"/>.
/// Without one, a profile's enrichment is not applied, and the settings stamp says so, so the documents are enriched
/// on their next rebuild after a model is added.
/// </summary>
public class IndexEnricherProvider : ITransientDependency
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory _loggerFactory;

    public IndexEnricherProvider(IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
    {
        _serviceProvider = serviceProvider;
        _loggerFactory = loggerFactory;
    }

    public virtual bool IsAvailable => ChatClient is not null;

    protected virtual IChatClient? ChatClient => _serviceProvider.GetKeyedService<IChatClient>(VaultIndexConsts.ChatClientKey);

    /// <summary>What a profile asking for <paramref name="requested"/> gets: nothing without a model.</summary>
    public virtual IndexEnrichment Effective(IndexEnrichment requested) => IsAvailable ? requested : IndexEnrichment.None;

    /// <summary>
    /// An enricher for <paramref name="enrichment"/> that writes in <paramref name="language"/> (the document's tag);
    /// <c>null</c> when there is nothing to add.
    /// </summary>
    public virtual IndexChunkEnricher? Create(IndexEnrichment enrichment, string? language = null)
    {
        var chatClient = ChatClient;
        return chatClient is null || enrichment == IndexEnrichment.None
            ? null
            : new IndexChunkEnricher(chatClient, enrichment, _loggerFactory.CreateLogger<IndexChunkEnricher>(), language);
    }
}
