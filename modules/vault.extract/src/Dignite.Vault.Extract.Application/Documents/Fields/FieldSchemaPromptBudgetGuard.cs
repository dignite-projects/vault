using System;
using System.Collections.Generic;
using System.IO;
using Dignite.Vault.Extract.Ai;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.DependencyInjection;

namespace Dignite.Vault.Extract.Documents.Fields;

/// <summary>
/// Enforces the per-document-type budget for field-definition prompt text (#468). The invariant is deliberately
/// aggregate-wide: a per-field limit cannot bound the schema message because the field count is not capped.
/// </summary>
public class FieldSchemaPromptBudgetGuard : ITransientDependency
{
    private readonly VaultExtractBehaviorOptions _options;

    public FieldSchemaPromptBudgetGuard(IOptions<VaultExtractBehaviorOptions> options)
    {
        _options = options.Value;
    }

    public virtual void EnsureCanPersist(string documentTypeCode, IEnumerable<string?> prompts)
    {
        var totalLength = GetTotalLength(prompts);
        if (totalLength <= MaxLength)
        {
            return;
        }

        throw new BusinessException(VaultExtractErrorCodes.FieldDefinition.SchemaPromptBudgetExceeded)
            .WithData("DocumentTypeCode", documentTypeCode)
            .WithData("ActualLength", totalLength)
            .WithData("MaxLength", MaxLength);
    }

    /// <summary>
    /// Defense-in-depth assertion at the final LLM call site. Normal configuration writes make this unreachable;
    /// throwing prevents a repository bypass, seed, future migration, or a host that lowered the ceiling after the
    /// types were configured from sending an unbounded schema prompt. An <see cref="InvalidDataException"/>: the schema
    /// is over a configured limit and stays so on every retry, so <c>ProviderFailureClassifier</c> ends the job instead
    /// of letting ABP retry it.
    /// </summary>
    public virtual void AssertWithinBudget(IEnumerable<string?> prompts)
    {
        var totalLength = GetTotalLength(prompts);
        if (totalLength > MaxLength)
        {
            throw new InvalidDataException(
                $"The persisted field schema contains {totalLength} prompt characters, exceeding the configured " +
                $"MaxFieldSchemaPromptLength ceiling of {MaxLength}.");
        }
    }

    protected virtual long GetTotalLength(IEnumerable<string?> prompts)
    {
        long totalLength = 0;
        foreach (var prompt in prompts)
        {
            // FieldDefinition normalizes whitespace-only prompts to null, so projected writes must use the same
            // semantics rather than charging budget for text that will not be persisted.
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                totalLength += prompt.Length;
            }
        }

        return totalLength;
    }

    private int MaxLength
    {
        get
        {
            if (_options.MaxFieldSchemaPromptLength < 0)
            {
                throw new InvalidOperationException(
                    "Vault:ExtractBehavior:MaxFieldSchemaPromptLength must be zero or greater.");
            }

            return _options.MaxFieldSchemaPromptLength;
        }
    }
}
