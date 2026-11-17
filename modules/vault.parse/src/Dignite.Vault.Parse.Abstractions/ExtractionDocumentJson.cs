using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Documents;

namespace Dignite.Vault.Parse;

/// <summary>
/// The document tree as JSON, so it can be stored and read back. <c>Microsoft.Extensions.Documents</c> has no
/// serialization of its own (its elements are a class hierarchy without type discriminators), so this is Vault's format,
/// version <see cref="Version"/>. Its shape follows Docling's <c>DoclingDocument</c>: the tree of sections and elements,
/// each element with the page regions it was read from.
/// <para>
/// Source pages are written once and referred to by identifier. Text keeps its role, heading level and content
/// properties; an image keeps its OCR text; a table its cells with their spans, roles and sources; a nested section its
/// own. An engine's raw representation is not written: it is the engine's object, not part of the tree's meaning.
/// </para>
/// </summary>
public static class ExtractionDocumentJson
{
    public const int Version = 1;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Serialize(ExtractionDocument document) => Write(document).ToJsonString(Options);

    public static ExtractionDocument Deserialize(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new JsonException("The document is not a JSON object.");
        var version = root["version"]?.GetValue<int>() ?? 0;
        if (version != Version)
        {
            throw new JsonException($"Unsupported document version {version}; this reads version {Version}.");
        }

        return Read(root);
    }

    /// <summary>The tree as a JSON object, to embed in a larger one.</summary>
    public static JsonObject Write(ExtractionDocument document)
    {
        var pages = new JsonArray();
        foreach (var page in document.SourcePages)
        {
            var written = new JsonObject
            {
                ["id"] = page.Identifier,
                ["number"] = page.PageNumber,
                ["unit"] = page.CoordinateUnit?.ToString(),
                ["origin"] = page.CoordinateOrigin?.ToString()
            };
            if (page.Dimensions is { } dimensions)
            {
                written["width"] = dimensions.Width;
                written["height"] = dimensions.Height;
            }

            pages.Add(written);
        }

        var metadata = new JsonObject();
        foreach (var (key, value) in document.Metadata)
        {
            metadata[key] = JsonSerializer.SerializeToNode(value);
        }

        return new JsonObject
        {
            ["version"] = Version,
            ["identifier"] = document.Identifier,
            ["metadata"] = metadata,
            ["pages"] = pages,
            ["sections"] = new JsonArray(document.Sections.Select(section => (JsonNode?)WriteElement(section)).ToArray())
        };
    }

    /// <summary>The tree from a JSON object written by <see cref="Write"/>.</summary>
    public static ExtractionDocument Read(JsonObject root)
    {
        var document = new ExtractionDocument(root["identifier"]?.GetValue<string>() ?? string.Empty);
        var pages = new Dictionary<string, ExtractionDocumentSourcePage>(StringComparer.Ordinal);
        foreach (var node in root["pages"]?.AsArray() ?? [])
        {
            var page = node!.AsObject();
            var width = page["width"]?.GetValue<double>();
            var height = page["height"]?.GetValue<double>();
            var source = new ExtractionDocumentSourcePage(
                page["id"]!.GetValue<string>(),
                page["number"]?.GetValue<int>(),
                width is { } w && height is { } h ? new ExtractionDocumentPageDimensions(w, h) : null,
                Enum.TryParse<ExtractionDocumentCoordinateUnit>(page["unit"]?.GetValue<string>(), out var unit) ? unit : null,
                Enum.TryParse<ExtractionDocumentCoordinateOrigin>(page["origin"]?.GetValue<string>(), out var origin) ? origin : null);
            pages[source.Identifier] = source;
            document.SourcePages.Add(source);
        }

        foreach (var (key, value) in root["metadata"]?.AsObject() ?? [])
        {
            document.Metadata[key] = value?.Deserialize<JsonElement>();
        }

        foreach (var node in root["sections"]?.AsArray() ?? [])
        {
            if (ReadElement(node!.AsObject(), pages) is ExtractionDocumentSection section)
            {
                document.Sections.Add(section);
            }
        }

        return document;
    }

    private static JsonObject WriteElement(ExtractionDocumentElement element)
    {
        var written = new JsonObject();
        switch (element)
        {
            case ExtractionDocumentSection section:
                written["type"] = "section";
                written["elements"] = new JsonArray(section.Elements.Select(child => (JsonNode?)WriteElement(child)).ToArray());
                break;

            case ExtractionDocumentTextElement text:
                written["type"] = "text";
                written["role"] = text.Role;
                written["headingLevel"] = text.HeadingLevel;
                WriteText(written, text.Content as TextContent);
                break;

            case ExtractionDocumentImageElement image:
                written["type"] = "image";
                written["text"] = image.Text;
                written["alternativeText"] = image.AlternativeText;
                break;

            case ExtractionDocumentTableElement table:
                written["type"] = "table";
                written["rows"] = table.Cells.RowCount;
                written["columns"] = table.Cells.ColumnCount;
                var cells = new JsonArray();
                for (var row = 0; row < table.Cells.RowCount; row++)
                {
                    for (var column = 0; column < table.Cells.ColumnCount; column++)
                    {
                        if (table.Cells[row, column] is not { } cell)
                        {
                            continue;
                        }

                        var writtenCell = new JsonObject
                        {
                            ["row"] = row,
                            ["column"] = column,
                            ["rowSpan"] = cell.RowSpan,
                            ["columnSpan"] = cell.ColumnSpan,
                            ["role"] = cell.Role,
                            ["content"] = cell.Content is { } content ? WriteElement(content) : null,
                            ["sources"] = WriteSources(cell.Sources)
                        };
                        cells.Add(writtenCell);
                    }
                }

                written["cells"] = cells;
                break;

            default:
                written["type"] = "unknown";
                break;
        }

        written["sources"] = WriteSources(element.Sources);
        return written;
    }

    private static void WriteText(JsonObject written, TextContent? content)
    {
        written["text"] = content?.Text;
        if (content?.AdditionalProperties is { Count: > 0 } properties)
        {
            var writtenProperties = new JsonObject();
            foreach (var (key, value) in properties)
            {
                writtenProperties[key] = JsonSerializer.SerializeToNode(value);
            }

            written["properties"] = writtenProperties;
        }
    }

    /// <summary>Source references in this format: <c>[{ "page": id, "polygon": [[x, y], ...] }]</c>, the page an id of <c>pages</c>.</summary>
    public static JsonArray WriteSources(IEnumerable<ExtractionDocumentSourceReference> sources)
        => new(sources.Select(source => (JsonNode?)new JsonObject
        {
            ["page"] = source.Page.Identifier,
            ["polygon"] = source.Polygon is { Count: > 0 } polygon
                ? new JsonArray(polygon.Select(point => (JsonNode?)new JsonArray(point.X, point.Y)).ToArray())
                : null
        }).ToArray());

    private static ExtractionDocumentElement? ReadElement(JsonObject node, IReadOnlyDictionary<string, ExtractionDocumentSourcePage> pages)
    {
        ExtractionDocumentElement? element;
        switch (node["type"]?.GetValue<string>())
        {
            case "section":
                var section = new ExtractionDocumentSection();
                foreach (var child in node["elements"]?.AsArray() ?? [])
                {
                    if (ReadElement(child!.AsObject(), pages) is { } read)
                    {
                        section.Elements.Add(read);
                    }
                }

                element = section;
                break;

            case "text":
                element = new ExtractionDocumentTextElement(ReadText(node), node["role"]?.GetValue<string>())
                {
                    HeadingLevel = node["headingLevel"]?.GetValue<int>()
                };
                break;

            case "image":
                element = new ExtractionDocumentImageElement(text: node["text"]?.GetValue<string>())
                {
                    AlternativeText = node["alternativeText"]?.GetValue<string>()
                };
                break;

            case "table":
                var table = new ExtractionDocumentTableElement(null, node["rows"]!.GetValue<int>(), node["columns"]!.GetValue<int>());
                foreach (var cellNode in node["cells"]?.AsArray() ?? [])
                {
                    var cell = cellNode!.AsObject();
                    var role = cell["role"]?.GetValue<string>();
                    var rowSpan = cell["rowSpan"]?.GetValue<int>() ?? 1;
                    var columnSpan = cell["columnSpan"]?.GetValue<int>() ?? 1;
                    var content = cell["content"] is JsonObject contentNode ? ReadElement(contentNode, pages) : null;
                    var readCell = content is null
                        ? new ExtractionDocumentTableCell(rowSpan, columnSpan, role)
                        : new ExtractionDocumentTableCell(content, rowSpan, columnSpan, role);
                    ReadSources(cell["sources"], readCell.Sources, pages);
                    table.Cells[cell["row"]!.GetValue<int>(), cell["column"]!.GetValue<int>()] = readCell;
                }

                element = table;
                break;

            default:
                return null;
        }

        ReadSources(node["sources"], element.Sources, pages);
        return element;
    }

    private static TextContent ReadText(JsonObject node)
    {
        var content = new TextContent(node["text"]?.GetValue<string>() ?? string.Empty);
        if (node["properties"] is JsonObject properties)
        {
            content.AdditionalProperties = new AdditionalPropertiesDictionary();
            foreach (var (key, value) in properties)
            {
                content.AdditionalProperties[key] = value?.Deserialize<JsonElement>();
            }
        }

        return content;
    }

    private static void ReadSources(
        JsonNode? node, IList<ExtractionDocumentSourceReference> into, IReadOnlyDictionary<string, ExtractionDocumentSourcePage> pages)
    {
        foreach (var sourceNode in node?.AsArray() ?? [])
        {
            var source = sourceNode!.AsObject();
            if (source["page"]?.GetValue<string>() is not { } id || !pages.TryGetValue(id, out var page))
            {
                continue;
            }

            var polygon = source["polygon"]?.AsArray()
                .Select(point => new ExtractionDocumentPoint(point![0]!.GetValue<double>(), point[1]!.GetValue<double>()))
                .ToList();
            into.Add(new ExtractionDocumentSourceReference(page, polygon));
        }
    }
}
