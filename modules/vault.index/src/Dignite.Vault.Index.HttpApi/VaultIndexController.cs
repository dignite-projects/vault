using Dignite.Vault.Index.Localization;
using Volo.Abp.AspNetCore.Mvc;

namespace Dignite.Vault.Index;

public abstract class VaultIndexController : AbpControllerBase
{
    protected VaultIndexController()
    {
        LocalizationResource = typeof(VaultIndexResource);
    }
}
