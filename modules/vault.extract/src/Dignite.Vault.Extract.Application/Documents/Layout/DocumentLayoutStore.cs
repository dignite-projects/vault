using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// <remarks>
    /// While the layout is current, <c>fields</c> places <paramref name="fieldSources"/> on the pages:
    /// <c>[{ "name", "spans": [{ "offset", "length" }], "sources": [{ "page", "polygon" }] }]</c>, the regions of the
    /// narrowest elements — a table cell rather than its table — whose rendered Markdown overlaps a span. Without a
    /// current layout the ranges cannot be placed, and <c>fields</c> is empty.
    /// </remarks>
    public virtual async Task<string?> GetAsync(
        Guid documentId,
        string? markdown,
        IReadOnlyDictionary<string, IReadOnlyList<DocumentTextSpan>>? fieldSources = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = await _blobContainer.GetOrNullAsync(BlobName(documentId), cancellationToken);
        if (stream is null)
        {
            return null;
        }

        var root = (await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken))!.AsObject();
        var current = markdown is not null && root["markdownSha256"]?.GetValue<string>() == Hash(markdown);
        root["current"] = current;
        root["fields"] = current && fieldSources is { Count: > 0 } && root["document"] is JsonObject tree
            ? PlaceFields(ExtractionDocumentJson.Read(tree), markdown!, fieldSources)
            : new JsonArray();
        return root.ToJsonString();
    }

    protected virtual JsonArray PlaceFields(
        ExtractionDocument tree,
        string markdown,
        IReadOnlyDictionary<string, IReadOnlyList<DocumentTextSpan>> fieldSources)
    {
        var map = ExtractionDocumentMarkdown.Map(tree);
        var fields = new JsonArray();
        if (!string.Equals(map.Markdown, markdown, StringComparison.Ordinal))
        {
            // The hash matched, so the renderer itself changed since the parse: its offsets no longer line up.
            return fields;
        }

        foreach (var (name, spans) in fieldSources.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var sources = spans.SelectMany(span => map.SourcesOf(span.Offset, span.Length)).Distinct().ToList();
            fields.Add(new JsonObject
            {
                ["name"] = name,
                ["spans"] = new JsonArray(spans.Select(span => (JsonNode?)new JsonObject
                {
                    ["offset"] = span.Offset,
                    ["length"] = span.Length
                }).ToArray()),
                ["sources"] = ExtractionDocumentJson.WriteSources(sources)
            });
        }

        return fields;
    }

    public virtual Task<bool> DeleteAsync(Guid documentId, CancellationToken cancellationToken = default)
        => _blobContainer.DeleteAsync(BlobName(documentId), cancellationToken);

    private static string Hash(string markdown) => ContentHasher.Sha256Hex(Encoding.UTF8.GetBytes(markdown));
}
