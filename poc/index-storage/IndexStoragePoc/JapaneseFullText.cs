using Microsoft.Data.SqlClient;

namespace IndexStoragePoc;

/// <summary>
/// Question 4: does the Japanese word breaker (LCID 1041) find what a Japanese user types? Each case is a query and
/// the sentences it should find; the report shows CONTAINS and FREETEXT hits and the tokens the breaker produced.
/// </summary>
public static class JapaneseFullText
{
    private static readonly string[] Sentences =
    [
        /* 1 */ "本賃貸借契約書は、甲と乙の間で締結される。",
        /* 2 */ "乙は毎月末日までに賃料を支払うものとする。",
        /* 3 */ "請求書の支払期限は2026年11月30日です。",
        /* 4 */ "サーバーの保守契約を更新しました。",
        /* 5 */ "サーバ構成図を添付します。",
        /* 6 */ "ＡＢＣ株式会社との取引基本契約",
        /* 7 */ "ABC株式会社との秘密保持契約",
        /* 8 */ "ｻｰﾊﾞｰの設定手順",
        /* 9 */ "内容をご確認ください。",
        /* 10 */ "内容をご確認下さい。",
        /* 11 */ "東京都千代田区丸の内一丁目",
        /* 12 */ "見積金額は１２３，４５６円（税込）",
        /* 13 */ "見積金額は123,456円(税込)",
        /* 14 */ "人工智能合同审查流程",
        /* 15 */ "Vault Extract で抽出した Markdown",
        /* 16 */ "損害賠償の上限は契約金額とする。",
        /* 17 */ "物件の引渡日は別途定める。",
        /* 18 */ "物件の引き渡し日を変更する。",
        /* 19 */ "インボイス制度の登録番号 T1234567890123",
        /* 20 */ "電子帳簿保存法に対応した請求書発行"
    ];

    private static readonly (string Query, int[] Expected)[] Cases =
    [
        ("契約", [1, 4, 6, 7, 16]),
        ("賃貸借", [1]),
        ("賃貸借契約", [1]),
        ("支払", [2, 3]),
        ("支払う", [2]),
        ("サーバー", [4, 5, 8]),
        ("サーバ", [4, 5, 8]),
        ("ABC", [6, 7]),
        ("ＡＢＣ", [6, 7]),
        ("123", [12, 13]),
        ("ください", [9, 10]),
        ("下さい", [9, 10]),
        ("千代田区", [11]),
        ("千代田", [11]),
        ("引渡日", [17, 18]),
        ("引き渡し", [17, 18]),
        ("合同", [14]),
        ("markdown", [15]),
        ("損害賠償", [16]),
        ("T1234567890123", [19]),
        ("電子帳簿保存法", [20]),
        ("帳簿", [20]),
        ("請求書", [3, 20]),
    ];

    public static async Task RunAsync(string cs)
    {
        await Sql.ExecAsync(cs, """
            IF OBJECT_ID('dbo.JpCases') IS NOT NULL BEGIN
                IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('dbo.JpCases')) DROP FULLTEXT INDEX ON dbo.JpCases;
                DROP TABLE dbo.JpCases;
            END;
            IF EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'ftc_jp') DROP FULLTEXT CATALOG ftc_jp;
            CREATE TABLE dbo.JpCases (Id int NOT NULL CONSTRAINT PK_JpCases PRIMARY KEY, Text nvarchar(400) NOT NULL);
            """);

        for (var i = 0; i < Sentences.Length; i++)
        {
            await Sql.ExecAsync(cs, $"INSERT dbo.JpCases (Id, Text) VALUES ({i + 1}, N'{Sentences[i].Replace("'", "''")}')");
        }

        await Sql.ExecAsync(cs, """
            CREATE FULLTEXT CATALOG ftc_jp;
            CREATE FULLTEXT INDEX ON dbo.JpCases (Text LANGUAGE 1041) KEY INDEX PK_JpCases ON ftc_jp WITH CHANGE_TRACKING AUTO;
            """);
        await FullText.WaitForPopulationAsync(cs, "ftc_jp");
        await Task.Delay(2000);

        var passContains = 0;
        var passFreetext = 0;
        foreach (var (query, expected) in Cases)
        {
            var contains = await HitsAsync(cs, "SELECT Id FROM dbo.JpCases WHERE CONTAINS(Text, @q) ORDER BY Id", $"\"{query}\"");
            var freetext = await HitsAsync(cs, "SELECT Id FROM dbo.JpCases WHERE FREETEXT(Text, @q) ORDER BY Id", query);
            var tokens = await TokensAsync(cs, $"\"{query}\"");
            var okC = contains.SequenceEqual(expected);
            var okF = freetext.SequenceEqual(expected);
            passContains += okC ? 1 : 0;
            passFreetext += okF ? 1 : 0;
            Console.WriteLine($"{query,-16} expected [{string.Join(",", expected)}]  CONTAINS [{string.Join(",", contains)}]{(okC ? "" : " ✗")}  FREETEXT [{string.Join(",", freetext)}]{(okF ? "" : " ✗")}  tokens: {tokens}");
        }

        Console.WriteLine($"exact match with the expected set: CONTAINS {passContains}/{Cases.Length}, FREETEXT {passFreetext}/{Cases.Length}");

        Console.WriteLine("--- how the breaker splits some indexed sentences ---");
        foreach (var i in new[] { 1, 4, 6, 8, 12, 17, 18, 20 })
        {
            Console.WriteLine($"{i,2}: {await TokensAsync(cs, Sentences[i - 1])}");
        }
    }

    private static async Task<List<int>> HitsAsync(string cs, string sql, string q)
    {
        var ids = new List<int>();
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c);
        cmd.Parameters.AddWithValue("@q", q);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            ids.Add(r.GetInt32(0));
        }

        return ids;
    }

    private static async Task<string> TokensAsync(string cs, string text)
    {
        var tokens = new List<string>();
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = new SqlCommand("SELECT display_term FROM sys.dm_fts_parser(@t, 1041, 0, 0) WHERE special_term <> 'End Of Sentence' ORDER BY occurrence", c);
        cmd.Parameters.AddWithValue("@t", text);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            tokens.Add(r.GetString(0));
        }

        return string.Join(" | ", tokens);
    }
}
