using System.Collections.Generic;
using Volo.Abp.Domain.Values;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// <b>Provenance metadata</b> for document text extraction (persisted value object, #210).
/// Serialized as a whole into the <c>Document.ExtractionMetadata</c> JSON column
/// (<c>AbpJsonValueConverter</c> plus a handwritten <c>ValueComparer</c>, the #206 cross-DB pattern for a
/// JSON-like payload with no per-column query requirement).
/// <para>
/// The <c>Text</c> class-name prefix disambiguates field extraction from text extraction.
/// </para>
/// <para>
/// Carries the engine that produced the Markdown and the completeness signal. JSON written before the
/// native payload archive was dropped still carries a <c>NativePayloadManifest</c> member; it is ignored on read.
/// </para>
/// <para>
/// Get-only properties plus a single parameterized constructor let System.Text.Json reuse this
/// constructor during deserialization, with parameter names matching property names.
/// </para>
/// </summary>
public class DocumentParseMetadata : ValueObject
{
    /// <summary>
    /// The engine that produced the Markdown, such as <c>PdfPig</c>, <c>VisionLlm</c>, <c>PaddleOCR</c>,
    /// <c>AzureDocumentIntelligence</c> or <c>ElBruno.MarkItDotNet</c>; null when unknown.
    /// </summary>
    public string? ProviderName { get; }

    /// <summary>
    /// Whether this text extraction is <b>complete</b> (#268). <c>true</c> means all content was
    /// captured; <c>false</c> means content is known to be missing, such as truncated OCR output,
    /// duplicate-guard drops, or pages in a multi-page PDF that could not be transcribed. Historical
    /// records created before this signal default to <c>true</c> and are treated as complete.
    /// </summary>
    public bool IsComplete { get; }

    /// <summary>Short diagnostic when incomplete; <c>null</c> when complete.</summary>
    public string? IncompleteReason { get; }

    public DocumentParseMetadata(
        string? providerName,
        bool isComplete = true,
        string? incompleteReason = null)
    {
        ProviderName = providerName;
        IsComplete = isComplete;
        IncompleteReason = incompleteReason;
    }

    protected override IEnumerable<object> GetAtomicValues()
    {
        // Nullable members use empty-string placeholders: ABP's ValueEquals compares atomics with object.Equals.
        yield return ProviderName ?? string.Empty;
        yield return IsComplete;
        yield return IncompleteReason ?? string.Empty;
    }
}
