namespace Dignite.Vault.Index.Permissions;

/// <summary>
/// Index's own permissions. Which documents a search may return is not one of them: that is Extract's read rule,
/// applied through the caller's read scope, so a caller also needs Extract's <c>VaultExtract.Documents</c> entry.
/// </summary>
public static class VaultIndexPermissions
{
    public const string GroupName = "VaultIndex";

    /// <summary>Search the index (REST and MCP), within the caller's Extract read scope.</summary>
    public const string Search = GroupName + ".Search";

    /// <summary>Change how document types are indexed.</summary>
    public const string Profiles = GroupName + ".Profiles";
}
