using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dignite.Vault.Parse.ElBrunoMarkItDown;
using Dignite.Vault.Parse.Ocr.VisionLlm;
using Dignite.Vault.Parse.OpenXml;
using Dignite.Vault.Parse.Pdf;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using Volo.Abp;
using Volo.Abp.Autofac;
using Volo.Abp.Modularity;

namespace Dignite.Vault.Parse.Evaluation;

/// <summary>
/// The host's Parse wiring: PdfPig, OpenXML and ElBruno MarkItDown, with vision-LLM OCR. The vision model comes from
/// <c>VAULT_PARSE_EVAL_VISION_ENDPOINT</c>, <c>_API_KEY</c> and <c>_MODEL</c>; without them a document that reaches OCR
/// fails, which no digital corpus document should.
/// </summary>
[DependsOn(
    typeof(AbpAutofacModule),
    typeof(VaultParseModule),
    typeof(VaultParsePdfModule),
    typeof(VaultParseOpenXmlModule),
    typeof(VaultParseElBrunoMarkItDownModule),
    typeof(VaultParseVisionLlmOcrModule))]
public class ParseEvaluationTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddKeyedSingleton<IChatClient>(
            VisionLlmOcrConsts.VisionChatClientKey,
            (_, _) => VisionModel.IsConfigured
                ? new OpenAIClient(
                        new ApiKeyCredential(VisionModel.ApiKey!),
                        new OpenAIClientOptions { Endpoint = new Uri(VisionModel.Endpoint!), NetworkTimeout = TimeSpan.FromMinutes(5) })
                    .GetChatClient(VisionModel.ModelId)
                    .AsIChatClient()
                : new UnconfiguredVisionClient());
    }

    private sealed class UnconfiguredVisionClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("This document needs OCR. Set VAULT_PARSE_EVAL_VISION_ENDPOINT, _API_KEY and _MODEL.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("This document needs OCR. Set VAULT_PARSE_EVAL_VISION_ENDPOINT, _API_KEY and _MODEL.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

/// <summary>The vision model the OCR documents are parsed with; each OCR page is one model call.</summary>
public static class VisionModel
{
    public static string? Endpoint => Environment.GetEnvironmentVariable("VAULT_PARSE_EVAL_VISION_ENDPOINT");

    public static string? ApiKey => Environment.GetEnvironmentVariable("VAULT_PARSE_EVAL_VISION_API_KEY");

    public static string? ModelId => Environment.GetEnvironmentVariable("VAULT_PARSE_EVAL_VISION_MODEL");

    public static bool IsConfigured
        => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ModelId);
}
