using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents.DocumentTypes;
using Dignite.Vault.Extract.Permissions;
using NSubstitute;
using Shouldly;
using Volo.Abp.Authorization;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// <c>SubmitAsync</c> is judged by two rows: <see cref="DocumentAccessRule.Upload"/> on the declared type, exactly as an
/// upload, and <see cref="DocumentAccessRule.Submit"/> on the empty subject for bringing one's own content. Either one
/// missing refuses the submission before anything is read or written. Runs on the grant-set host of the declared-type
/// upload tests, whose repositories are substitutes.
/// </summary>
public class DocumentAppService_SubmitAccess_Tests
    : VaultExtractApplicationTestBase<DocumentAppServiceUploadDeclaredTypeTestModule>
{
    private readonly IDocumentAppService _appService;
    private readonly IDocumentRepository _documentRepository;
    private readonly IDocumentTypeRepository _documentTypeRepository;
    private readonly GrantSetAuthorizationService _authorization;
    private readonly DocumentAccessCheckRecorder _recorder;

    public DocumentAppService_SubmitAccess_Tests()
    {
        _appService = GetRequiredService<IDocumentAppService>();
        _documentRepository = GetRequiredService<IDocumentRepository>();
        _documentTypeRepository = GetRequiredService<IDocumentTypeRepository>();
        _authorization = GetRequiredService<GrantSetAuthorizationService>();
        _recorder = GetRequiredService<DocumentAccessCheckRecorder>();
        _documentTypeRepository.GetCountAsync(Arg.Any<CancellationToken>()).Returns(1L);
    }

    [Fact]
    public async Task The_upload_right_alone_does_not_admit_a_submission()
    {
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Upload);

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => _appService.SubmitAsync(new SubmitDocumentInput { Markdown = "# Text" }));

        await NothingInsertedAsync();
    }

    [Fact]
    public async Task The_submit_right_alone_does_not_admit_a_submission()
    {
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Submit);

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => _appService.SubmitAsync(new SubmitDocumentInput { Markdown = "# Text" }));

        await NothingInsertedAsync();
    }

    [Fact]
    public async Task A_typed_submission_is_judged_as_an_upload_into_that_type_and_as_a_submission()
    {
        var type = new DocumentType(Guid.NewGuid(), null, "invoice", "Invoice");
        _documentTypeRepository.FindByTypeCodeAsync("invoice", Arg.Any<CancellationToken>()).Returns(type);
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.Upload);

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => _appService.SubmitAsync(new SubmitDocumentInput { Markdown = "# Text", DocumentTypeCode = "invoice" }));

        _recorder.Checks.ShouldContain(check => check.Rule == DocumentAccessRule.Upload && check.Subject!.DocumentTypeId == type.Id);
        _recorder.Checks.ShouldContain(check => check.Rule == DocumentAccessRule.Submit && check.Subject!.DocumentTypeId == null);
    }

    private void Grant(params string[] permissions) => _authorization.Granted = new HashSet<string>(permissions);

    private Task NothingInsertedAsync()
        => _documentRepository.DidNotReceiveWithAnyArgs().InsertAsync(default!, default, default);
}
