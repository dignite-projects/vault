using System;
using Dignite.Vault.Index.Indexing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Index.Compilation;

/// <summary>
/// The summary compiler, when the host registers Index's chat model under <see cref="VaultIndexConsts.ChatClientKey"/>.
/// Without one, a profile's compilation is not applied; summaries already compiled keep serving while their source
/// is unchanged.
/// </summary>
public class DocumentSummaryCompilerProvider : ITransientDependency
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerFactory _loggerFactory;

    public DocumentSummaryCompilerProvider(IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
    {
        _serviceProvider = serviceProvider;
        _loggerFactory = loggerFactory;
    }

    public virtual bool IsAvailable => ChatClient is not null;

    protected virtual IChatClient? ChatClient => _serviceProvider.GetKeyedService<IChatClient>(VaultIndexConsts.ChatClientKey);

    /// <summary>What a profile asking for <paramref name="requested"/> gets: nothing without a model.</summary>
    public virtual IndexCompilation Effective(IndexCompilation requested) => IsAvailable ? requested : IndexCompilation.None;

    /// <summary>A compiler that writes in <paramref name="language"/> (the document's tag); <c>null</c> without a model.</summary>
    public virtual DocumentSummaryCompiler? Create(string? language)
        => ChatClient is { } chatClient
            ? new DocumentSummaryCompiler(chatClient, _loggerFactory.CreateLogger<DocumentSummaryCompiler>(), language)
            : null;
}
