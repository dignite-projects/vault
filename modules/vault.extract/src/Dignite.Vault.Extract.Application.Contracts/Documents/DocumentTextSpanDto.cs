namespace Dignite.Vault.Extract.Documents;

/// <summary>A range of a document's Markdown, in UTF-16 code units: the units a .NET or JavaScript string indexes by.</summary>
public class DocumentTextSpanDto
{
    public int Offset { get; set; }

    public int Length { get; set; }
}
