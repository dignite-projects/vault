using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents;
using Dignite.Vault.Extract.EntityFrameworkCore;
using Dignite.Vault.Extract.Host.Data;
using Dignite.Vault.Extract.Host.HealthChecks;
using Dignite.Vault.Extract.Host.Localization;
using Dignite.Vault.Extract.Localization;
using Dignite.Abp.AspNetCore.Mcp;
using Dignite.Vault.Extract.Mcp.Authentication;
using Dignite.Vault.Index;
using Dignite.Vault.Index.EntityFrameworkCore;
using Dignite.Abp.Notifications.SignalR;
using Dignite.NotificationCenter;
using Dignite.NotificationCenter.EntityFrameworkCore;
using Dignite.Vault.Parse.Ocr.VisionLlm;
using Dignite.Vault.Parse;
using Dignite.Vault.Parse.ElBrunoMarkItDown;
using Dignite.Vault.Parse.OpenXml;
using Dignite.Vault.Parse.Pdf;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.EntityFrameworkCore;
using OpenAI;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.OpenApi;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;
using Volo.Abp;
using Volo.Abp.Account;
using Volo.Abp.Account.Web;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.AspNetCore.Mvc.Localization;
using Volo.Abp.AspNetCore.Mvc.UI.Bundling;
using Volo.Abp.AspNetCore.Mvc.UI.Theme.LeptonXLite;
using Volo.Abp.AspNetCore.Mvc.UI.Theme.LeptonXLite.Bundling;
using Volo.Abp.AspNetCore.Mvc.UI.Theme.Shared;
using Volo.Abp.AspNetCore.Serilog;
using Volo.Abp.AuditLogging.EntityFrameworkCore;
using Volo.Abp.Autofac;
using Volo.Abp.BackgroundJobs.EntityFrameworkCore;
using Volo.Abp.BlobStoring;
using Volo.Abp.BlobStoring.FileSystem;
using Volo.Abp.Caching;
using Volo.Abp.Emailing;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore.DistributedEvents;
using Volo.Abp.EntityFrameworkCore.SqlServer;
using Volo.Abp.EventBus.Distributed;
using Volo.Abp.FeatureManagement;
using Volo.Abp.FeatureManagement.EntityFrameworkCore;
using Volo.Abp.Identity;
using Volo.Abp.Identity.EntityFrameworkCore;
using Volo.Abp.Localization;
using Volo.Abp.Mapperly;
using Volo.Abp.Modularity;
using Volo.Abp.MultiTenancy;
using Volo.Abp.OpenIddict;
using Volo.Abp.OpenIddict.EntityFrameworkCore;
using Volo.Abp.PermissionManagement;
using Volo.Abp.PermissionManagement.EntityFrameworkCore;
using Volo.Abp.PermissionManagement.HttpApi;
using Volo.Abp.PermissionManagement.Identity;
using Volo.Abp.PermissionManagement.OpenIddict;
using Volo.Abp.Security.Claims;
using Volo.Abp.SettingManagement;
using Volo.Abp.SettingManagement.EntityFrameworkCore;
using Volo.Abp.Studio;
using Volo.Abp.Studio.Client.AspNetCore;
using Volo.Abp.Swashbuckle;
using Volo.Abp.TenantManagement;
using Volo.Abp.TenantManagement.EntityFrameworkCore;
using Volo.Abp.Timing;
using Volo.Abp.UI.Navigation.Urls;
using Volo.Abp.VirtualFileSystem;

namespace Dignite.Vault.Extract.Host;

[DependsOn(
    // ABP Framework packages
    typeof(AbpAspNetCoreMvcModule),
    typeof(AbpAutofacModule),
    typeof(AbpMapperlyModule),
    typeof(AbpCachingModule),
    typeof(AbpSwashbuckleModule),
    typeof(AbpAspNetCoreSerilogModule),
    typeof(AbpStudioClientAspNetCoreModule),

    // theme
    typeof(AbpAspNetCoreMvcUiLeptonXLiteThemeModule),

    // Account module packages
    typeof(AbpAccountApplicationModule),
    typeof(AbpAccountHttpApiModule),
    typeof(AbpAccountWebOpenIddictModule),

    // Identity module packages
    typeof(AbpPermissionManagementDomainIdentityModule),
    typeof(AbpPermissionManagementDomainOpenIddictModule),
    typeof(AbpIdentityApplicationModule),
    typeof(AbpIdentityHttpApiModule),

    // Permission Management module packages
    typeof(AbpPermissionManagementApplicationModule),
    typeof(AbpPermissionManagementHttpApiModule),

    // Feature Management module packages
    typeof(AbpFeatureManagementHttpApiModule),
    typeof(AbpFeatureManagementApplicationModule),

    // Setting Management module packages
    typeof(AbpSettingManagementHttpApiModule),
    typeof(AbpSettingManagementApplicationModule),

    // Tenant Management module packages
    typeof(AbpTenantManagementHttpApiModule),
    typeof(AbpTenantManagementApplicationModule),

    // Entity Framework Core packages for the used modules
    typeof(AbpAuditLoggingEntityFrameworkCoreModule),
    typeof(AbpIdentityEntityFrameworkCoreModule),
    typeof(AbpOpenIddictEntityFrameworkCoreModule),
    typeof(AbpFeatureManagementEntityFrameworkCoreModule),
    typeof(AbpPermissionManagementEntityFrameworkCoreModule),
    typeof(AbpSettingManagementEntityFrameworkCoreModule),
    typeof(AbpTenantManagementEntityFrameworkCoreModule),
    typeof(AbpBackgroundJobsEntityFrameworkCoreModule),
    typeof(AbpBlobStoringFileSystemModule),
    typeof(AbpEntityFrameworkCoreSqlServerModule),

    // Extract core modules
    typeof(VaultExtractHttpApiModule),
    typeof(VaultExtractMcpModule),          // MCP exit adapter, parallel to the HttpApi REST exit.
    typeof(VaultExtractApplicationModule),
    typeof(VaultExtractEntityFrameworkCoreModule),

    // Vault Index (P0 in progress): its own DbContext and connection string, migrated by VaultIndexHostDbContext.
    typeof(VaultIndexApplicationModule),
    typeof(VaultIndexEntityFrameworkCoreSqlServerModule),   // vectors on SQL Server 2025 / Azure SQL; brings the EF module
    typeof(VaultIndexHttpApiModule),
    typeof(VaultIndexMcpModule),                            // vault_index_* tools on the shared /mcp endpoint

    // Operator notifications (#680). Application publishes them to the uploader on document lifecycle transitions;
    // these three are what the host does with them: live push over SignalR, then a persistent inbox + the
    // /api/notification-center REST API (Dignite.NotificationCenter).
    typeof(AbpNotificationsSignalRModule),
    typeof(AbpNotificationCenterApplicationModule),
    typeof(AbpNotificationCenterHttpApiModule),
    typeof(AbpNotificationCenterEntityFrameworkCoreModule),

    // Extract infrastructure modules
    typeof(VaultParseModule),
    typeof(VaultParsePdfModule),             // PdfPig: digital-PDF text layer + embedded-image transcription (#301). Claims .pdf; coexists with ElBruno (catch-all). Omitting it makes .pdf fall back to ElBruno.
    typeof(VaultParseOpenXmlModule),         // OpenXML: PPTX (slide text/notes, #307) + DOCX (headings/tables/lists/inline formatting/hyperlinks/text boxes, #308), each with charts/tables + embedded-image transcription. Claims .pptx and .docx. REQUIRED for .pptx (ElBruno has no PresentationML converter -> empty Markdown, no OCR fallback since .pptx != .pdf); .docx degrades gracefully to ElBruno if this module is omitted.
    typeof(VaultParseElBrunoMarkItDownModule),
    typeof(VaultParseVisionLlmOcrModule)                  // Vision-LLM OCR for photos, receipts, and image PDFs; current default OCR provider (#259). IOcrProvider implementations are mutually exclusive: when switching providers, update the .csproj ProjectReference and ConfigureAI keyed vision IChatClient together. See docs/en/text-extraction/ocr-vision-llm.md.
                                                          // typeof(VaultParsePaddleOcrModule),                 // Local PaddleOCR sidecar (free CPU, PP-StructureV3); to switch back, uncomment this, comment VisionLlm, and restore its .csproj ProjectReference
                                                          // typeof(VaultParseAzureDocumentIntelligenceModule), // Cloud option (higher accuracy); when switching, also comment / enable the matching ProjectReference in .csproj
)]
public class VaultExtractHostModule : AbpModule
{
    /* Single point to enable/disable multi-tenancy */
    public const bool IsMultiTenant = true;

    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        var hostingEnvironment = context.Services.GetHostingEnvironment();
        var configuration = context.Services.GetConfiguration();

        context.Services.PreConfigure<AbpMvcDataAnnotationsLocalizationOptions>(options =>
        {
            options.AddAssemblyResource(
                typeof(VaultExtractHostResource)
            );
        });

        PreConfigure<OpenIddictBuilder>(builder =>
        {
            builder.AddValidation(options =>
            {
                // Single audience for the whole Extract API. REST and MCP are two exit adapters
                // over the same OpenIddict-Bearer resource — every token's aud is "VaultExtract",
                // regardless of how it was obtained (manual static Bearer or MCP Guided OAuth).
                options.AddAudiences("VaultExtract");
                options.UseLocalServer();
                options.UseAspNetCore();
            });
        });

        // MCP Guided-OAuth clients (RFC 9728 + RFC 8707) MUST send a `resource` parameter naming
        // the MCP server's canonical URI on every /authorize and /token request (docs/en/egress/mcp-server.md).
        // OpenIddict's default resource handling would reject that parameter two ways:
        //   - ValidateResources         → ID2190 (invalid_target) unless the URI is pre-registered;
        //   - ValidateResourcePermissions → ID2192 unless the client carries an `rsrc:<uri>` grant.
        // Extract is a SINGLE protected resource (audience "VaultExtract"); REST and MCP are just two
        // exit adapters over it. The token aud is derived from the granted scope's resources during
        // sign-in, NOT from this request parameter — verified by decompiling OpenIddict 7.2.0:
        // nothing in OpenIddictServerHandlers narrows the principal's resources by the request
        // `resource`, and ABP's authorize controller resolves resources from scopes alone. So the
        // resource indicator carries no isolation value here and there is no "other resource" to gate
        // against. We therefore turn OFF both resource gates (accept-but-ignore the parameter) rather
        // than minting the MCP URL as a first-class resource, which would scatter URL coupling across
        // scope resources, validation audiences, and per-client permissions. Both gates only fire for
        // requests that actually carry a `resource` param — the Angular / Swagger clients never do, so
        // this is a no-op for them. IgnoreResourcePermissions is flagged "NOT recommended" by
        // OpenIddict for the general multi-resource case; it is the correct, minimal choice for a
        // single-resource server. If Extract ever becomes multi-resource, revisit (re-enable the
        // gates, RegisterResources per resource, and seed rsrc: grants per client).
        PreConfigure<OpenIddictServerBuilder>(serverBuilder =>
        {
            serverBuilder.DisableResourceValidation();
            serverBuilder.IgnoreResourcePermissions();
        });

        if (!hostingEnvironment.IsDevelopment())
        {
            PreConfigure<AbpOpenIddictAspNetCoreOptions>(options =>
            {
                options.AddDevelopmentEncryptionAndSigningCertificate = false;
            });

            PreConfigure<OpenIddictServerBuilder>(serverBuilder =>
            {
                serverBuilder.AddProductionEncryptionAndSigningCertificate("openiddict.pfx", configuration["AuthServer:CertificatePassPhrase"]!);
            });
        }

        VaultExtractHostGlobalFeatureConfigurator.Configure();
        VaultExtractHostModuleExtensionConfigurator.Configure();
        VaultExtractHostEfCoreEntityExtensionMappings.Configure();
    }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var hostingEnvironment = context.Services.GetHostingEnvironment();
        var configuration = context.Services.GetConfiguration();

        if (!hostingEnvironment.IsProduction())
        {
            Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = true;
            Microsoft.IdentityModel.Logging.IdentityModelEventSource.LogCompleteSecurityArtifact = true;
        }

        if (!configuration.GetValue<bool>("AuthServer:RequireHttpsMetadata"))
        {
            Configure<OpenIddictServerAspNetCoreOptions>(options =>
            {
                options.DisableTransportSecurityRequirement = true;
            });
        }

        // #469: when explicitly enabled, restore the originating client IP before /mcp rate limiting
        // so clients behind the same reverse proxy do not collapse into one partition. The MCP module's
        // registration fails closed unless the deployment declares trusted proxy IPs or CIDR networks.
        // Forwarded headers remain disabled for client IPs by default; UseForwardedHeaders is already the
        // first middleware in OnApplicationInitialization.
        context.Services.AddVaultExtractMcpForwardedClientIp(
            configuration.GetSection("Mcp:ForwardedClientIp"),
            includeForwardedProto: !configuration.GetValue<bool>("AuthServer:RequireHttpsMetadata"));

        if (hostingEnvironment.IsDevelopment())
        {
            context.Services.Replace(ServiceDescriptor.Singleton<IEmailSender, NullEmailSender>());
        }

        Configure<AbpClockOptions>(options =>
        {
            options.Kind = DateTimeKind.Utc;
        });

        ConfigureStudio(hostingEnvironment);
        ConfigureAuthentication(context);
        ConfigureBundles(hostingEnvironment);
        ConfigureMultiTenancy();
        ConfigureUrls(configuration);
        ConfigureHealthChecks(context);
        ConfigureSwagger(context.Services, configuration);
        ConfigureAutoApiControllers();
        ConfigureLocalization();
        ConfigureCors(context, configuration);
        ConfigureDataProtection(context);
        ConfigureVirtualFiles(hostingEnvironment);
        ConfigureEfCore(context);
        ConfigureDistributedEventBus();
        ConfigureAI(context, configuration);
        ConfigureVaultIndexAI(context, configuration);
        ConfigureOpenTelemetry(context, configuration);
        ConfigureRequestLimits(context);
        ConfigureMcpRateLimiter(context);
        ConfigureMcpServer();
    }

    // #433: rate-limit the /mcp egress endpoint (a DoS / abuse backstop against unauthenticated probing).
    // The mechanism (per-IP fixed-window policy, 429 rejection) lives in the Mcp egress module
    // (AddVaultExtractMcpRateLimiter); the host owns the config and the pipeline wiring. Enabled by default
    // with generous limits (Mcp:RateLimit), applied to /mcp via RequireRateLimiting in
    // OnApplicationInitialization so it also covers the #278 discovery-401 path. The limiter never fires for
    // legitimate, in-limit MCP session traffic.
    /// <summary>
    /// This deployment's settings for the shared MCP server (Dignite.Abp.AspNetCore.Mcp).
    /// </summary>
    private void ConfigureMcpServer()
    {
        Configure<AbpMcpServerOptions>(options =>
        {
            // #433: scopes the /mcp rate limiter to this endpoint, covering the #278 discovery-401 path and
            // all /mcp traffic in one place.
            options.EndpointConventions.Add(endpoint => endpoint.RequireRateLimiting(McpRateLimiterDefaults.PolicyName));
        });
    }

    private void ConfigureMcpRateLimiter(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();
        context.Services.AddVaultExtractMcpRateLimiter(options =>
        {
            configuration.GetSection("Mcp:RateLimit").Bind(options);
        });
    }

    // #221: upload request body limits as Kestrel / Form-layer backstop. The real fail-closed friendly error lives in
    // DocumentAppService.UploadAsync, which validates against DocumentConsts.MaxUploadFileBytes and throws BusinessException.
    // Leave multipart envelope headroom here (+1 MiB), so a normal max-size file is not cut off by 413 before reaching the application layer,
    // while converging ASP.NET Core's implicit defaults (~28.6 MB Kestrel / 128 MB Form) into an explicit, auditable boundary.
    private static void ConfigureRequestLimits(ServiceConfigurationContext context)
    {
        var bodyLimit = DocumentConsts.MaxUploadFileBytes + 1024 * 1024;

        context.Services.Configure<KestrelServerOptions>(options =>
        {
            options.Limits.MaxRequestBodySize = bodyLimit;
        });

        context.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = bodyLimit;
        });
    }

    // Enable ABP transactional outbox + inbox (issue #188):
    // - publish paths run inside the caller UoW, so events are written to AbpEventOutbox atomically with business changes
    // - a background worker scans the table and actually delivers to the message broker, guaranteeing at-least-once delivery
    // Downstream consumers handle redelivery idempotently by (DocumentId, EventType, EventTime).
    private void ConfigureDistributedEventBus()
    {
        Configure<AbpDistributedEventBusOptions>(options =>
        {
            options.Outboxes.Configure(config =>
            {
                config.UseDbContext<Data.VaultExtractHostDbContext>();
            });

            options.Inboxes.Configure(config =>
            {
                config.UseDbContext<Data.VaultExtractHostDbContext>();
            });
        });
    }

    private void ConfigureHealthChecks(ServiceConfigurationContext context)
    {
        context.Services.AddVaultExtractHealthChecks();
    }

    private void ConfigureStudio(IHostEnvironment hostingEnvironment)
    {
        if (hostingEnvironment.IsProduction())
        {
            Configure<AbpStudioClientOptions>(options =>
            {
                options.IsLinkEnabled = false;
            });
        }
    }

    private void ConfigureAuthentication(ServiceConfigurationContext context)
    {
        // Bearer requests route to OpenIddict validation; everything else falls back to the application
        // cookie (browser / MVC Account). The /mcp endpoint keeps its bare scheme-free RequireAuthorization()
        // (the #278 invariant), so the dynamic-claims-enriched principal is not dropped by re-authentication.
        context.Services.ForwardIdentityAuthenticationForBearer(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

        context.Services.Configure<AbpClaimsPrincipalFactoryOptions>(options =>
        {
            options.IsDynamicClaimsEnabled = true;
        });

        ConfigureMcpAuthentication(context);
    }

    // #278: add OAuth Protected Resource Metadata discovery for the /mcp export endpoint (RFC 9728). This is additive
    // and does not break existing manual token paths. The McpAuth scheme has only two responsibilities in this host, neither involving token validation:
    //   1. Self-serve `/.well-known/oauth-protected-resource`. McpAuthenticationHandler implements
    //      IAuthenticationRequestHandler and directly returns metadata JSON for that path during UseAuthentication(),
    //      without relying on any authorization policy and without separately mapping an endpoint.
    //   2. Provide 401 challenge by injecting the `WWW-Authenticate: Bearer resource_metadata="..."` pointer.
    // Note that McpAuth must not enter the /mcp endpoint authorization policy AuthenticationSchemes. Otherwise PolicyEvaluator would
    // re-authenticate and lose the principal enriched by UseDynamicClaims. Dignite.Abp.AspNetCore.Mcp's
    // AbpMcpAuthorizationMiddlewareResultHandler triggers the challenge only for its /mcp endpoint; see that handler's comments.
    // Token validation, dynamic claims, and tenant resolution all still use the endpoint default policy + existing OpenIddict chain.
    // Manual token paths (mcp-remote static Bearer / Inspector manual token / password-grant) are unaffected; discovery triggers only on 401 without token.
    private void ConfigureMcpAuthentication(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();
        var authority = configuration["AuthServer:Authority"];
        if (string.IsNullOrWhiteSpace(authority))
        {
            // Without authority, there is no authorization server to point to and discovery is meaningless.
            // Do not register half-complete metadata or take over challenge. /mcp remains protected by RequireAuthorization,
            // and manual token paths are unaffected.
            return;
        }

        var selfUrl = configuration["App:SelfUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(selfUrl))
        {
            // Same reasoning as the authority guard: the metadata must name this server's own URI, and
            // deriving it from the request's Host header instead is not something to do silently.
            return;
        }

        // #422: the discovery mechanics (McpAuth scheme registration, login-page hiding, challenge-only
        // result-handler replacement) are owned by the shared MCP server module, Dignite.Abp.AspNetCore.Mcp
        // (AddAbpMcpAuthenticationDiscovery). The host keeps only the deployment-specific decisions: whether
        // discovery is enabled (the guards above) and the ProtectedResourceMetadata values below.
        context.Services.AddAbpMcpAuthenticationDiscovery(metadata =>
        {
            // RFC 9728 `resource`: the MCP server's canonical URI. The MCP authorization spec
            // says a client SHOULD use the most-specific URI it can, so we advertise the full
            // /mcp endpoint (not the bare host origin). The client echoes this back as the
            // RFC 8707 `resource` parameter on /authorize + /token; OpenIddict accepts-but-
            // ignores it because both resource gates are turned off in PreConfigureServices
            // (see the rationale there), so the issued token's aud stays "VaultExtract". Set
            // explicitly rather than left to handler inference so it remains correct behind a
            // reverse proxy (inference derives it from request host/scheme headers).
            metadata.Resource = $"{selfUrl}/mcp";
            metadata.AuthorizationServers = new List<string> { authority };
            metadata.ScopesSupported = new List<string> { "VaultExtract" };
            metadata.BearerMethodsSupported = new List<string> { "header" };
            metadata.ResourceName = "Extract MCP";
        });
    }

    private void ConfigureBundles(IHostEnvironment hostingEnvironment)
    {
        Configure<AbpBundlingOptions>(options =>
        {
            options.StyleBundles.Configure(
                LeptonXLiteThemeBundles.Styles.Global,
                bundle =>
                {
                    bundle.AddFiles("/global-styles.css");
                }
            );

            options.ScriptBundles.Configure(
                LeptonXLiteThemeBundles.Scripts.Global,
                bundle =>
                {
                    bundle.AddFiles("/global-scripts.js");
                    if (hostingEnvironment.IsDevelopment())
                    {
                        bundle.AddFiles("/dev-login-helper.js");
                    }
                }
            );
        });
    }

    private void ConfigureMultiTenancy()
    {
        Configure<AbpMultiTenancyOptions>(options =>
        {
            options.IsEnabled = IsMultiTenant;
        });
    }

    private void ConfigureUrls(IConfiguration configuration)
    {
        Configure<AppUrlOptions>(options =>
        {
            options.Applications["MVC"].RootUrl = configuration["App:SelfUrl"];
            options.RedirectAllowedUrls.AddRange(configuration["App:RedirectAllowedUrls"]?.Split(',') ?? Array.Empty<string>());

            options.Applications["Angular"].RootUrl = configuration["App:ClientUrl"];
            options.Applications["Angular"].Urls[AccountUrlNames.PasswordReset] = "account/reset-password";
        });
    }

    private void ConfigureLocalization()
    {
        Configure<AbpLocalizationOptions>(options =>
        {
            options.Resources
                .Add<VaultExtractHostResource>("en")
                .AddBaseTypes(typeof(VaultExtractResource))
                .AddVirtualJson("/Localization/ExtractHost");

            options.DefaultResourceType = typeof(VaultExtractHostResource);

            options.Languages.Add(new LanguageInfo("en", "en", "English"));
            options.Languages.Add(new LanguageInfo("zh-Hans", "zh-Hans", "Chinese (Simplified)"));
            options.Languages.Add(new LanguageInfo("zh-Hant", "zh-Hant", "Chinese (Traditional)"));
            options.Languages.Add(new LanguageInfo("ja", "ja", "Japanese"));
        });

    }

    private void ConfigureAutoApiControllers()
    {
        Configure<AbpAspNetCoreMvcOptions>(options =>
        {
            options.ConventionalControllers.Create(typeof(VaultExtractHostModule).Assembly);
        });
    }

    private void ConfigureSwagger(IServiceCollection services, IConfiguration configuration)
    {
        services.AddAbpSwaggerGenWithOAuth(
            configuration["AuthServer:Authority"]!,
            new Dictionary<string, string>
            {
                {"VaultExtract", "Extract API"}
            },
            options =>
            {
                options.SwaggerDoc("v1", new OpenApiInfo { Title = "Extract API", Version = "v1" });
                options.DocInclusionPredicate((docName, description) => true);
                options.CustomSchemaIds(type => type.FullName);
            });
    }

    private void ConfigureCors(ServiceConfigurationContext context, IConfiguration configuration)
    {
        context.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(builder =>
            {
                builder
                    .WithOrigins(
                        configuration["App:CorsOrigins"]?
                            .Split(",", StringSplitOptions.RemoveEmptyEntries)
                            .Select(o => o.RemovePostFix("/"))
                            .ToArray() ?? Array.Empty<string>()
                    )
                    .WithAbpExposedHeaders()
                    .SetIsOriginAllowedToAllowWildcardSubdomains()
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
            });
        });
    }

    private void ConfigureDataProtection(ServiceConfigurationContext context)
    {
        context.Services.AddDataProtection().SetApplicationName("VaultExtract");
    }

    private void ConfigureVirtualFiles(IWebHostEnvironment hostingEnvironment)
    {
        Configure<AbpVirtualFileSystemOptions>(options =>
        {
            options.FileSets.AddEmbedded<VaultExtractHostModule>();
            if (hostingEnvironment.IsDevelopment())
            {
                /* Using physical files in development, so we don't need to recompile on changes */
                options.FileSets.ReplaceEmbeddedByPhysical<VaultExtractHostModule>(hostingEnvironment.ContentRootPath);
            }
        });
    }

    private void ConfigureEfCore(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<Data.VaultExtractHostDbContext>(options =>
        {
            options.AddDefaultRepositories(includeAllEntities: true);
        });

        // Migrations only (see VaultIndexHostDbContext); Index's repositories live on its own VaultIndexDbContext.
        context.Services.AddAbpDbContext<Data.VaultIndexHostDbContext>();

        Configure<AbpDbContextOptions>(options =>
        {
            options.Configure(configurationContext =>
            {
                // Issue #206: after removing native JSON columns, SQL Server 2025 compatibility level 170 is no longer required.
                // The persistence layer now uses ordinary relational columns only (typed columns + nvarchar(max)), portable across
                // SQL Server versions and relational databases. Deployment targets are no longer locked to SQL Server 2025.
                configurationContext.UseSqlServer();
            });
        });

        var hostingEnvironment = context.Services.GetHostingEnvironment();
        Configure<AbpBlobStoringOptions>(options =>
        {
            options.Containers.ConfigureDefault(container =>
            {
                container.UseFileSystem(fileSystem =>
                {
                    fileSystem.BasePath = Path.Combine(
                        hostingEnvironment.ContentRootPath, "App_Data", "blobs");
                });
            });
        });
    }

    private void ConfigureAI(ServiceConfigurationContext context, IConfiguration configuration)
    {
        // Classification and field extraction have no non-LLM fallback, so a provider is mandatory.
        // Fail fast at startup instead of letting documents silently fail provider auth on the first
        // pipeline call. The committed appsettings.json ships the "YOUR_API_KEY" placeholder; a real
        // provider must come from appsettings.Development.json / user-secrets / env vars. Hosts that
        // target a non-OpenAI wire protocol replace this whole method (see docs/en/configuration/ai-provider.md) and
        // own their own validation.
        var endpoint = configuration["Vault:Extract:Endpoint"];
        var apiKey = configuration["Vault:Extract:ApiKey"];
        var chatModelId = configuration["Vault:Extract:ChatModelId"];
        if (string.IsNullOrWhiteSpace(endpoint)
            || string.IsNullOrWhiteSpace(apiKey)
            || apiKey == "YOUR_API_KEY"
            || string.IsNullOrWhiteSpace(chatModelId))
        {
            throw new AbpException(
                "Extract requires an LLM provider before it can start: document classification and " +
                "field extraction have no non-LLM fallback. Set Vault:Extract:Endpoint, Vault:Extract:ApiKey, " +
                "and Vault:Extract:ChatModelId in src/Dignite.Vault.Extract.Host/appsettings.secrets.json (git-ignored) " +
                "or environment variables. For a zero-cost local option, point Endpoint at a " +
                "local Ollama /v1 endpoint and use any non-empty token as the key. See docs/en/configuration/ai-provider.md.");
        }

        // Every chat client below talks to one provider through one named HttpClient, so its retries, circuit breaker and
        // concurrency limit are shared: a provider that throttles cools down for all of them at once.
        AddProviderHttpClient(
            context,
            LlmHttpClientName,
            configuration.GetSection("Vault:Resilience:Llm"),
            options =>
            {
                // Field extraction sends a whole document with its full field schema; one answer can take minutes.
                options.AttemptTimeout.Timeout = TimeSpan.FromMinutes(5);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(15);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(10);
                options.RateLimiter.DefaultRateLimiterOptions.PermitLimit = 16;
                options.RateLimiter.DefaultRateLimiterOptions.QueueLimit = 256;
            },
            // Provider quirk, opt-in: a gateway that ignores max_completion_tokens (SiliconFlow) needs the cap sent as
            // max_tokens. Never on genuine OpenAI, whose reasoning models refuse max_tokens with a 400.
            configuration.GetValue("Vault:Extract:UseLegacyMaxTokens", defaultValue: false)
                ? () => new MaxCompletionTokensToMaxTokensHandler()
                : null);
        context.Services.AddKeyedSingleton(
            LlmHttpClientName,
            (serviceProvider, _) => CreateOpenAIClient(serviceProvider, LlmHttpClientName, endpoint, apiKey));

        // A reasoning model spends reasoning tokens out of the same output cap the callers size for their visible
        // output (field extraction's schema estimate, Vision OCR's MaxOutputTokens), so a deployment on one sets
        // the effort here. Unset sends nothing: a provider without reasoning may reject the parameter.
        var reasoningEffort = ReadReasoningEffort(configuration);
        ChatClientBuilder Decorate(ChatClientBuilder builder)
        {
            if (reasoningEffort is { } effort)
            {
                // Outermost, so the telemetry span records the effort actually sent. A caller's own value wins.
                builder.ConfigureOptions(options => options.Reasoning ??= new ReasoningOptions { Effort = effort });
            }

            return builder.UseOpenTelemetry().UseLogging();
        }

        // Title-generator chat client: single-shot, tool-free, prompt-unique-per-call so
        // distributed caching is a net negative. Consumed by
        // DocumentParseBackgroundJob.TryGenerateTitleAsync and SlugSuggestionAppService via
        // [FromKeyedServices(VaultExtractConsts.TitleGeneratorChatClientKey)]. Falls back
        // to ChatModelId when TitleGeneratorModelId is unset; hosts that want to cut cost
        // can point this at a small fast model (e.g. Qwen3-8B) — it must still honour
        // ChatResponseFormat.ForJsonSchema for the slug call to keep working.
        var titleGeneratorModelId = configuration["Vault:Extract:TitleGeneratorModelId"]
            ?? chatModelId;
        Decorate(context.Services.AddKeyedChatClient(
            VaultExtractConsts.TitleGeneratorChatClientKey,
            serviceProvider => serviceProvider.GetRequiredKeyedService<OpenAIClient>(LlmHttpClientName).GetChatClient(titleGeneratorModelId).AsIChatClient()));

        // Structured-output chat client: shared by classification (GetResponseAsync<T>) plus
        // direct JSON-schema callers such as field extraction. All are tool-free and
        // prompts are document/admin-input-derived (unique per call), so
        // FunctionInvocation and DistributedCache are pure overhead. OTel + Logging stay
        // so each structured call shows up as a clean chat <model> span. Falls back to
        // ChatModelId when StructuredModelId is unset; this is the accuracy-sensitive
        // link in the pipeline (mis-classification / mis-extraction is user-visible), so
        // keep it on a strong model rather than trimming cost here.
        var structuredModelId = configuration["Vault:Extract:StructuredModelId"]
            ?? chatModelId;
        Decorate(context.Services.AddKeyedChatClient(
            VaultExtractConsts.StructuredChatClientKey,
            serviceProvider => serviceProvider.GetRequiredKeyedService<OpenAIClient>(LlmHttpClientName).GetChatClient(structuredModelId).AsIChatClient()));

        // Vision OCR chat client: consumed by VisionLlmOcrProvider via
        // [FromKeyedServices(VisionLlmOcrConsts.VisionChatClientKey)] to transcribe photos / thermal
        // receipts / image-only PDFs that layout OCR (PP-StructureV3) fails on (#259). Reuses the same
        // OpenAIClient (endpoint/key) but REQUIRES an explicit vision-capable model id — it must NOT fall
        // back to ChatModelId, because the main chat model (e.g. DeepSeek-V3) may have no vision support.
        // Fail fast if unset, mirroring the provider-mandatory check above. Only wired because VisionLlm is
        // the enabled OCR provider; a host that switches back to PaddleOCR/Azure can drop this block.
        var visionOcrModelId = configuration["Vault:Extract:VisionOcrModelId"];
        if (string.IsNullOrWhiteSpace(visionOcrModelId))
        {
            throw new AbpException(
                "VisionLlm is the configured OCR provider but Vault:Extract:VisionOcrModelId is not set. " +
                "Point it at a vision-capable model (e.g. Qwen/Qwen3-VL-8B-Instruct on SiliconFlow) in " +
                "src/Dignite.Vault.Extract.Host/appsettings.secrets.json (git-ignored) or environment variables. " +
                "See docs/en/text-extraction/ocr-vision-llm.md.");
        }
        Decorate(context.Services.AddKeyedChatClient(
            VisionLlmOcrConsts.VisionChatClientKey,
            serviceProvider => serviceProvider.GetRequiredKeyedService<OpenAIClient>(LlmHttpClientName).GetChatClient(visionOcrModelId).AsIChatClient()));

        // Vault Index's chat model: chunk enrichment (keywords, answerable questions) and section and document
        // summaries, consumed through VaultIndexConsts.ChatClientKey when an index profile switches them on. Optional,
        // and with no fallback to Vault:Extract:ChatModelId: Index calls it for every document of those types, so a
        // deployment opts in with a model it chose for that volume (a small instruct model will do). Without it,
        // profiles' enrichment and summaries are saved but not applied. Same endpoint and key as Extract's models.
        var indexChatModelId = configuration["Vault:Index:ChatModelId"];
        if (!string.IsNullOrWhiteSpace(indexChatModelId))
        {
            Decorate(context.Services.AddKeyedChatClient(
                VaultIndexConsts.ChatClientKey,
                serviceProvider => serviceProvider.GetRequiredKeyedService<OpenAIClient>(LlmHttpClientName).GetChatClient(indexChatModelId).AsIChatClient()));
        }
    }

    /// <summary>
    /// Vault Index's embedding model, under <see cref="VaultIndexConsts.EmbeddingGeneratorKey"/>. Optional while Index is
    /// under development: without <c>Vault:Index:EmbeddingModelId</c> nothing is registered, the host starts, and Index
    /// indexes nothing until a model is added. Uses Extract's endpoint and key unless Index has
    /// its own. <c>Vault:Index:EmbeddingDimensions</c> is sent only when set: OpenAI's text-embedding-3 models shorten
    /// their vectors to it, while a fixed-size model such as BAAI/bge-m3 rejects the parameter (HTTP 400 on
    /// SiliconFlow). Either way the vectors must match <see cref="Data.VaultIndexHostDbContext.VectorDimensions"/>.
    /// <para>
    /// While the index is rebuilt for a new model, the one it was built with can stay registered under
    /// <see cref="VaultIndexConsts.PreviousEmbeddingGeneratorKey"/> from <c>Vault:Index:PreviousEmbedding*</c>, so search
    /// keeps working until the new generation takes over. Its endpoint and key default to the current model's.
    /// </para>
    /// </summary>
    private static void ConfigureVaultIndexAI(ServiceConfigurationContext context, IConfiguration configuration)
    {
        context.Services.Configure<VaultIndexSqlServerOptions>(options =>
            options.VectorDimensions = Data.VaultIndexHostDbContext.VectorDimensions);

        ConfigureVaultIndexReranker(context, configuration);

        var modelId = configuration["Vault:Index:EmbeddingModelId"];
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        var endpoint = configuration["Vault:Index:EmbeddingEndpoint"] is { Length: > 0 } ownEndpoint
            ? ownEndpoint
            : configuration["Vault:Extract:Endpoint"]!;
        var apiKey = configuration["Vault:Index:EmbeddingApiKey"] is { Length: > 0 } ownKey
            ? ownKey
            : configuration["Vault:Extract:ApiKey"]!;
        AddVaultIndexEmbeddingGenerator(
            context, VaultIndexConsts.EmbeddingGeneratorKey, modelId, endpoint, apiKey, configuration["Vault:Index:EmbeddingDimensions"]);

        var previousModelId = configuration["Vault:Index:PreviousEmbeddingModelId"];
        if (!string.IsNullOrWhiteSpace(previousModelId))
        {
            AddVaultIndexEmbeddingGenerator(
                context,
                VaultIndexConsts.PreviousEmbeddingGeneratorKey,
                previousModelId,
                configuration["Vault:Index:PreviousEmbeddingEndpoint"] is { Length: > 0 } previousEndpoint ? previousEndpoint : endpoint,
                configuration["Vault:Index:PreviousEmbeddingApiKey"] is { Length: > 0 } previousKey ? previousKey : apiKey,
                configuration["Vault:Index:PreviousEmbeddingDimensions"]);
        }
    }

    private static void AddVaultIndexEmbeddingGenerator(
        ServiceConfigurationContext context,
        string key,
        string modelId,
        string endpoint,
        string apiKey,
        string? dimensionsSetting)
    {
        int? dimensions = int.TryParse(dimensionsSetting, out var parsed) ? parsed : null;

        // Short requests, but the same throttling as the chat models: a TPM-limited provider refuses bursts during
        // ingestion, and the retries have to outlast its one-minute window.
        var httpClientName = "Vault." + key;
        AddProviderHttpClient(
            context,
            httpClientName,
            context.Services.GetConfiguration().GetSection("Vault:Resilience:Embedding"),
            options =>
            {
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(2);
            });

        context.Services.AddKeyedEmbeddingGenerator(
                key,
                serviceProvider => CreateOpenAIClient(serviceProvider, httpClientName, endpoint, apiKey)
                    .GetEmbeddingClient(modelId)
                    .AsIEmbeddingGenerator(dimensions))
            .UseOpenTelemetry()
            .UseLogging();
    }

    /// <summary>
    /// Vault Index's reranker, from <c>Vault:Index:RerankModelId</c>, over the Cohere / Jina rerank API (SiliconFlow,
    /// Jina, Cohere, vLLM; OpenAI has none). Optional: without it search keeps the fused order. Uses Extract's endpoint and
    /// key unless Index has its own.
    /// </summary>
    private static void ConfigureVaultIndexReranker(ServiceConfigurationContext context, IConfiguration configuration)
    {
        var modelId = configuration["Vault:Index:RerankModelId"];
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        var reranker = context.Services.AddVaultIndexHttpReranker(options =>
        {
            options.ModelId = modelId;
            options.Endpoint = configuration["Vault:Index:RerankEndpoint"] is { Length: > 0 } ownEndpoint
                ? ownEndpoint
                : configuration["Vault:Extract:Endpoint"]!;
            options.ApiKey = configuration["Vault:Index:RerankApiKey"] is { Length: > 0 } ownKey
                ? ownKey
                : configuration["Vault:Extract:ApiKey"]!;
        });

        // A search waits for the reranker and falls back to the fused order when it fails, so one quick retry at most,
        // all inside HttpRerankerOptions.Timeout (15 s).
        OverrideFrom(
            reranker.AddStandardResilienceHandler()
                .Configure(options =>
                {
                    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(6);
                    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(13);
                    options.Retry.MaxRetryAttempts = 1;
                    options.Retry.Delay = TimeSpan.FromMilliseconds(500);
                    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                }),
            configuration.GetSection("Vault:Resilience:Rerank"));
    }

    /// <summary>
    /// Applies a configuration section over the code defaults. A missing section keeps the defaults: the library's own
    /// <c>Configure(section)</c> refuses a section that does not exist.
    /// </summary>
    private static void OverrideFrom(IHttpStandardResiliencePipelineBuilder pipeline, IConfigurationSection section)
    {
        if (section.Exists())
        {
            pipeline.Configure(section);
        }
    }

    /// <summary>The named HttpClient behind every chat client of the configured provider.</summary>
    private const string LlmHttpClientName = "Vault.Llm";

    /// <summary>
    /// A named HttpClient for one model provider with <c>Microsoft.Extensions.Http.Resilience</c>'s standard pipeline:
    /// a concurrency limiter, a total timeout, retries that honour <c>Retry-After</c>, a circuit breaker and a timeout per
    /// attempt. The pipeline is the only timeout and retry authority: <c>HttpClient.Timeout</c> is off here and the OpenAI
    /// SDK's own retries are off in <see cref="CreateOpenAIClient"/>, since two retry layers multiply the attempts.
    /// <para>
    /// The defaults retry five times from 5 s, doubling with jitter up to 2 min: a TPM-limited provider refills over a
    /// one-minute window, so the retries span about two and a half windows instead of giving up inside the first (the
    /// same reasoning as RAGFlow's embedder). <paramref name="defaults"/> sets the per-provider timeouts, and the
    /// configuration <paramref name="section"/>, in <c>HttpStandardResilienceOptions</c>' own names, overrides both.
    /// </para>
    /// <para>
    /// The OpenAI SDK keeps the client for the life of the process, so the handler is never rotated and the primary
    /// handler bounds <c>PooledConnectionLifetime</c>: a NAT or proxy that silently drops an idle pooled connection would
    /// otherwise make every later request on it hang until the timeout, which is what happened before this was bounded.
    /// </para>
    /// </summary>
    private static void AddProviderHttpClient(
        ServiceConfigurationContext context,
        string name,
        IConfigurationSection section,
        Action<HttpStandardResilienceOptions> defaults,
        Func<DelegatingHandler>? requestRewriter = null)
    {
        var builder = context.Services
            .AddHttpClient(name, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        if (requestRewriter is not null)
        {
            // Outside the resilience handler, so a request is rewritten once however often it is retried.
            builder.AddHttpMessageHandler(requestRewriter);
        }

        OverrideFrom(
            builder.AddStandardResilienceHandler()
                .Configure(options =>
                {
                    options.Retry.MaxRetryAttempts = 5;
                    options.Retry.Delay = TimeSpan.FromSeconds(5);
                    options.Retry.MaxDelay = TimeSpan.FromMinutes(2);
                    options.CircuitBreaker.MinimumThroughput = 10;
                    options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
                    defaults(options);
                }),
            section);
    }

    private static OpenAIClient CreateOpenAIClient(IServiceProvider serviceProvider, string httpClientName, string endpoint, string apiKey)
        => new(
            new System.ClientModel.ApiKeyCredential(apiKey),
            new OpenAIClientOptions
            {
                Endpoint = new Uri(endpoint),
                Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(
                    serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(httpClientName)),
                RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(maxRetries: 0),
                NetworkTimeout = Timeout.InfiniteTimeSpan
            });

    private static ReasoningEffort? ReadReasoningEffort(IConfiguration configuration)
    {
        var value = configuration["Vault:Extract:ReasoningEffort"];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // IsDefined rejects a numeric string, which TryParse would accept as any int.
        if (Enum.TryParse<ReasoningEffort>(value, ignoreCase: true, out var effort) && Enum.IsDefined(effort))
        {
            return effort;
        }

        throw new AbpException(
            $"Vault:Extract:ReasoningEffort must be one of {string.Join(", ", Enum.GetNames<ReasoningEffort>())}, " +
            $"or empty to send no reasoning effort; got '{value}'. See docs/en/configuration/ai-provider.md.");
    }

    /// <summary>
    /// Rewrites <c>max_completion_tokens</c> to <c>max_tokens</c> in every outgoing chat-completions request body.
    /// <para>
    /// <b>Why this exists</b>: the <c>OpenAI</c> SDK (2.14.0, pulled in by <c>Microsoft.Extensions.AI.OpenAI</c>)
    /// unconditionally serializes <see cref="ChatOptions.MaxOutputTokens"/> / <c>ChatCompletionOptions.MaxOutputTokenCount</c>
    /// as OpenAI's current <c>max_completion_tokens</c> field, which replaced the legacy <c>max_tokens</c> field for
    /// chat completions. SiliconFlow's OpenAI-compatible endpoint does not recognize <c>max_completion_tokens</c> —
    /// it silently ignores the field and falls back to its own undocumented default output cap (observed at ~4096
    /// tokens), no matter how large a value the caller requests. Confirmed directly: capturing the wire request body
    /// showed <c>"max_completion_tokens":500</c> being sent and having zero effect (a 500-cap request generated 2000+
    /// tokens), while an identical request with <c>"max_tokens":500</c> instead cut generation at exactly 500 tokens
    /// with <c>finish_reason: "length"</c>. Every previous attempt to fix field-extraction truncation by sizing
    /// <see cref="ChatOptions.MaxOutputTokens"/> more generously (see <c>FieldExtractionWorkflow.EstimateMaxOutputTokens</c>)
    /// was therefore computing a correct number that never reached the provider — this handler is the actual fix;
    /// the sizing logic still matters once this rewrite makes <c>MaxOutputTokens</c> take effect at all.
    /// </para>
    /// <para>
    /// Installed only when <c>Vault:Extract:UseLegacyMaxTokens</c> is <c>true</c>, which a SiliconFlow deployment
    /// sets. It must stay off for genuine OpenAI and Azure OpenAI, the shipped default: they understand
    /// <c>max_completion_tokens</c>, and their reasoning models refuse <c>max_tokens</c> with a 400.
    /// </para>
    /// </summary>
    private sealed class MaxCompletionTokensToMaxTokensHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null &&
                request.Content.Headers.ContentType?.MediaType == "application/json")
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                if (body.Contains("\"max_completion_tokens\"", StringComparison.Ordinal))
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(body)!.AsObject();
                    if (node.Remove("max_completion_tokens", out var value))
                    {
                        node["max_tokens"] = value;
                        var rewritten = node.ToJsonString();
                        var content = new StringContent(rewritten, System.Text.Encoding.UTF8, "application/json");
                        request.Content = content;
                    }
                }
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    // OTel export pipeline. Microsoft.Extensions.AI (gen_ai.* spans from the chat-client UseOpenTelemetry
    // decorators above) emits signals. Without an exporter wired here they are silently dropped.
    private void ConfigureOpenTelemetry(ServiceConfigurationContext context, IConfiguration configuration)
    {
        var section = configuration.GetSection("OpenTelemetry");
        if (!section.GetValue("Enabled", defaultValue: false))
        {
            return;
        }

        var endpointValue = section["Otlp:Endpoint"];
        var useOtlp = !string.IsNullOrWhiteSpace(endpointValue);
        var useConsole = section.GetValue("ConsoleExporter", defaultValue: false);

        var serviceVersion = typeof(VaultExtractHostModule).Assembly.GetName().Version?.ToString() ?? "1.0.0";

        var otel = context.Services.AddOpenTelemetry();

        otel.ConfigureResource(resource => resource
            .AddService(serviceName: "Dignite.Vault.Extract", serviceVersion: serviceVersion));

        otel.WithTracing(tracing =>
        {
            tracing
                .AddSource("Experimental.Microsoft.Extensions.AI")
                .AddSource("Microsoft.Extensions.AI")
                .AddSource("Dignite.Vault.Extract.*")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation();

            if (useOtlp)
            {
                tracing.AddOtlpExporter(o => ConfigureOtlpExporter(o, section));
            }

            if (useConsole)
            {
                tracing.AddConsoleExporter();
            }
        });

        otel.WithMetrics(metrics =>
        {
            metrics
                .AddMeter("Dignite.Vault.Extract.*")
                .AddMeter("Experimental.Microsoft.Extensions.AI")
                .AddMeter("Microsoft.Extensions.AI")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation();

            if (useOtlp)
            {
                metrics.AddOtlpExporter(o => ConfigureOtlpExporter(o, section));
            }

            if (useConsole)
            {
                metrics.AddConsoleExporter();
            }
        });
    }

    private static void ConfigureOtlpExporter(
        OpenTelemetry.Exporter.OtlpExporterOptions options,
        IConfigurationSection section)
    {
        var endpoint = section["Otlp:Endpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            options.Endpoint = new Uri(endpoint);
        }

        var protocol = section["Otlp:Protocol"];
        if (string.Equals(protocol, "HttpProtobuf", StringComparison.OrdinalIgnoreCase))
        {
            options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
        }
        else if (string.Equals(protocol, "Grpc", StringComparison.OrdinalIgnoreCase))
        {
            options.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
        }
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var app = context.GetApplicationBuilder();
        var env = context.GetEnvironment();
        app.UseForwardedHeaders();
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }

        app.UseAbpRequestLocalization();

        if (!env.IsDevelopment())
        {
            app.UseErrorPage();
        }

        app.UseCorrelationId();
        app.UseRouting();
        // #433: rate limiter, after UseRouting so it can read the endpoint's RequireRateLimiting metadata.
        // Only the /mcp endpoint opts in (below); every other endpoint is unaffected.
        app.UseRateLimiter();
        app.UseStaticFiles();
        app.UseAbpStudioLink();
        app.UseAbpSecurityHeaders();
        app.UseCors();
        // A Bearer-carrying /mcp request is routed to OpenIddict validation by the cookie ForwardDefaultSelector
        // (see ConfigureAuthentication), so its principal flows through UseDynamicClaims below.
        app.UseAuthentication();
        app.UseAbpOpenIddictValidation();

        if (IsMultiTenant)
        {
            app.UseMultiTenancy();
        }

        app.UseUnitOfWork();
        app.UseDynamicClaims();
        app.UseAuthorization();

        app.UseSwagger();
        app.UseAbpSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "Extract API");
            options.OAuthClientId(context.GetConfiguration()["AuthServer:SwaggerClientId"]);
        });

        app.UseAuditing();
        app.UseAbpSerilogEnrichers();
        // The MCP export endpoint (Streamable HTTP, /mcp) is mapped here too, by Dignite.Abp.AspNetCore.Mcp
        // through AbpEndpointRouterOptions: RequireAuthorization on the default policy (the host's existing
        // OpenIddict Bearer, with dynamic-claims enrichment preserved), plus the #278 discovery 401 and the
        // #433 rate limiter configured in ConfigureMcpServer. Tool / resource method bodies still perform
        // explicit permission assertions as fail-closed double insurance.
        app.UseConfiguredEndpoints();
    }
}
