using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Documents;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using Shouldly;
using Volo.Abp.Modularity;
using Xunit;

namespace Dignite.Vault.Extract.Mcp.Documents;

[DependsOn(typeof(VaultExtractTestBaseModule))]
public class DocumentSubmissionToolsTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddSingleton(Substitute.For<IDocumentAppService>());
        context.Services.AllowAnyExplicitTenant();
    }
}

/// <summary>
/// <see cref="DocumentSubmissionTools"/>: the parameters a model sees, and the confirmation that comes before any
/// submission - through the client when it can show one (the model's own word does not count there), otherwise
/// through <c>userConfirmed</c>, refused with the consequences until it is set. What the submission does is the
/// application service's, covered by its own tests.
/// </summary>
public class DocumentSubmissionTools_Tests : VaultExtractTestBase<DocumentSubmissionToolsTestModule>
{
    private readonly IDocumentAppService _documentAppService;

    public DocumentSubmissionTools_Tests()
    {
        _documentAppService = GetRequiredService<IDocumentAppService>();
    }

    [Fact]
    public void The_input_schema_holds_the_model_facing_parameters_and_nothing_injected()
    {
        // Through real schema generation under Autofac (llm-call-anti-patterns C): a collection parameter that Autofac
        // could resolve as a service would silently leave the schema.
        var method = typeof(DocumentSubmissionTools).GetMethod(nameof(DocumentSubmissionTools.SubmitAsync))!;
        var tool = McpServerTool.Create(method, target: null, new McpServerToolCreateOptions { Services = ServiceProvider });

        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
        foreach (var name in new[] { "markdown", "documentTypeCode", "fields", "fieldSources", "title", "language", "cabinetId", "userConfirmed", "tenantId" })
        {
            properties.TryGetProperty(name, out _).ShouldBeTrue(name);
        }

        foreach (var name in new[] { "documentAppService", "server", "requestContext", "serviceProvider" })
        {
            properties.TryGetProperty(name, out _).ShouldBeFalse(name);
        }

        tool.ProtocolTool.Annotations!.ReadOnlyHint.ShouldBe(false);
        tool.ProtocolTool.Annotations.DestructiveHint.ShouldBe(false);
    }

    [Fact]
    public async Task Without_a_client_confirmation_nothing_is_submitted_until_the_user_agreed()
    {
        var server = Server(mrtr: false, elicitation: false);

        var result = await SubmitAsync(server, userConfirmed: false);

        result.Submitted.ShouldBeFalse();
        result.ConfirmationRequired.ShouldBeTrue();
        result.Consequences.ShouldBe(DocumentSubmissionTools.Consequences);
        await _documentAppService.DidNotReceiveWithAnyArgs().SubmitAsync(default!);
    }

    [Fact]
    public async Task With_the_users_agreement_the_submission_goes_to_the_application_service()
    {
        var id = Guid.NewGuid();
        _documentAppService.SubmitAsync(Arg.Any<SubmitDocumentInput>()).Returns(new DocumentDto
        {
            Id = id,
            LifecycleStatus = DocumentLifecycleStatus.Ready,
            DocumentTypeCode = "invoice",
            ExtractedFields = new Dictionary<string, JsonElement> { ["total"] = JsonDocument.Parse("1200").RootElement.Clone() }
        });
        var cabinetId = Guid.NewGuid();

        var result = await DocumentSubmissionTools.SubmitAsync(
            "# Invoice",
            _documentAppService,
            Server(mrtr: false, elicitation: false),
            Context(Server(mrtr: false, elicitation: false)),
            documentTypeCode: "invoice",
            fields: new Dictionary<string, JsonElement> { ["total"] = JsonDocument.Parse("1200").RootElement.Clone() },
            fieldSources: new Dictionary<string, List<string>> { ["total"] = ["Total 1200"] },
            title: "Invoice 7",
            language: "en",
            cabinetId: cabinetId.ToString(),
            userConfirmed: true,
            serviceProvider: ServiceProvider);

        result.Submitted.ShouldBeTrue();
        result.Id.ShouldBe(id);
        result.Uri.ShouldBe(DocumentResourceUri.Format(id, null));
        result.LifecycleStatus.ShouldBe("Ready");
        result.StoredFieldCount.ShouldBe(1);
        await _documentAppService.Received(1).SubmitAsync(Arg.Is<SubmitDocumentInput>(input =>
            input.Markdown == "# Invoice"
            && input.DocumentTypeCode == "invoice"
            && input.Fields!.ContainsKey("total")
            && input.FieldSources!["total"][0] == "Total 1200"
            && input.Title == "Invoice 7"
            && input.Language == "en"
            && input.CabinetId == cabinetId));
    }

    [Fact]
    public void A_client_that_can_confirm_is_asked_and_the_models_word_does_not_count()
    {
        var server = Server(mrtr: true, elicitation: true);

        var exception = Should.Throw<InputRequiredException>(
            () => DocumentSubmissionTools.Confirm(server, Context(server), userConfirmed: true));

        var request = exception.Result.InputRequests![DocumentSubmissionTools.ConfirmationKey];
        request.ElicitationParams!.Message.ShouldBe(DocumentSubmissionTools.ConfirmationMessage);
    }

    [Theory]
    [InlineData("accept", DocumentSubmissionTools.ConfirmationState.Confirmed)]
    [InlineData("decline", DocumentSubmissionTools.ConfirmationState.Declined)]
    [InlineData("cancel", DocumentSubmissionTools.ConfirmationState.Declined)]
    public void The_users_answer_in_the_retry_decides(string action, DocumentSubmissionTools.ConfirmationState expected)
    {
        var server = Server(mrtr: true, elicitation: true);
        var context = Context(server, new Dictionary<string, InputResponse>
        {
            [DocumentSubmissionTools.ConfirmationKey] = InputResponse.FromElicitResult(new ElicitResult { Action = action })
        });

        DocumentSubmissionTools.Confirm(server, context, userConfirmed: false).ShouldBe(expected);
    }

    [Fact]
    public async Task A_declined_confirmation_submits_nothing()
    {
        var server = Server(mrtr: true, elicitation: true);
        var context = Context(server, new Dictionary<string, InputResponse>
        {
            [DocumentSubmissionTools.ConfirmationKey] = InputResponse.FromElicitResult(new ElicitResult { Action = "decline" })
        });

        var result = await DocumentSubmissionTools.SubmitAsync(
            "# Invoice", _documentAppService, server, context, userConfirmed: true, serviceProvider: ServiceProvider);

        result.Declined.ShouldBeTrue();
        result.Submitted.ShouldBeFalse();
        await _documentAppService.DidNotReceiveWithAnyArgs().SubmitAsync(default!);
    }

    private Task<DocumentSubmissionResult> SubmitAsync(McpServer server, bool userConfirmed)
        => DocumentSubmissionTools.SubmitAsync(
            "# Invoice", _documentAppService, server, Context(server), userConfirmed: userConfirmed, serviceProvider: ServiceProvider);

    private static McpServer Server(bool mrtr, bool elicitation)
    {
        var server = Substitute.For<McpServer>();
        server.IsMrtrSupported.Returns(mrtr);
        server.ClientCapabilities.Returns(new ClientCapabilities { Elicitation = elicitation ? new ElicitationCapability() : null });
        return server;
    }

    private static RequestContext<CallToolRequestParams> Context(
        McpServer server, IDictionary<string, InputResponse>? responses = null)
        => new(server, new JsonRpcRequest { Method = RequestMethods.ToolsCall }, new CallToolRequestParams
        {
            Name = "vault_extract_submit_document",
            InputResponses = responses
        });
}
