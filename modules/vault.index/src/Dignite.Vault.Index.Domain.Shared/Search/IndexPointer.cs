using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;

namespace Dignite.Vault.Index.Search;

public enum IndexPointerKind
{
    /// <summary>One chunk: <c>chunk:{chunkId}</c>.</summary>
    Chunk = 0,

    /// <summary>
    /// One node of a document's outline and everything under it: <c>node:{documentId}/{path}</c>, where the path is
    /// the node's position in the heading tree, 1-based, dotted (<c>2.1</c> is the first subsection of the second
    /// section).
    /// </summary>
    Node = 1
}

/// <summary>
/// What <c>read</c> accepts: a chunk or an outline node, as search and outline return them. A pointer names Index's
/// own rows, never a character range of the source Markdown, and is valid until the document is indexed again.
/// </summary>
public sealed record IndexPointer
{
    private const string ChunkPrefix = "chunk:";
    private const string NodePrefix = "node:";

    /// <summary>Deeper than any Markdown heading tree (six levels); bounds what a hostile pointer can make the parser do.</summary>
    private const int MaxNodeDepth = 16;

    public IndexPointerKind Kind { get; }

    /// <summary>The chunk id of a chunk pointer; the document id of a node pointer.</summary>
    public Guid Id { get; }

    /// <summary>The node's 1-based position in the heading tree; empty for a chunk pointer.</summary>
    public IReadOnlyList<int> NodePath { get; }

    private IndexPointer(IndexPointerKind kind, Guid id, IReadOnlyList<int> nodePath)
    {
        Kind = kind;
        Id = id;
        NodePath = nodePath;
    }

    public static IndexPointer ForChunk(Guid chunkId) => new(IndexPointerKind.Chunk, chunkId, []);

    public static IndexPointer ForNode(Guid documentId, IReadOnlyList<int> nodePath)
    {
        if (nodePath.Count == 0 || nodePath.Count > MaxNodeDepth || nodePath.Any(i => i < 1))
        {
            throw new ArgumentException("A node path has one to sixteen 1-based positions.", nameof(nodePath));
        }

        return new IndexPointer(IndexPointerKind.Node, documentId, nodePath.ToArray());
    }

    public static bool TryParse(string? value, [NotNullWhen(true)] out IndexPointer? pointer)
    {
        pointer = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        if (value.StartsWith(ChunkPrefix, StringComparison.Ordinal))
        {
            if (Guid.TryParseExact(value[ChunkPrefix.Length..], "D", out var chunkId))
            {
                pointer = ForChunk(chunkId);
                return true;
            }

            return false;
        }

        if (!value.StartsWith(NodePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = value[NodePrefix.Length..];
        var slash = body.IndexOf('/');
        if (slash < 0 || !Guid.TryParseExact(body[..slash], "D", out var documentId))
        {
            return false;
        }

        var parts = body[(slash + 1)..].Split('.');
        if (parts.Length is 0 or > MaxNodeDepth)
        {
            return false;
        }

        var path = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out path[i]) || path[i] < 1)
            {
                return false;
            }
        }

        pointer = new IndexPointer(IndexPointerKind.Node, documentId, path);
        return true;
    }

    public override string ToString() => Kind == IndexPointerKind.Chunk
        ? ChunkPrefix + Id.ToString("D")
        : NodePrefix + Id.ToString("D") + "/" + string.Join('.', NodePath.Select(i => i.ToString(CultureInfo.InvariantCulture)));

    public bool Equals(IndexPointer? other)
        => other is not null && Kind == other.Kind && Id == other.Id && NodePath.SequenceEqual(other.NodePath);

    public override int GetHashCode() => HashCode.Combine(Kind, Id, NodePath.Count);
}
