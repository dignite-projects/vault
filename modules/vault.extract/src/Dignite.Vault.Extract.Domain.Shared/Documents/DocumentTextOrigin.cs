namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// Where a document's text came from. The numbers are stored and sent on the wire: never renumber them.
/// </summary>
public enum DocumentTextOrigin
{
    /// <summary>Vault read it from the file (a text-extraction engine or OCR).</summary>
    Parsed = 0,

    /// <summary>
    /// The caller converted the document itself and submitted the Markdown. Vault validated its shape, not its fidelity
    /// to a file; a re-parse from the original file, where there is one, replaces it with a parsed text.
    /// </summary>
    Submitted = 1
}
