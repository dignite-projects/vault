using System;
using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Index.Search;

/// <summary>
/// Retrieval over Vault Index: find, then read. <see cref="SearchAsync"/> locates chunks; <see cref="GetOutlineAsync"/>
/// shows a document's heading tree; <see cref="ReadAsync"/> returns the text behind a pointer either of them returned.
/// <para>
/// Every method needs <c>VaultIndex.Search</c>, and returns only what the caller's Extract read scope reaches, resolved
/// on each call: the caller also needs Extract's <c>VaultExtract.Documents</c> entry, without which Extract refuses
/// to resolve a scope. Something outside the scope is reported as not found, never as forbidden. Results are
/// evidence: text Index stored, with its source, never a generated answer.
/// </para>
/// </summary>
public interface IIndexSearchAppService : IApplicationService
{
    /// <summary>
    /// Hybrid search: keyword and vector candidates fused by reciprocal rank. Each hit carries a chunk pointer for
    /// <see cref="ReadAsync"/> and its source (document, title, heading path).
    /// </summary>
    Task<IndexSearchResultDto> SearchAsync(IndexSearchInput input);

    /// <summary>The heading tree of one document, each node with a pointer for <see cref="ReadAsync"/>.</summary>
    Task<DocumentOutlineDto> GetOutlineAsync(Guid documentId);

    /// <summary>The text behind a chunk or outline-node pointer, capped and announced when cut.</summary>
    Task<IndexReadResultDto> ReadAsync(IndexReadInput input);
}
