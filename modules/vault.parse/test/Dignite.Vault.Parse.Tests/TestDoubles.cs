using System;
using System.IO;
using System.Threading;
using Microsoft.Extensions.DocumentExtraction;
using NSubstitute;

namespace Dignite.Vault.Parse;

/// <summary>
/// Shared NSubstitute builders for the router unit tests. Keeps each test focused on the single branch it exercises
/// rather than on substitute plumbing.
/// </summary>
internal static class TestDoubles
{
    /// <summary>
    /// An engine that reads exactly the extension <paramref name="handles"/> (case-insensitive) at
    /// <paramref name="priority"/>, answering with <paramref name="markdown"/> read into a document.
    /// </summary>
    public static ParseExtractionClient Engine(
        int priority,
        string handles,
        string markdown = "digital markdown",
        string provider = "FakeEngine")
    {
        var engine = Substitute.For<ParseExtractionClient>();
        engine.Priority.Returns(priority);
        engine.ProviderName.Returns(provider);
        engine.CanExtract(Arg.Any<string>(), Arg.Any<string>())
            .Returns(call => string.Equals(call.ArgAt<string>(0), handles, StringComparison.OrdinalIgnoreCase));
        engine.ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DocumentExtractionOptions?>(), Arg.Any<CancellationToken>())
            .Returns(call => ParseTestKit.OcrDocument(call.ArgAt<string>(1), markdown, provider));
        return engine;
    }

    public static ParseRoutingClient Router(params ParseExtractionClient[] engines) => new(engines);

    public static MemoryStream Bytes(params byte[] content) => new(content);
}
