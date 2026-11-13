using System;
using System.Linq;
using System.Threading.Tasks;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Domain.Services;

namespace Dignite.Vault.Index.Profiles;

/// <summary>
/// The settings a document type is indexed with: its own profile, else its layer's default profile, else the
/// built-in defaults. Reads the ambient tenant's layer only, like Extract's types and fields.
/// </summary>
public class IndexProfileResolver : DomainService
{
    private readonly IRepository<IndexProfile, Guid> _profileRepository;

    public IndexProfileResolver(IRepository<IndexProfile, Guid> profileRepository)
    {
        _profileRepository = profileRepository;
    }

    public virtual async Task<ResolvedIndexProfile> ResolveAsync(Guid documentTypeId)
    {
        var candidates = await _profileRepository.GetListAsync(p => p.DocumentTypeId == documentTypeId || p.DocumentTypeId == null);
        var profile = candidates.FirstOrDefault(p => p.DocumentTypeId == documentTypeId)
                      ?? candidates.FirstOrDefault(p => p.DocumentTypeId == null);

        return profile is null
            ? ResolvedIndexProfile.BuiltIn
            : new ResolvedIndexProfile(
                profile.ChunkingStrategy, profile.MaxTokensPerChunk, profile.OverlapTokens, profile.Enrichment, profile.Compilation);
    }
}
