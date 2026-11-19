using System;
using System.Collections.Generic;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// What <c>vault_extract_submit_document</c> answers: the document it created, or why it created none — the user has
/// not yet agreed to what an inline submission leaves out (<see cref="ConfirmationRequired"/>), or declined it.
/// Every text here is a compile-time constant or a system value.
/// </summary>
public sealed record DocumentSubmissionResult
{
    public bool Submitted { get; init; }

    /// <summary>
    /// <c>true</c> when nothing was submitted because the user has not agreed yet: tell them <see cref="Consequences"/>,
    /// and call again with <c>userConfirmed=true</c> only if they still want this document submitted inline.
    /// </summary>
    public bool ConfirmationRequired { get; init; }

    /// <summary><c>true</c> when the user declined the confirmation the client showed; nothing was submitted.</summary>
    public bool Declined { get; init; }

    /// <summary>What an inline submission gives up, for the user to weigh; present when confirmation is required.</summary>
    public IReadOnlyList<string>? Consequences { get; init; }

    /// <summary>What to do next.</summary>
    public string? Instruction { get; init; }

    public Guid? Id { get; init; }

    public string? Uri { get; init; }

    /// <summary>The document's lifecycle right after the submission: <c>Ready</c> when every stage was supplied and nothing holds it for review.</summary>
    public string? LifecycleStatus { get; init; }

    public string? DocumentTypeCode { get; init; }

    /// <summary>How many of the submitted field values the server stored.</summary>
    public int? StoredFieldCount { get; init; }
}
