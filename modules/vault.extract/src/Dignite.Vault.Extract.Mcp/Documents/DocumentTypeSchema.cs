using System.Collections.Generic;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// Document type field schema: the LLM-facing read projection for the MCP document-type resource
/// (ambient or explicitly tenant-scoped). It lets downstream AI clients discover which fields a type
/// has and what they hold, so they can populate the search tool's <c>fieldFilters</c> /
/// <c>includeFields</c> with correct field names. <see cref="DisplayName"/> is admin-configured
/// user-derived text and is already wrapped with <c>PromptBoundary.WrapField</c> to prevent indirect
/// prompt injection. <see cref="TypeCode"/> and field <c>Name</c> / <c>FieldType</c> are
/// system-controlled values (allow-list / registration key), so they are emitted raw.
/// </summary>
public sealed record DocumentTypeSchema
{
    public required string TypeCode { get; init; }

    /// <summary>Resource URI for this schema. Uses an explicit tenant scope when the caller supplied tenantId.</summary>
    public required string Uri { get; init; }

    /// <summary>Type display name, already wrapped with PromptBoundary.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The type's fields in display order, capped (see <see cref="FieldsTruncated"/>).</summary>
    public required IReadOnlyList<DocumentTypeFieldSchema> Fields { get; init; }

    /// <summary>How many fields the type has, including those <see cref="Fields"/> leaves out.</summary>
    public int FieldCount { get; init; }

    /// <summary><c>true</c> when <see cref="Fields"/> holds fewer than <see cref="FieldCount"/>.</summary>
    public bool FieldsTruncated { get; init; }
}

/// <summary>
/// Schema projection for a single field. <see cref="Name"/> is the identifier used in
/// <c>fieldFilters</c> / <c>includeFields</c>, and the key of its value in a submission. <see cref="DisplayName"/>
/// is already wrapped with PromptBoundary. How to extract the field (<see cref="ValueSchema"/>,
/// <see cref="ExtractionPrompt"/>) is present only when the caller asked for it: it is useless for query /
/// projection orchestration and would waste LLM context there, while a client that submits field values it
/// extracted itself needs exactly that.
/// </summary>
public sealed record DocumentTypeFieldSchema
{
    public required string Name { get; init; }

    /// <summary>
    /// Field type registration key: <c>Text</c>, <c>Number</c>, <c>Boolean</c>, <c>DateTime</c>,
    /// <c>Select</c>, <c>CKEditor</c> (long text) or <c>Tags</c> (multi-valued). Determines the available
    /// query operators — Text / Boolean / Select / Tags support equality only, while Number and DateTime
    /// support ranges.
    /// <para>
    /// Renamed from <c>dataType</c> in v3 (#559) rather than reused: the value set changed shape at the
    /// same time (v2's <c>Date</c> and <c>DateTime</c> became one <c>DateTime</c> type told apart by
    /// configuration, and <c>LongText</c> became <c>CKEditor</c>), so a client still reading the old key
    /// fails loudly instead of quietly matching new values against the old enum names.
    /// </para>
    /// </summary>
    public required string FieldType { get; init; }

    /// <summary>
    /// Whether the field holds several values. When true, the field is a <b>JSON array</b>
    /// (<c>string[]</c>) in search-result <c>extractedFields</c> rather than a scalar, so clients can
    /// parse it correctly. Equality filtering still matches one value and returns documents containing
    /// that value.
    /// <para>
    /// Kept as its own flag rather than left for the client to infer from <see cref="FieldType"/>: this
    /// is the one thing about a field a client must get right to parse a response at all, and it should
    /// not depend on the client knowing which registration keys happen to be multi-valued.
    /// </para>
    /// </summary>
    public bool IsMultiValue { get; init; }

    /// <summary>
    /// Whether this field can appear in <c>fieldFilters</c> at all. False for a field whose type is not
    /// indexable (long text) or whose admin turned searchability off — filtering on one is rejected, so
    /// saying so up front is cheaper than letting the model discover it through an error.
    /// </summary>
    public bool IsFilterable { get; init; }

    /// <summary>Field display name, already wrapped with PromptBoundary.</summary>
    public string? DisplayName { get; init; }

    public bool IsRequired { get; init; }

    /// <summary>
    /// The JSON schema a submitted value of this field must satisfy, the same one the server's own extraction
    /// constrains its model with: a <c>Select</c>'s options as an <c>enum</c>, a <c>Table</c>'s rows as
    /// <c>{"values": {column: value}}</c>, a <c>DateTime</c> field's format. Only when extraction schemas were asked for.
    /// </summary>
    public System.Text.Json.JsonElement? ValueSchema { get; init; }

    /// <summary>
    /// The field's extraction instruction as its administrator wrote it, already wrapped with PromptBoundary; absent
    /// when there is none (the name says what to extract) or extraction schemas were not asked for.
    /// </summary>
    public string? ExtractionPrompt { get; init; }
}
