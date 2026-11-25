using System.Threading.Tasks;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.MultiTenancy;

namespace Dignite.Vault.Extract.Documents.LegacyFiles;

/// <summary>
/// Starts moving a layer's document files to Blob Management when some are still in Extract's own container: runs with
/// the data seed (<c>--migrate-database</c>) for the Host and every tenant, and on tenant creation, where it finds
/// nothing. Each seed starts a new walk, so a document whose file could not be moved is tried again; one already moved
/// is not read again.
/// </summary>
public class LegacyDocumentFileMoveDataSeedContributor : IDataSeedContributor, ITransientDependency
{
    private readonly IDocumentRepository _documentRepository;
    private readonly IBackgroundJobManager _backgroundJobManager;
    private readonly ICurrentTenant _currentTenant;

    public LegacyDocumentFileMoveDataSeedContributor(
        IDocumentRepository documentRepository,
        IBackgroundJobManager backgroundJobManager,
        ICurrentTenant currentTenant)
    {
        _documentRepository = documentRepository;
        _backgroundJobManager = backgroundJobManager;
        _currentTenant = currentTenant;
    }

    public virtual async Task SeedAsync(DataSeedContext context)
    {
        using (_currentTenant.Change(context.TenantId))
        {
            if ((await _documentRepository.GetIdsWithLegacyFileAsync(afterId: null, maxCount: 1)).Count == 0)
            {
                return;
            }

            await _backgroundJobManager.EnqueueAsync(
                new LegacyDocumentFileMoveJobArgs { TenantId = context.TenantId },
                BackgroundJobPriority.Low);
        }
    }
}
