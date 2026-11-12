using Dignite.Vault.Index.Features;
using Dignite.Vault.Index.Localization;
using Volo.Abp.Application.Services;
using Volo.Abp.Features;

namespace Dignite.Vault.Index;

// On the base class so that every application service, and the REST controllers and MCP tools that call them through
// their interfaces, is gated at once. Background jobs and event handlers never go through an application service:
// ingestion keeps a tenant's index current while the feature is off, so turning it back on needs no backfill.
[RequiresFeature(VaultIndexFeatures.Enable)]
public abstract class VaultIndexAppService : ApplicationService
{
    protected VaultIndexAppService()
    {
        LocalizationResource = typeof(VaultIndexResource);
        ObjectMapperContext = typeof(VaultIndexApplicationModule);
    }
}
