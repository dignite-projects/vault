using System;

namespace Dignite.Vault.Extract.Mcp.Documents;

/// <summary>
/// What <c>vault_extract_create_submission_upload</c> answers: a link that stores one file, and the commands that send
/// it from the machine the file is on, so the bytes never pass through the model.
/// </summary>
public sealed record SubmissionUploadResult
{
    /// <summary>The id the file will have: pass it to <c>vault_extract_submit_document</c>.</summary>
    public Guid FileId { get; init; }

    /// <summary>Where to send the file: <c>POST</c>, multipart, in the form field <see cref="FormField"/>.</summary>
    public required string Url { get; init; }

    public string FormField { get; init; } = "file";

    public DateTime ExpiresAt { get; init; }

    /// <summary>The largest file the link takes, in bytes.</summary>
    public long MaxSize { get; init; }

    /// <summary>For sh, bash, zsh and cmd.exe: replace FILE_PATH.</summary>
    public required string Command { get; init; }

    /// <summary>For PowerShell, where <c>curl</c> is an alias of something else: replace FILE_PATH.</summary>
    public required string PowerShellCommand { get; init; }
}
