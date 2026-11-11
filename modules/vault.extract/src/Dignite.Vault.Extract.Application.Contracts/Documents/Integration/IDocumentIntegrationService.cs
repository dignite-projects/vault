using System;
using System.Threading.Tasks;
using Volo.Abp;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace Dignite.Vault.Extract.Documents.Integration;

/// <summary>
/// The in-process pull side of the egress, for a module in the same host that keeps derived data of Ready
/// documents (Vault Index). It reacts to <c>DocumentReadyEto</c> and the lifecycle events from a background job or
/// an event handler, so there is no user behind the call.
/// <para>
/// <b>No user, so no authorization check, and nothing outside the Ready set.</b> It returns only what
/// <c>DocumentReadyEto</c> has already announced to every subscriber of the tenant, under the ambient tenant. That is
/// also why it is deliberately not behind the <c>VaultExtract.Enable</c> feature gate, which only principal-bearing
/// paths carry (see <c>VaultExtractFeatures.Enable</c>).
/// </para>
/// <para>
/// <b>Never on a path that serves a user.</b> A consumer that answers a user's query filters its own rows by
/// <see cref="IDocumentAppService.GetReadScopeAsync"/> instead; reading documents through this service on that path
/// would skip the read rule. Not exposed over HTTP: ABP keeps an <c>[IntegrationService]</c> off the auto API
/// controllers unless a host sets <c>AbpAspNetCoreMvcOptions.ExposeIntegrationServices</c>, and the module's own
/// controllers do not implement it. Not an MCP tool either.
/// </para>
/// </summary>
[IntegrationService]
public interface IDocumentIntegrationService : IApplicationService
{
    /// <summary>
    /// The document, if it is Ready for consumers under the ambient tenant: <c>Ready</c>, not a container, with a
    /// confirmed type. <c>null</c> otherwise, which covers an unknown id, another tenant's document, a document in the
    /// recycle bin, and a document that was Ready when its event was published and has since been sent back to
    /// review. The caller treats <c>null</c> as "nothing to apply now" and keeps what it derived from the last
    /// Ready version; deletion reaches it separately as <c>DocumentDeletedEto</c>.
    /// <para>
    /// The Markdown is returned whole. A consumer that sends it on to an LLM applies its own ceiling.
    /// </para>
    /// </summary>
    Task<DocumentIntegrationDto?> FindReadyAsync(Guid id);

    /// <summary>
    /// One page of the ids of the Ready-for-consumers documents under the ambient tenant, for a first backfill or a
    /// rebuild. A keyset cursor: pass the last id of the previous page as <see cref="GetReadyDocumentIdsInput.AfterId"/>
    /// (null for the first page) and stop at an empty page. Ordered by id, live documents only.
    /// </summary>
    Task<ListResultDto<Guid>> GetReadyIdsAsync(GetReadyDocumentIdsInput input);
}
