using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dignite.Abp.FlexFields;
using Volo.Abp;
using Volo.Abp.Domain.Entities.Auditing;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// A type-bound field definition (field architecture v3, #558/#559) — Vault Extract's implementation of
/// the FlexFields kernel's <see cref="IFlexField"/> contract. Replaces v2's now-removed
/// <c>FieldDefinition</c> entity (#593).
/// <para>
/// Unique constraint stays <c>(TenantId, DocumentTypeId, Name)</c>: fields remain bound to a single
/// document type, deliberately <b>not</b> the tenant-wide reusable field library plus per-usage split
/// that the site repository builds on the same kernel (#558 non-goal). With one binding per field,
/// <see cref="IsRequired"/> / <see cref="IsSearchable"/> live here rather than in a separate usage
/// object, and the provider hands them to the kernel per document.
/// </para>
/// <para>
/// Everything <see cref="IFlexField"/> declares is mapped by the kernel's
/// <c>ConfigureFlexField&lt;Field&gt;()</c>. The properties below it — <see cref="DocumentTypeId"/>,
/// <see cref="DisplayOrder"/>, <see cref="IsRequired"/>, <see cref="IsSearchable"/>,
/// <see cref="IsUniqueKey"/> — are Vault Extract's own and Vault Extract maps them, the same way the
/// site repository adds its own <c>GroupName</c> beside the contract's members.
/// </para>
/// </summary>
public class Field : FullAuditedAggregateRoot<Guid>, IFlexField, IMultiTenant
{
    /// <summary>
    /// Deliberately the <b>same</b> pattern v2's <c>FieldDefinition</c> enforced, referenced rather
    /// than copied. The FlexFields kernel validates no format on <c>Name</c> at all — its
    /// <c>ConfigureFlexField</c> maps only a max length, leaving the character set to the downstream —
    /// so adopting the kernel does <b>not</b> carry this guard over by itself. It has to be re-declared
    /// here, or v3 would silently drop it.
    /// </summary>
    private static readonly Regex NameRegex = new(
        FieldDefinitionConsts.NamePattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public virtual Guid? TenantId { get; private set; }

    /// <summary>Internal association to the parent document type by immutable id (#207).</summary>
    public virtual Guid DocumentTypeId { get; private set; }

    /// <summary>
    /// Machine contract key: the JSON schema key in LLM prompts, the <c>ExtractedFields</c> dictionary
    /// key, the downstream contract id — and, new in v3, <b>the key this field's value is stored under
    /// in every document's value bag</b>.
    /// <para>
    /// That last role is why renaming is no longer free. Under v2 the value rows keyed on the immutable
    /// <c>FieldDefinitionId</c>, so a rename touched nothing else; under v3 the bag keys on this string,
    /// so a rename must rewrite every document's bag through
    /// <c>IFlexFieldValueMigrator&lt;Document&gt;</c>, in the order that interface documents. The query
    /// index is unaffected either way — its rows key on the field id.
    /// </para>
    /// <para>
    /// Constrained by <see cref="FieldDefinitionConsts.NamePattern"/>, which is a <b>prompt-injection
    /// boundary</b>, not a formatting preference: this value is concatenated raw into the LLM's schema
    /// message, so the allow-list is what keeps newlines, quotes and Markdown control characters out of
    /// prompt context.
    /// </para>
    /// </summary>
    public virtual string Name { get; private set; } = default!;

    /// <summary>Human-readable label. Does <b>not</b> enter LLM prompts.</summary>
    public virtual string DisplayName { get; private set; } = default!;

    /// <summary>
    /// The LLM extraction instruction — what v2 called <c>Prompt</c>, now carried by the contract's
    /// <see cref="IFlexField.Description"/> (#559 resolution 4 rationale: the kernel's own consumers
    /// already treat Description as the field's AI-facing briefing).
    /// <para>
    /// Optional: when null the model infers what to extract from <see cref="Name"/> and the field type
    /// alone. Uncapped, as #447 decided — it is admin-authored configuration and may be long structured
    /// Markdown. The kernel maps it with no length limit, so nothing narrows that here.
    /// </para>
    /// </summary>
    public virtual string? Description { get; private set; }

    /// <summary>
    /// Registration key of the <c>IFieldType</c> this field is bound to — <c>Text</c>, <c>Number</c>,
    /// <c>DateTime</c>, <c>Boolean</c>, <c>Select</c>, the <c>CKEditor</c> bolt-on that carries v2's
    /// <c>LongText</c>, or Vault Extract's own <c>Tags</c>. Replaces v2's <c>FieldDataType</c> enum.
    /// <para>
    /// A stored key, not a class name: renaming a field type orphans every field bound to it.
    /// </para>
    /// </summary>
    public virtual string FieldTypeName { get; private set; } = default!;

    /// <summary>Type-specific configuration, interpreted by the field type (e.g. <c>Select.Options</c>).</summary>
    public virtual FieldConfigurationDictionary Configuration { get; private set; } = new();

    public virtual int DisplayOrder { get; private set; }

    public virtual bool IsRequired { get; private set; }

    /// <summary>
    /// Whether this field's values are decomposed into the query index, and therefore filterable.
    /// <para>
    /// New in v3 (#558). Under v2 every extracted value was indexed unconditionally — there was no
    /// opt-out — so migrated fields default to <c>true</c> to preserve existing behaviour. A field whose
    /// type is not indexable at all (<c>IFieldType.IndexValueType</c> is null, e.g. the CKEditor type
    /// serving long text) yields nothing regardless of this flag; the kernel's
    /// <c>GetSearchableValues</c> short-circuits on the type before consulting the usage.
    /// </para>
    /// </summary>
    public virtual bool IsSearchable { get; private set; }

    /// <summary>
    /// Whether this field participates in the document type's duplicate-detection key (#411). Not part
    /// of <see cref="IFlexField"/> — a Vault Extract concern the kernel has no opinion about.
    /// </summary>
    public virtual bool IsUniqueKey { get; private set; }

    /// <summary>
    /// The checks this field's stored value must pass, evaluated in code wherever a document's values are written. Empty
    /// for a field with none. Not part of <see cref="IFlexField"/>: validation is Vault Extract's concern, not the kernel's.
    /// </summary>
    public virtual IReadOnlyList<FieldValidationRule> ValidationRules { get; private set; } = Array.Empty<FieldValidationRule>();

    protected Field()
    {
    }

    public Field(
        Guid id,
        Guid? tenantId,
        Guid documentTypeId,
        string name,
        string displayName,
        string fieldTypeName,
        string? description = null,
        FieldConfigurationDictionary? configuration = null,
        int displayOrder = 0,
        bool isRequired = false,
        bool isSearchable = true,
        bool isUniqueKey = false)
        : base(id)
    {
        TenantId = tenantId;
        DocumentTypeId = Check.NotDefaultOrNull<Guid>(documentTypeId, nameof(documentTypeId));
        SetName(name);
        SetDisplayName(displayName);
        SetFieldTypeName(fieldTypeName);
        SetDescription(description);
        Configuration = configuration ?? new FieldConfigurationDictionary();
        DisplayOrder = displayOrder;
        IsRequired = isRequired;
        IsSearchable = isSearchable;
        IsUniqueKey = isUniqueKey;
    }

    /// <summary>
    /// Renames the field. Callers must not use this directly — go through the domain service that also
    /// rewrites the value bags this name keys into, or every document keeps its value under the old key
    /// where nothing can reach it.
    /// </summary>
    public virtual void SetName(string name)
    {
        Check.NotNullOrWhiteSpace(name, nameof(name), FieldDefinitionConsts.MaxNameLength);

        if (!NameRegex.IsMatch(name))
        {
            throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.InvalidName)
                .WithData("name", name)
                .WithData("pattern", FieldDefinitionConsts.NamePattern);
        }

        Name = name;
    }

    /// <summary>
    /// DisplayName never reaches an LLM prompt; rejecting control characters is defense-in-depth for UI
    /// rendering and logs. Same rule v2's <c>FieldDefinition</c> enforced, so a migrated value cannot
    /// become unsavable.
    /// </summary>
    public virtual void SetDisplayName(string displayName)
    {
        Check.NotNullOrWhiteSpace(displayName, nameof(displayName), FieldDefinitionConsts.MaxDisplayNameLength);

        if (displayName.Any(char.IsControl))
        {
            throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.InvalidDisplayName)
                .WithData("displayName", displayName);
        }

        DisplayName = displayName;
    }

    /// <summary>
    /// Normalizes a candidate display name into a <b>safe-to-save</b> form: replace control characters
    /// such as line breaks / tabs / null bytes with spaces, fold consecutive whitespace, and truncate to
    /// <see cref="FieldDefinitionConsts.MaxDisplayNameLength"/>.
    /// <para>
    /// Used by "draft from prompt" (#264) to prefill forms — <c>FieldDraftSuggestionAppService</c> is the
    /// sole live caller. Carried over unchanged from v2's <c>FieldDefinition.NormalizeDisplayName</c>
    /// (#593 removed that entity once the v3 data migration ran; this method moved here because it is
    /// still needed). Output normalized through this method is <b>guaranteed</b> to pass
    /// <see cref="SetDisplayName"/>, so saving will not immediately loud-fail. Whitespace input -> empty
    /// string, letting callers treat it as "draft unavailable".
    /// </para>
    /// </summary>
    public static string NormalizeDisplayName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var cleaned = new string(raw.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        if (cleaned.Length > FieldDefinitionConsts.MaxDisplayNameLength)
        {
            cleaned = cleaned[..FieldDefinitionConsts.MaxDisplayNameLength];
            // Truncating by UTF-16 code units can split surrogate pairs and leave a trailing unpaired high surrogate.
            // SetDisplayName would allow it, but it is an invalid paired character and can break later JSON serialization / DB round-trips.
            // Drop the trailing unpaired high surrogate (#264 review2 #2).
            if (cleaned.Length > 0 && char.IsHighSurrogate(cleaned[^1]))
            {
                cleaned = cleaned[..^1];
            }

            cleaned = cleaned.Trim();
        }

        return cleaned;
    }

    public virtual void SetFieldTypeName(string fieldTypeName)
    {
        FieldTypeName = Check.NotNullOrWhiteSpace(
            fieldTypeName, nameof(fieldTypeName), FlexFieldConsts.MaxFieldTypeNameLength);
    }

    /// <summary>Blank collapses to null — "no instruction", so the model infers from name and type alone.</summary>
    public virtual void SetDescription(string? description)
    {
        Description = string.IsNullOrWhiteSpace(description) ? null : description;
    }

    public virtual void SetConfiguration(FieldConfigurationDictionary? configuration)
    {
        Configuration = configuration ?? new FieldConfigurationDictionary();
    }

    public virtual void SetDisplayOrder(int displayOrder)
    {
        DisplayOrder = displayOrder;
    }

    public virtual void SetIsRequired(bool isRequired)
    {
        IsRequired = isRequired;
    }

    public virtual void SetIsSearchable(bool isSearchable)
    {
        IsSearchable = isSearchable;
    }

    public virtual void SetIsUniqueKey(bool isUniqueKey)
    {
        IsUniqueKey = isUniqueKey;
    }

    /// <summary>
    /// Replaces the field's validation rules. Checks each rule's shape (see <see cref="CheckValidationRules"/>); whether an
    /// expression parses and whether each input is a field of the same type are checked by the application layer, which
    /// holds the expression engine and the type's other fields.
    /// </summary>
    public virtual void SetValidationRules(IEnumerable<FieldValidationRule>? rules)
    {
        ValidationRules = CheckValidationRules(Name, Id, rules);
    }

    /// <summary>
    /// The shape every rule of a field must have: at most <see cref="FieldValidationRuleConsts.MaxRulesPerField"/>
    /// rules; a one-line description and an expression within their lengths; at most
    /// <see cref="FieldValidationRuleConsts.MaxInputsPerRule"/> inputs, each with an identifier for an alias (not
    /// <c>value</c>, not repeated) and another field (not this one, not repeated). Returns the rules as checked; throws a
    /// <c>BusinessException</c> naming the first rule that fails (<c>Extract:TooManyFieldValidationRules</c>,
    /// <c>Extract:InvalidFieldValidationRule</c> or <c>Extract:InvalidFieldValidationRuleInput</c>). Static so an import
    /// can check a pack's rules before it writes anything.
    /// </summary>
    /// <param name="fieldName">The field's name, for the error.</param>
    /// <param name="fieldId">The field's id, which no input may name.</param>
    /// <param name="rules">The rules.</param>
    public static IReadOnlyList<FieldValidationRule> CheckValidationRules(
        string fieldName,
        Guid fieldId,
        IEnumerable<FieldValidationRule>? rules)
    {
        var list = rules?.ToList() ?? new List<FieldValidationRule>();
        if (list.Count > FieldValidationRuleConsts.MaxRulesPerField)
        {
            throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.TooManyValidationRules)
                .WithData("Name", fieldName)
                .WithData("Max", FieldValidationRuleConsts.MaxRulesPerField);
        }

        var checkedRules = new List<FieldValidationRule>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            checkedRules.Add(CheckRule(fieldName, fieldId, list[i], i + 1));
        }

        return checkedRules;
    }

    private static FieldValidationRule CheckRule(string fieldName, Guid fieldId, FieldValidationRule? rule, int position)
    {
        if (rule == null)
        {
            throw InvalidRule(fieldName, position);
        }

        if (string.IsNullOrWhiteSpace(rule.Description)
            || rule.Description.Length > FieldValidationRuleConsts.MaxDescriptionLength
            || rule.Description.Any(char.IsControl))
        {
            throw InvalidRule(fieldName, position);
        }

        if (string.IsNullOrWhiteSpace(rule.Expression) || rule.Expression.Length > FieldValidationRuleConsts.MaxExpressionLength)
        {
            throw InvalidRule(fieldName, position);
        }

        var inputs = rule.Inputs ?? Array.Empty<FieldValidationRuleInput>();
        if (inputs.Count > FieldValidationRuleConsts.MaxInputsPerRule)
        {
            throw InvalidInput(fieldName, position, null);
        }

        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var fieldIds = new HashSet<Guid>();
        foreach (var input in inputs)
        {
            if (input == null || input.Alias == null || !AliasRegex.IsMatch(input.Alias)
                || input.Alias == FieldValidationRuleConsts.ValueAlias)
            {
                throw InvalidInput(fieldName, position, input?.Alias);
            }

            if (!aliases.Add(input.Alias))
            {
                throw InvalidInput(fieldName, position, input.Alias);
            }

            if (input.FieldId == Guid.Empty || input.FieldId == fieldId)
            {
                throw InvalidInput(fieldName, position, input.Alias);
            }

            if (!fieldIds.Add(input.FieldId))
            {
                throw InvalidInput(fieldName, position, input.Alias);
            }
        }

        return new FieldValidationRule(rule.Description, rule.Expression, inputs.ToList());
    }

    private static BusinessException InvalidRule(string fieldName, int position)
        => new BusinessException(VaultExtractErrorCodes.FieldDefinition.InvalidValidationRule)
            .WithData("Name", fieldName)
            .WithData("Index", position)
            .WithData("MaxDescription", FieldValidationRuleConsts.MaxDescriptionLength)
            .WithData("MaxExpression", FieldValidationRuleConsts.MaxExpressionLength);

    /// <summary>The error for a rule's unusable input, also thrown by the application layer for an input outside the type.</summary>
    public static BusinessException InvalidInput(string fieldName, int position, string? alias)
        => new BusinessException(VaultExtractErrorCodes.FieldDefinition.InvalidValidationRuleInput)
            .WithData("Name", fieldName)
            .WithData("Index", position)
            .WithData("Alias", alias ?? string.Empty)
            .WithData("MaxInputs", FieldValidationRuleConsts.MaxInputsPerRule);

    private static readonly Regex AliasRegex = new(
        FieldValidationRuleConsts.AliasPattern,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
