using System.Collections.Generic;
using System.Text.Json;

namespace Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;

/// <summary>
/// Structured result of one <see cref="FieldExtractionWorkflow.ExtractAsync"/> call (#527 §1): the extracted field
/// <see cref="Values"/> AND any <see cref="ValidationWarnings"/>, from a <b>single</b> LLM response — there is no second
/// call for validation.
/// <para>
/// A field that carries a validation warning still keeps its extracted value in <see cref="Values"/>: a warning is
/// never represented as a <c>null</c> value and is never mapped to <c>MissingRequiredFields</c> (that stays the
/// distinct "not extracted" signal). Missing / unparseable values continue to become <c>null</c> as before. The
/// App-layer caller (<c>FieldExtractionService</c>) persists the values and — resolving each warning's field name to
/// the immutable <c>FieldDefinition</c> Id (#207) — persists the warnings separately, keeping warning text out of field
/// values, search, export, and event payloads (#527 §11).
/// </para>
/// </summary>
public sealed record FieldExtractionWorkflowResult(
    IReadOnlyDictionary<string, JsonElement?> Values,
    IReadOnlyList<FieldValidationWarningResult> ValidationWarnings,
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
    /// The answer was cut off before its warning list closed, so <see cref="ValidationWarnings"/> is empty for want of an
    /// answer, not because every value passed.
    /// </summary>
    public bool ValidationWarningsUnfinished { get; init; }

    /// <summary>
    /// The answer was cut off before it settled every value and warning: the field set it leaves is not the document's
    /// full extraction. Sources cut off alone do not count, because they only locate values.
    /// </summary>
    public bool IsIncomplete => UnfinishedFields.Count > 0 || ValidationWarningsUnfinished;
}

/// <summary>
/// One field validation warning as produced by the LLM (#527 §1), keyed by the field <b>name</b> — the workflow speaks
/// in field names, and the caller resolves the name to the immutable <c>FieldDefinition</c> Id (#207). The workflow has
/// already defensively normalized the untrusted model output (#527 §3): warnings for undeclared fields are discarded,
/// blank / malformed entries dropped, duplicates merged to one per field, the message truncated at a UTF-16 character
/// boundary, and the count capped — so the caller receives a clean, bounded set.
/// </summary>
public sealed record FieldValidationWarningResult(string FieldName, string Message);
