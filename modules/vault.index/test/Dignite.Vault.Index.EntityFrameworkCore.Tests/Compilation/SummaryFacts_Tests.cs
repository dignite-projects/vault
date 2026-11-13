using Shouldly;
using Xunit;

namespace Dignite.Vault.Index.Compilation;

/// <summary>The numbers check: a number in a summary must be a value its input states, however either writes it.</summary>
public class SummaryFacts_Tests
{
    [Theory]
    [InlineData("合計58,960円", "合計 ５８，９６０ 円")]
    [InlineData("月額30万円", "月額 300,000 円")]
    [InlineData("共益費5万6000円", "共益費は月額 56,000 円")]
    [InlineData("月額300,000円", "月額30万円")]
    [InlineData("3か月前までに予告", "三か月前までに書面で通知")]
    [InlineData("上限は12か月分", "直近十二か月間に受領した委託料")]
    [InlineData("2026-11-01に承認", "2026年11月1日の会議で承認")]
    [InlineData("受付は9:00から", "平日 9 時から 18 時まで")]
    [InlineData("平日19時～22時に作業", "作業は平日の午後 7 時から午後 10 時までに行う")]
    [InlineData("第1,2条", "第1条 第2条")]
    [InlineData("電力量1.5万kWh", "15,000 kWh")]
    [InlineData("住居として賃貸する", "住居として賃貸する。")]
    public void A_number_the_input_states_in_any_spelling_is_supported(string summary, string input)
        => SummaryFacts.Unsupported(summary, input).ShouldBeEmpty();

    [Fact]
    public void An_invented_date_or_amount_is_not()
    {
        SummaryFacts.Unsupported("請求期間は2026年9月1日から9月30日", "ご使用期間 2026年9月15日から 2026年10月14日まで")
            .ShouldBe(["1", "30"]);
        SummaryFacts.Unsupported("合計59,000円", "合計 58,960 円").ShouldBe(["59000"]);
    }
}
