using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Dignite.Vault.Parse;

/// <summary>
/// Cuts a range of pages out of a paged file into a new file of the same format, the pages as they are (no re-render,
/// no re-encode): what splitting a scanned bundle into its documents needs. An engine registers one for each format it
/// can cut, found through <c>[ExposeServices(typeof(ParsePageCopier))]</c>; a host without one for a format leaves
/// such files unsplit by page.
/// </summary>
public abstract class ParsePageCopier
{
    /// <summary>Whether this copier cuts files of this kind; <paramref name="extension"/> is lower-case with its dot.</summary>
    public abstract bool CanCopy(string extension, string? mediaType);

    /// <summary>
    /// Pages <paramref name="firstPage"/> to <paramref name="lastPage"/> (1-based, inclusive) of <paramref name="source"/>
    /// as a new file. Throws <see cref="InvalidDataException"/> when the file cannot be read or the range is outside it.
    /// </summary>
    public abstract Task<byte[]> CopyPagesAsync(Stream source, int firstPage, int lastPage, CancellationToken cancellationToken = default);
}
