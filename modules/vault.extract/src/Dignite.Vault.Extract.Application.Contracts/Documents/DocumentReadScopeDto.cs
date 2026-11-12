using System;
using System.Collections.Generic;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// The calling user's read scope over the documents of the current tenant, as
/// <see cref="IDocumentAppService.GetReadScopeAsync"/> returns it, for a module that keeps its own rows about
/// documents and must show a user only those Extract would let them read (Vault Index).
/// <para>
/// <b>Version 1 reads:</b> a document is readable when <see cref="IsUnrestricted"/> is <c>true</c>, or its type id
/// is in <see cref="DocumentTypeIds"/>, or its owner (<c>DocumentIntegrationDto.OwnerId</c>) equals
/// <see cref="OwnerId"/>. An empty <see cref="DocumentTypeIds"/> with a <c>null</c> <see cref="OwnerId"/> reaches
/// nothing.
/// </para>
/// <para>
/// <see cref="Version"/> changes whenever that reading changes. A consumer accepts only the versions it was written
/// for and refuses the rest: a newer rule read by an older consumer could otherwise widen what a user sees.
/// </para>
/// </summary>
public class DocumentReadScopeDto
{
    public int Version { get; set; }

    /// <summary>The caller reads every document of the tenant; the other two members are then empty.</summary>
    public bool IsUnrestricted { get; set; }

    /// <summary>The types whose documents the caller reads through a per-type grant.</summary>
    public List<Guid> DocumentTypeIds { get; set; } = new();

    /// <summary>The caller's user id when they read the documents they uploaded; <c>null</c> otherwise.</summary>
    public Guid? OwnerId { get; set; }
}
