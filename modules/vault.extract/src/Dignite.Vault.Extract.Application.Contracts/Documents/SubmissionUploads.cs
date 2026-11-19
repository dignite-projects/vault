using System;

namespace Dignite.Vault.Extract.Documents;

/// <summary>What a submission upload link takes. The numbers travel on the wire: never renumber them.</summary>
public enum SubmissionFileKind
{
    /// <summary>The document's Markdown, as a UTF-8 <c>.md</c> or <c>.txt</c> file.</summary>
    Markdown = 0,

    /// <summary>The document's original file, of a type an upload accepts.</summary>
    Original = 1
}

public class CreateSubmissionUploadInput
{
    public SubmissionFileKind Kind { get; set; }
}

/// <summary>
/// A link that stores one file for a submission: sent to <see cref="UploadUrl"/> (<c>POST</c>, multipart, form field
/// <c>file</c>) without signing in, before <see cref="ExpirationTime"/>, then named in the submission by
/// <see cref="FileId"/>. Only the user it was issued to can submit the file; one never submitted is deleted after a day.
/// </summary>
public class SubmissionUploadDto
{
    /// <summary>The id the file will have, given in advance.</summary>
    public Guid FileId { get; set; }

    /// <summary>
    /// Where to send the file, relative to the host (<c>/api/blob-management/uploads/{ticket}</c>). It holds a signed
    /// ticket: a credential for this one upload - do not log it.
    /// </summary>
    public string UploadUrl { get; set; } = default!;

    /// <summary>When the link stops being accepted (UTC).</summary>
    public DateTime ExpirationTime { get; set; }

    /// <summary>The largest file the link takes, in bytes.</summary>
    public long MaxSize { get; set; }
}

/// <summary>The submission upload container: a managed Blob Management container the host declares.</summary>
public static class DocumentSubmissionConsts
{
    /// <summary>
    /// The managed container submission uploads are staged in until the submission takes them. A stored name: the host
    /// declares a container of exactly this name, and renaming it strands the staged files.
    /// </summary>
    public const string UploadContainerName = "extract-submissions";

    /// <summary>How long a submission upload link is accepted, in seconds.</summary>
    public const int UploadLinkLifetimeSeconds = 600;
}
