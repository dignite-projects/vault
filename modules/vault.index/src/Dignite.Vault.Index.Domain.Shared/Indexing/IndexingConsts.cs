namespace Dignite.Vault.Index.Indexing;

public static class IndexingConsts
{
    /// <summary>A SHA-256 in lowercase hex.</summary>
    public const int ContentHashLength = 64;

    public const int MaxHeadingPathLength = 1024;

    public const int MaxTemplateNameLength = 64;

    public const int MaxModelIdLength = 256;

    public const int MaxProfileStampLength = 64;

    /// <summary>The document title Index keeps for citations; a longer one is cut.</summary>
    public const int MaxTitleLength = 256;
}
