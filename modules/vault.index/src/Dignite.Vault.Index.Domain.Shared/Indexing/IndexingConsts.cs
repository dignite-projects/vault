namespace Dignite.Vault.Index.Indexing;

public static class IndexingConsts
{
    /// <summary>A SHA-256 in lowercase hex.</summary>
    public const int ContentHashLength = 64;

    /// <summary>A chunk's keywords, joined: at most 8 of at most 64 characters each.</summary>
    public const int MaxKeywordsLength = 600;

    /// <summary>A chunk's answerable questions, one per line: at most 5 of at most 200 characters each.</summary>
    public const int MaxQuestionsLength = 1100;

    public const int MaxHeadingPathLength = 1024;

    public const int MaxTemplateNameLength = 64;

    public const int MaxModelIdLength = 256;

    public const int MaxProfileStampLength = 64;

    /// <summary>The document title Index keeps for citations; a longer one is cut.</summary>
    public const int MaxTitleLength = 256;

    /// <summary>A BCP 47 tag as Extract stores it; a longer one is not kept.</summary>
    public const int MaxLanguageLength = 16;

    /// <summary>What a compilation says about itself (sections it could not summarize, levels it left out); cut beyond.</summary>
    public const int MaxArtifactNoteLength = 1024;
}
