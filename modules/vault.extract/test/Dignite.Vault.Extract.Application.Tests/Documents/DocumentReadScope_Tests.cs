using System;
using System.Linq;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Permissions;
using Shouldly;
using Volo.Abp.Authorization;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// <see cref="IDocumentAppService.GetReadScopeAsync"/>: the read scope projected for a module in the same host that
/// filters its own rows by it (Vault Index). The projection facts pin each member; the parity fact is the contract
/// itself, that applying the documented version-1 reading of the DTO admits exactly the documents Extract's own read
/// rule admits.
/// </summary>
public class DocumentReadScope_Tests : DocumentAccessTestBase
{
    [Fact]
    public async Task ReadAll_holder_gets_an_unrestricted_scope()
    {
        Grant(VaultExtractPermissions.Documents.Default, VaultExtractPermissions.Documents.ReadAll);

        var scope = await AsStrangerAsync(() => AppService.GetReadScopeAsync());

        scope.Version.ShouldBe(1);
        scope.IsUnrestricted.ShouldBeTrue();
        scope.DocumentTypeIds.ShouldBeEmpty();
        scope.OwnerId.ShouldBeNull();
    }

    [Fact]
    public async Task Per_type_grants_and_the_owner_arm_project_into_the_scope()
    {
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Read, TypeA.Id, StrangerId);
        GrantResource(VaultExtractResourcePermissions.Read, TypeB.Id, StrangerId);
        // An Edit grant is not a Read grant: it must not leak into the read scope.
        GrantResource(VaultExtractResourcePermissions.Edit, TypeC.Id, StrangerId);

        var scope = await AsStrangerAsync(() => AppService.GetReadScopeAsync());

        scope.Version.ShouldBe(1);
        scope.IsUnrestricted.ShouldBeFalse();
        scope.DocumentTypeIds.ShouldBe(new[] { TypeA.Id, TypeB.Id }, ignoreOrder: true);
        scope.OwnerId.ShouldBe(StrangerId);
    }

    [Fact]
    public async Task Entry_alone_reaches_only_the_callers_own_documents()
    {
        GrantEntryOnly();

        var scope = await AsStrangerAsync(() => AppService.GetReadScopeAsync());

        scope.IsUnrestricted.ShouldBeFalse();
        scope.DocumentTypeIds.ShouldBeEmpty();
        scope.OwnerId.ShouldBe(StrangerId);
    }

    /// <summary>
    /// Without entry there is no scope at all, so a module that resolves it per query refuses the query: holding
    /// <c>ReadAll</c> alone does not open the documents area, and must not open it through another module either.
    /// </summary>
    [Fact]
    public async Task Caller_without_entry_gets_no_scope()
    {
        Grant(VaultExtractPermissions.Documents.ReadAll);

        await Should.ThrowAsync<AbpAuthorizationException>(
            () => AsStrangerAsync(() => AppService.GetReadScopeAsync()));
    }

    /// <summary>
    /// The contract a consumer relies on: reading the DTO as its type documents (unrestricted, or the document's type
    /// is listed, or its owner is the scope's owner) gives the same answer per document as Extract's own read rule,
    /// asked through <see cref="IDocumentAppService.FindForCallerAsync"/>. Covers each arm admitting, each arm
    /// refusing, and the untyped document that only its uploader reaches.
    /// </summary>
    [Fact]
    public async Task Applying_the_scope_admits_exactly_what_the_read_rule_admits()
    {
        GrantEntryOnly();
        GrantResource(VaultExtractResourcePermissions.Read, TypeA.Id, StrangerId);

        var documents = new[]
        {
            StubDocument(TypeA.Id, creatorId: OwnerId),     // granted type, someone else's: readable
            StubDocument(TypeB.Id, creatorId: OwnerId),     // ungranted type, someone else's: not readable
            StubDocument(TypeB.Id, creatorId: StrangerId),  // ungranted type, the caller's own: readable
            StubDocument(null, creatorId: StrangerId),      // untyped, the caller's own: readable
            StubDocument(null, creatorId: OwnerId),         // untyped, someone else's: not readable
            StubDocument(TypeC.Id, creatorId: null)         // machine-created, ungranted type: not readable
        };

        var scope = await AsStrangerAsync(() => AppService.GetReadScopeAsync());

        foreach (var document in documents)
        {
            var admittedByScope = scope.IsUnrestricted
                                  || (document.DocumentTypeId is { } typeId && scope.DocumentTypeIds.Contains(typeId))
                                  || (scope.OwnerId is { } ownerId && document.CreatorId == ownerId);

            var admittedByRule = await AsStrangerAsync(() => AppService.FindForCallerAsync(document.Id)) != null;

            admittedByScope.ShouldBe(
                admittedByRule,
                $"document of type {document.DocumentTypeId?.ToString() ?? "none"} created by {document.CreatorId?.ToString() ?? "nobody"}");
        }

        documents.Count(d => scope.IsUnrestricted
                             || (d.DocumentTypeId is { } t && scope.DocumentTypeIds.Contains(t))
                             || (scope.OwnerId is { } o && d.CreatorId == o))
            .ShouldBe(3);
    }
}
