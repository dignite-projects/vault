using Dignite.Vault.Index.Localization;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Localization;

namespace Dignite.Vault.Index.Permissions;

public class VaultIndexPermissionDefinitionProvider : PermissionDefinitionProvider
{
    public override void Define(IPermissionDefinitionContext context)
    {
        var group = context.AddGroup(VaultIndexPermissions.GroupName, L("Permission:VaultIndex"));
        group.AddPermission(VaultIndexPermissions.Search, L("Permission:VaultIndex.Search"));
        group.AddPermission(VaultIndexPermissions.Profiles, L("Permission:VaultIndex.Profiles"));
        var status = group.AddPermission(VaultIndexPermissions.Status, L("Permission:VaultIndex.Status"));
        status.AddChild(VaultIndexPermissions.StatusRebuild, L("Permission:VaultIndex.Status.Rebuild"));
    }

    private static LocalizableString L(string name)
    {
        return LocalizableString.Create<VaultIndexResource>(name);
    }
}
