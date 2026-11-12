namespace Dignite.Vault.Index;

public static class VaultIndexDbProperties
{
    /// <summary>The same prefix as Extract's tables: the two modules share it, and table names are kept distinct by hand.</summary>
    public static string DbTablePrefix { get; set; } = "Vault";

    public static string? DbSchema { get; set; } = null;

    /// <summary>
    /// Index's own connection string name. When a host does not configure it, ABP falls back to <c>Default</c>, so the
    /// two modules share a database by deployment, not by code. Index's vector and full-text storage needs SQL Server
    /// 2025 with Full-Text Search, or Azure SQL, behind whichever connection string this resolves to.
    /// </summary>
    public const string ConnectionStringName = "VaultIndex";
}
