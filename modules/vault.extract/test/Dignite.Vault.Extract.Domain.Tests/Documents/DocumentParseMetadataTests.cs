using System.Text.Json;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents;

/// <summary>
/// #218: <see cref="DocumentParseMetadata"/> inherits ABP <c>ValueObject</c> and gets structural equality through
/// <c>ValueEquals</c> (ABP's ValueObject does not override <c>Equals</c>/<c>GetHashCode</c>/<c>==</c>). This must not
/// break System.Text.Json round-trips, and JSON written by earlier versions must still read. Pure unit test; no ABP host
/// required.
/// </summary>
public class DocumentParseMetadataTests
{
    [Fact]
    public void Metadata_With_Same_Values_Should_Be_ValueEqual()
    {
        new DocumentParseMetadata("PaddleOCR").ValueEquals(new DocumentParseMetadata("PaddleOCR")).ShouldBeTrue();
    }

    [Fact]
    public void Metadata_With_Null_Members_Should_Be_ValueEqual_And_Differ_From_Populated()
    {
        var emptyA = new DocumentParseMetadata(null);
        var emptyB = new DocumentParseMetadata(null);

        emptyA.ValueEquals(emptyB).ShouldBeTrue();
        emptyA.ValueEquals(new DocumentParseMetadata("PaddleOCR")).ShouldBeFalse();
    }

    [Fact]
    public void Json_Roundtrip_Should_Preserve_Value_Equality()
    {
        var original = new DocumentParseMetadata("PaddleOCR");

        var roundtripped = JsonSerializer.Deserialize<DocumentParseMetadata>(JsonSerializer.Serialize(original));

        roundtripped.ShouldNotBeNull();
        roundtripped!.ValueEquals(original).ShouldBeTrue();
    }

    [Fact]
    public void Completeness_Participates_In_Value_Equality_And_Json_Roundtrip()
    {
        var complete = new DocumentParseMetadata("VisionLlm");
        var incomplete = new DocumentParseMetadata(
            "VisionLlm", isComplete: false, incompleteReason: "2 of 5 page(s) were not fully transcribed.");

        complete.ValueEquals(incomplete).ShouldBeFalse();

        var roundtripped = JsonSerializer.Deserialize<DocumentParseMetadata>(JsonSerializer.Serialize(incomplete));
        roundtripped.ShouldNotBeNull();
        roundtripped!.IsComplete.ShouldBeFalse();
        roundtripped.IncompleteReason.ShouldBe("2 of 5 page(s) were not fully transcribed.");
        roundtripped.ValueEquals(incomplete).ShouldBeTrue();
    }

    [Fact]
    public void Legacy_Json_Without_Completeness_Deserializes_As_Complete()
    {
        // Backward compatibility (#268): rows persisted before #268 have no completeness fields, so the
        // constructor's optional parameter defaults treat them as complete.
        var legacyJson = "{\"ProviderName\":\"PaddleOCR\",\"NativePayloadManifest\":null}";

        var metadata = JsonSerializer.Deserialize<DocumentParseMetadata>(legacyJson);

        metadata.ShouldNotBeNull();
        metadata!.IsComplete.ShouldBeTrue();
        metadata.IncompleteReason.ShouldBeNull();
    }

    [Fact]
    public void Legacy_Json_With_A_Native_Payload_Manifest_Still_Reads()
    {
        // Rows written while the parse job archived native payloads carry the manifest; it is ignored on read.
        var legacyJson = """
            {"ProviderName":"PaddleOCR","NativePayloadManifest":{"BlobName":"extraction-native/doc-1","ContentType":"application/json","SizeBytes":1234,"Sha256":"abc123","SchemaName":"PaddleOCR/PP-StructureV3"},"IsComplete":false,"IncompleteReason":"truncated"}
            """;

        var metadata = JsonSerializer.Deserialize<DocumentParseMetadata>(legacyJson);

        metadata.ShouldNotBeNull();
        metadata!.ProviderName.ShouldBe("PaddleOCR");
        metadata.IsComplete.ShouldBeFalse();
        metadata.IncompleteReason.ShouldBe("truncated");
    }
}
