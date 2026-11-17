using System;
using System.Collections.Generic;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// <see cref="Document.FieldSources"/>: per field name, the ranges of <see cref="Document.Markdown"/> its value was found
/// in. Stored as one JSON column next to the value bag, with the bag's ordinal keys.
/// </summary>
public class DocumentFieldSources : Dictionary<string, IReadOnlyList<DocumentTextSpan>>
{
    public DocumentFieldSources()
        : base(StringComparer.Ordinal)
    {
    }
}

/// <summary>A range of <see cref="Document.Markdown"/> in UTF-16 code units, as a .NET or JavaScript string indexes it.</summary>
public sealed record DocumentTextSpan(int Offset, int Length);
