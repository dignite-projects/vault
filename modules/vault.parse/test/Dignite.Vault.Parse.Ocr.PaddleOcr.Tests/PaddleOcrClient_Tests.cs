using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Parse.Ocr.PaddleOcr;

/// <summary>
/// Unit tests for <see cref="PaddleOcrClient"/> at the HTTP boundary: a recording
/// <see cref="HttpMessageHandler"/> stands in for the PaddleOCR sidecar, so request mapping and response
/// handling are verified without any running service.
/// </summary>
public class PaddleOcrClient_Tests
{
    private static (PaddleOcrClient Provider, RecordingHandler Handler) CreateProvider(
        HttpResponseMessage response,
        PaddleOcrOptions? options = null)
    {
        var handler = new RecordingHandler(response);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));
        var provider = new PaddleOcrClient(Options.Create(options ?? new PaddleOcrOptions()), factory);
        return (provider, handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static MemoryStream FakeImage() => new(new byte[] { 0xFF, 0xD8, 0xFF });

    [Fact]
    public async Task Should_Pass_Through_Structured_Markdown_And_Keep_The_Raw_Answer()
    {
        var rawJson = """
            { "markdown": "# Heading\n\n| a | b |", "detected_language": "en", "provider_name": "PaddleOCR", "provider_model": "PP-StructureV3" }
            """;
        var (provider, _) = CreateProvider(Json(rawJson));

        var result = await provider.ParseAsync(FakeImage(), "upload", "image/jpeg");

        result.Markdown.ShouldBe("# Heading\n\n| a | b |");
        result.DetectedLanguage.ShouldBe("en");
        result.ProviderName.ShouldBe("PaddleOCR");
        // #210: the sidecar's answer is kept verbatim as the document's raw representation.
        result.Document.RawRepresentation.ShouldBe(rawJson);
        // PaddleOCR has no completeness concept → default complete.
        result.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public async Task Should_Wrap_RawText_Into_Flat_Markdown_Paragraphs_When_No_Markdown_Is_Returned()
    {
        // PP-OCRv4 mode returns only raw_text; the engine wraps it into flat
        // Markdown paragraphs to satisfy Markdown-first.
        var (provider, _) = CreateProvider(Json("""{ "raw_text": "line one\nline two\n\nline three" }"""));

        var result = await provider.ParseAsync(FakeImage(), "upload", "");

        var expected = string.Join(
            "\n\n",
            "line one", "line two", "line three");
        result.Markdown.ShouldBe(expected);
        // provider_name absent → defaults to "PaddleOCR".
        result.ProviderName.ShouldBe("PaddleOCR");
    }

    [Fact]
    public async Task Should_Post_Multipart_Request_To_The_Ocr_Endpoint()
    {
        var (provider, handler) = CreateProvider(Json("""{ "markdown": "x" }"""));

        await provider.ParseAsync(
            FakeImage(),
            "upload", "image/jpeg", new[] { "fr", "de" });

        handler.Request!.Method.ShouldBe(HttpMethod.Post);
        handler.Request.RequestUri!.ToString().ShouldBe("http://localhost:8866/ocr");
        // LanguageHints win over the option defaults and are sent comma-joined.
        handler.RequestBody.ShouldContain("fr,de");
        // model_name carries the configured model; the file part carries the supplied content type.
        handler.RequestBody.ShouldContain("PP-StructureV3");
        handler.RequestBody.ShouldContain("image/jpeg");
    }

    [Fact]
    public async Task Should_Fall_Back_To_Option_Languages_When_No_Hints_Are_Supplied()
    {
        var (provider, handler) = CreateProvider(
            Json("""{ "markdown": "x" }"""),
            new PaddleOcrOptions { Languages = new List<string> { "ja", "en" } });

        await provider.ParseAsync(FakeImage(), "upload", ""); // no LanguageHints

        handler.RequestBody.ShouldContain("ja,en");
    }

    [Fact]
    public async Task Should_Send_The_Configured_Model_Name()
    {
        var (provider, handler) = CreateProvider(
            Json("""{ "raw_text": "hi" }"""),
            new PaddleOcrOptions { ModelName = "PP-OCRv4" });

        await provider.ParseAsync(FakeImage(), "upload", "");

        handler.RequestBody.ShouldContain("PP-OCRv4");
    }

    [Fact]
    public async Task Should_Trim_A_Trailing_Slash_On_The_Endpoint()
    {
        var (provider, handler) = CreateProvider(
            Json("""{ "markdown": "x" }"""),
            new PaddleOcrOptions { Endpoint = "http://localhost:8866/" });

        await provider.ParseAsync(FakeImage(), "upload", "");

        handler.Request!.RequestUri!.ToString().ShouldBe("http://localhost:8866/ocr");
    }

    [Fact]
    public async Task Should_Throw_HttpRequestException_With_Status_And_Body_On_Error_Response()
    {
        var (provider, _) = CreateProvider(
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("model crashed")
            });

        var ex = await Should.ThrowAsync<HttpRequestException>(() =>
            provider.ParseAsync(FakeImage(), "upload", ""));

        ex.Message.ShouldContain("500");
        ex.Message.ShouldContain("model crashed");
    }

    [Fact]
    public async Task Should_Throw_When_The_Sidecar_Returns_An_Empty_Json_Body()
    {
        var (provider, _) = CreateProvider(Json("null"));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            provider.ParseAsync(FakeImage(), "upload", ""));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public RecordingHandler(HttpResponseMessage response) => _response = response;

        public HttpRequestMessage? Request { get; private set; }
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            if (request.Content is not null)
            {
                // Read the multipart body here, before it is disposed, so tests can assert on the fields.
                RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return _response;
        }
    }
}
