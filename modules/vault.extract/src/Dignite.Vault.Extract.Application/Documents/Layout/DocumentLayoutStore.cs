using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Parse;
using Microsoft.Extensions.Documents;
using Volo.Abp.BlobStoring;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Documents.Layout;

/// <summary>
/// Keeps the document tree a parse produced — sections, text with roles, tables with spans, images, and the page region
/// every element was read from — next to the document's file, as the blob <c>layout/{documentId}</c>. A re-parse
/// overwrites it; a permanent delete removes it.
/// <para>
/// The blob is <c>{ "schema", "version", "markdownSha256", "document" }</c>: <c>document</c> is the tree in
/// <see cref="ExtractionDocumentJson"/>'s format, and <c>markdownSha256</c> the hash of the Markdown it rendered. Read
/// back, it carries <c>current</c>: whether the document's Markdown is still that one. An operator's correction changes
/// the Markdown and leaves the layout describing the parse.
/// </para>
/// </summary>
public class DocumentLayoutStore : ITransientDependency
{
    public const string Schema = "vault.extract.layout";

    public const int Version = 1;

    private readonly IBlobContainer<VaultExtractDocumentContainer> _blobContainer;

    public DocumentLayoutStore(IBlobContainer<VaultExtractDocumentContainer> blobContainer)
    {
        _blobContainer = blobContainer;
    }

    /// <summary>The blob name of a document's layout. Stored data: never rename.</summary>
    public static string BlobName(Guid documentId) => $"layout/{documentId}";

    public virtual async Task SaveAsync(Guid documentId, ExtractionDocument tree, string markdown, CancellationToken cancellationToken = default)
    {
        var envelope = new JsonObject
        {
            ["schema"] = Schema,
            ["version"] = Version,
            ["markdownSha256"] = Hash(markdown),
            ["document"] = ExtractionDocumentJson.Write(tree)
        };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(envelope.ToJsonString()), writable: false);
        await _blobContainer.SaveAsync(BlobName(documentId), stream, overrideExisting: true, cancellationToken);
    }

    /// <summary>
    /// The stored layout as JSON, with <c>current</c> telling whether <paramref name="markdown"/> (the document's Markdown
    /// now) is the one the tree rendered; <c>null</c> when the document has no layout.
    /// </summary>
    public virtual async Task<string?> GetAsync(Guid documentId, string? markdown, CancellationToken cancellationToken = default)
    {
        await using var stream = await _blobContainer.GetOrNullAsync(BlobName(documentId), cancellationToken);
        if (stream is null)
        {
            return null;
        }

        var root = (await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken))!.AsObject();
        root["current"] = markdown is not null && root["markdownSha256"]?.GetValue<string>() == Hash(markdown);
        return root.ToJsonString();
    }

    public virtual Task<bool> DeleteAsync(Guid documentId, CancellationToken cancellationToken = default)
        => _blobContainer.DeleteAsync(BlobName(documentId), cancellationToken);

    private static string Hash(string markdown) => ContentHasher.Sha256Hex(Encoding.UTF8.GetBytes(markdown));
}
