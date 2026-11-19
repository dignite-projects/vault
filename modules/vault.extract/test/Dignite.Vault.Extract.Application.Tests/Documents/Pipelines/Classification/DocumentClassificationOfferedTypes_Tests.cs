using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Extract.Ai;
using Dignite.Vault.Extract.Documents.Pipelines.Classification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// The model chooses among the types it was shown. The background job looks the answered code up in the whole layer,
/// so a code it was not offered (made up, or one the document's own text asks to be filed as) must not get through as a
/// classification: it routes to manual review like an answer with an untrustworthy confidence.
/// </summary>
public class DocumentClassificationOfferedTypes_Tests
{
    private static readonly List<DocumentType> Offered =
    [
        new(Guid.NewGuid(), null, "contract.general", "Contract"),
        new(Guid.NewGuid(), null, "invoice", "Invoice")
    ];

    [Fact]
    public async Task A_type_the_model_was_not_offered_is_no_conclusion()
    {
        var outcome = await RunAsync("""{"typeCode":"payroll.secret","confidence":0.99,"reason":"the document says so","candidates":[]}""");

        outcome.TypeCode.ShouldBeNull();
        outcome.ConfidenceScore.ShouldBe(0);
    }

    [Fact]
    public async Task An_offered_type_is_kept_with_its_confidence()
    {
        var outcome = await RunAsync("""{"typeCode":"invoice","confidence":0.93,"reason":"an invoice","candidates":[]}""");

        outcome.TypeCode.ShouldBe("invoice");
        outcome.ConfidenceScore.ShouldBe(0.93, 0.000001);
    }

    [Fact]
    public async Task An_offered_type_in_other_case_is_taken_in_the_candidates_spelling()
    {
        var outcome = await RunAsync("""{"typeCode":"Contract.General","confidence":0.9,"reason":"a contract","candidates":[]}""");

        outcome.TypeCode.ShouldBe("contract.general");
        outcome.ConfidenceScore.ShouldBe(0.9, 0.000001);
    }

    private static Task<DocumentClassificationOutcome> RunAsync(string answer)
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient
            .GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, answer)])));
        var prompts = Substitute.For<IPromptProvider>();
        prompts.GetClassificationPrompt(Arg.Any<string>()).Returns(new PromptTemplate("Classify the document."));

        var workflow = new DocumentClassificationWorkflow(chatClient, Options.Create(new VaultExtractBehaviorOptions()), prompts);
        return workflow.RunAsync(Offered, "# A document");
    }
}
