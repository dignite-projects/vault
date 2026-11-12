using Dignite.Vault.Index.Localization;
using Volo.Abp.Features;
using Volo.Abp.Localization;
using Volo.Abp.Validation.StringValues;

namespace Dignite.Vault.Index.Features;

public class VaultIndexFeatureDefinitionProvider : FeatureDefinitionProvider
{
    public override void Define(IFeatureDefinitionContext context)
    {
        var group = context.AddGroup(VaultIndexFeatures.GroupName, L("Feature:VaultIndex"));

        // On by default for the reason VaultExtract.Enable is: a host without the Feature Management module has
        // nowhere to switch it on. A host that wants Index opt-in overrides the default in its own provider.
        group.AddFeature(
            VaultIndexFeatures.Enable,
            defaultValue: "true",
            displayName: L("Feature:VaultIndex.Enable"),
            description: L("Feature:VaultIndex.Enable:Description"),
            valueType: new ToggleStringValueType(),
            isVisibleToClients: true);
    }

    private static LocalizableString L(string name)
    {
        return LocalizableString.Create<VaultIndexResource>(name);
    }
}
