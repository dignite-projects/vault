using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dignite.Vault.Index.Chunks;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Index.Ingestion;

/// <summary>A document's field values as the lines in front of each chunk's embedding text.</summary>
public class IndexFieldContext_Tests
{
    [Fact]
    public void Values_a_search_names_become_lines_in_name_order()
    {
        var fields = Fields(new {
            tenant = "山田太郎",
            rent = 185000,
            tags = new[] { "住宅", "更新可" },
            items = new[] { new { name = "敷金", amount = 370000 } },
            address = new { city = "港区" },
            note = new string('x', IndexFieldContext.MaxValueLength + 1),
            empty = ""
        });

        IndexFieldContext.Describe(fields).ShouldBe("rent: 185000\ntags: 住宅, 更新可\ntenant: 山田太郎");
    }

    [Fact]
    public void No_fields_no_lines_and_the_lines_are_capped()
    {
        IndexFieldContext.Describe(null).ShouldBeNull();
        IndexFieldContext.Describe(new Dictionary<string, JsonElement>()).ShouldBeNull();

        var many = Enumerable.Range(0, 40).ToDictionary(i => $"field{i:00}", _ => JsonSerializer.SerializeToElement(new string('値', 30)));
        IndexFieldContext.Describe(many)!.Length.ShouldBeLessThanOrEqualTo(IndexFieldContext.MaxLength);
    }

    [Fact]
    public void The_lines_sit_between_the_title_and_the_chunk()
    {
        ChunkContext.ComposeForEmbedding("賃貸借契約書", "tenant: 山田太郎", "# 第3条\n敷金は二か月分", "敷金はいくら")
            .ShouldBe("賃貸借契約書\ntenant: 山田太郎\n# 第3条\n敷金は二か月分\n敷金はいくら");
        ChunkContext.ComposeForEmbedding("賃貸借契約書", null, "本文", null).ShouldBe("賃貸借契約書\n本文");
    }

    private static Dictionary<string, JsonElement> Fields(object values)
        => JsonSerializer.SerializeToElement(values).EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
}
