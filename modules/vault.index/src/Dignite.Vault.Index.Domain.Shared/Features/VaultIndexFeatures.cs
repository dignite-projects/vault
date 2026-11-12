namespace Dignite.Vault.Index.Features;

public static class VaultIndexFeatures
{
    public const string GroupName = "VaultIndex";

    /// <summary>
    /// Turns Vault Index on for a tenant. Enforced on the application services, the way <c>VaultExtract.Enable</c>
    /// is. Whether ingestion, which runs with no signed-in user, also stops for a tenant without the feature is
    /// decided with the ingestion jobs, not here.
    /// </summary>
    public const string Enable = GroupName + ".Enable";
}
