namespace Dignite.Vault.Extract.Documents;

/// <summary>Where a document's own file is kept: a managed Blob Management container the host declares.</summary>
public static class DocumentFileConsts
{
    /// <summary>
    /// The managed container that holds each document's original file, and each page-range sub-document's cut, by id
    /// (<c>FileOrigin.FileId</c>). A stored name: the host declares a container of exactly this name, and renaming it
    /// strands every file in it.
    /// </summary>
    public const string ContainerName = "extract-files";
}
