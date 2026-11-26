using System.Collections.Generic;
using System.Text.Json;

namespace Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;

/// <summary>
/// Structured result of one <see cref="FieldExtractionWorkflow.ExtractAsync"/> call: the extracted field
/// <see cref="Values"/> and the passages they were quoted from. Missing or unparseable values are <c>null</c>.
/// <para>
/// It carries no validation warnings: a field's validation rules are evaluated in code over the values the caller
/// (<c>FieldExtractionService</c>) stores, not judged by the extraction model.
/// </para>
/// </summary>
public sealed record FieldExtractionWorkflowResult(
    IReadOnlyDictionary<string, JsonElement?> Values,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Sources = null)
{
    /// <summary>
    /// Per field name, the passages the model quoted from the document as where it found the value: untrusted text,
    /// bounded in count and length, that the caller locates in the Markdown. A field it quoted nothing for is absent.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Sources { get; init; } =
        Sources ?? new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>
    /// The fields whose value the answer did not finish: it was cut off at the output limit before or inside them. They
    /// read as <c>null</c> in <see cref="Values"/>, but unlike a <c>null</c> the model wrote, nothing is known about them,
    /// so the caller keeps what the document already holds. Empty for an answer that finished.
    /// </summary>
    public IReadOnlySet<string> UnfinishedFields { get; init; } = new HashSet<string>();

    /// <summary>
    /// The answer was cut off before it settled every value: the field set it leaves is not the document's full
    /// extraction. Sources cut off alone do not count, because they only locate values.
    /// </summary>
    public bool IsIncomplete => UnfinishedFields.Count > 0;
}
