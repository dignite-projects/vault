using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dignite.Abp.FlexFields;
using Dignite.Abp.FlexFields.Select;
using Dignite.Vault.Extract.Documents.Fields;
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

    [Fact]
    public void A_select_field_contributes_its_options_labels()
    {
        var fields = Fields(new { status = "st_02", channels = new[] { "web", "tel", "fax" }, note = "st_02" });
        var definitions = new Dictionary<string, FieldDefinitionDto>
        {
            ["status"] = Select(multiple: false, ("st_01", "申請中"), ("st_02", "承認済み")),
            ["channels"] = Select(multiple: true, ("web", "Web 申込"), ("tel", "電話"), ("fax", "")),
            ["note"] = new() { Name = "note", FieldTypeName = "Text" }
        };

        // A label replaces its value; an option without a label, and a field of another type, stay as stored.
        IndexFieldContext.Describe(fields, definitions).ShouldBe("channels: Web 申込, 電話, fax\nnote: st_02\nstatus: 承認済み");
        IndexFieldContext.Describe(fields).ShouldBe("channels: web, tel, fax\nnote: st_02\nstatus: st_02");
    }

    [Fact]
    public void A_select_value_outside_its_options_stays_as_stored()
    {
        var definitions = new Dictionary<string, FieldDefinitionDto> { ["status"] = Select(multiple: false, ("st_01", "申請中")) };

        IndexFieldContext.Describe(Fields(new { status = "st_09" }), definitions).ShouldBe("status: st_09");
    }

    private static FieldDefinitionDto Select(bool multiple, params (string Value, string Text)[] options) => new()
    {
        Name = "select",
        FieldTypeName = SelectFieldType.ControlName,
        Configuration = new SelectConfiguration
        {
            Multiple = multiple,
            Options = options.Select(o => new SelectListItem(o.Text, o.Value, false)).ToList()
        }.ConfigurationDictionary
    };

    private static Dictionary<string, JsonElement> Fields(object values)
        => JsonSerializer.SerializeToElement(values).EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
}
