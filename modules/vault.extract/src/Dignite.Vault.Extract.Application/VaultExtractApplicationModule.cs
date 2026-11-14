using Dignite.Abp.Notifications;
using Dignite.Vault.Extract.Abstractions;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents.Cabinets;
using Dignite.Vault.Extract.Documents.Pipelines.Classification;
using Dignite.Vault.Extract.Documents.Pipelines.FieldExtraction;
using Dignite.Vault.Extract.Documents.Pipelines.Parse;
using Dignite.Vault.Extract.Documents.Pipelines.Segmentation;
using Dignite.Vault.Extract.Notifications;
using Dignite.Vault.Parse;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Application;
using Volo.Abp.BackgroundJobs;
using Volo.Abp.DistributedLocking;
using Volo.Abp.Gdpr;
using Volo.Abp.Mapperly;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Extract;

[DependsOn(
    typeof(VaultExtractAbstractionsModule),
    typeof(VaultParseAbstractionsModule),
    typeof(VaultExtractDomainModule),
    typeof(VaultExtractApplicationContractsModule),
    typeof(AbpDddApplicationModule),
    typeof(AbpBackgroundJobsModule),
    typeof(AbpDistributedLockingAbstractionsModule),   // DocumentWriteLock
    typeof(AbpNotificationsAbstractionsModule),   // operator notifications for the uploader (#680)
    typeof(AbpGdprAbstractionsModule),   // the user-data deletion event the uploader's name is erased on (#698)
    typeof(AbpMapperlyModule)
    )]
public class VaultExtractApplicationModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddMapperlyObjectMapper<VaultExtractApplicationModule>();

        var configuration = context.Services.GetConfiguration();
        Configure<VaultExtractBehaviorOptions>(configuration.GetSection("Vault:ExtractBehavior"));

        // Default routing for the uploader notifications (#680): live push. A module owns the default for its own
        // notifications and the host, configured last, overrides it (later writes win). It has to be a rule rather than
        // left to the host: a process running the notification framework stateless (no inbox store) fails at startup
        // for any defined notification that resolves to no channel, which would break every host that embeds
        // Application without configuring routing. A host that does not run the SignalR notifier only gets a startup
        // warning naming the channel.
        Configure<NotificationRoutingOptions>(options =>
            options.ForNotifications(
                VaultExtractNotificationNames.All,
                VaultExtractNotificationConsts.SignalRChannelName));

        // Each slow stage gets a background job worker of its own, so a scan's OCR or a long contract's field
        // extraction never holds up the documents behind it in another stage; the other jobs share ABP's default
        // worker. A worker runs up to AbpBackgroundJobWorkerOptions.MaxParallelJobExecutionCount jobs at a time, which
        // the host sets. The names are the workers' distributed locks.
        Configure<AbpBackgroundJobWorkerOptions>(options =>
        {
            options.AddDedicatedWorker("VaultExtract:Parse", typeof(DocumentParseJobArgs));
            options.AddDedicatedWorker(
                "VaultExtract:Classification",
                typeof(DocumentClassificationJobArgs),
                typeof(DocumentSegmentationJobArgs),
                typeof(DocumentCabinetSuggestionJobArgs));
            options.AddDedicatedWorker("VaultExtract:FieldExtraction", typeof(DocumentFieldExtractionJobArgs));
        });
    }
}
