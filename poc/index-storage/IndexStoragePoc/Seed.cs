using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;

namespace IndexStoragePoc;

/// <summary>
/// Synthetic corpus. Two tenants (200k + 50k chunks), 20 document types per tenant with Zipf-skewed sizes, 50
/// owners, about 10 chunks per document, 1024-dimension vectors clustered by type (15 centroids per type) so that a
/// nearest-neighbour query has real neighbours, and Japanese text assembled from contract / invoice / manual phrases
/// plus one selective token per document (its invoice number).
/// </summary>
public static class Corpus
{
    public const int Dimensions = 1024;
    public const int TypesPerTenant = 20;
    public const int OwnersPerTenant = 50;
    public const int CentroidsPerType = 15;

    public static readonly Guid MainTenant = Guid.Parse("11111111-0000-0000-0000-000000000001");
    public static readonly Guid OtherTenant = Guid.Parse("22222222-0000-0000-0000-000000000002");

    public static Guid TypeId(Guid tenant, int i) => Det($"type:{tenant}:{i}");
    public static Guid OwnerId(Guid tenant, int i) => Det($"owner:{tenant}:{i}");

    public static Guid Det(string key)
        => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(key)));

    private static long _seq;

    /// <summary>SQL Server orders uniqueidentifier by its last six bytes first, like ABP's SequentialAtEnd.</summary>
    public static Guid NextSequentialGuid()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        var n = Interlocked.Increment(ref _seq);
        for (var i = 0; i < 6; i++)
        {
            bytes[15 - i] = (byte)(n >> (8 * i));
        }

        return new Guid(bytes);
    }

    public static float[] Centroid(Guid tenant, int type, int k)
    {
        // Not HashCode.Combine: it is randomized per process, and the query side runs in a different process.
        var rng = new Random(BitConverter.ToInt32(Det($"centroid:{tenant}:{type}:{k}").ToByteArray(), 0));
        return Normalize(Gaussian(rng, Dimensions, 1f));
    }

    public static float[] Near(float[] centroid, Random rng, float noise)
    {
        var v = Gaussian(rng, Dimensions, noise);
        for (var i = 0; i < v.Length; i++)
        {
            v[i] += centroid[i];
        }

        return Normalize(v);
    }

    public static float[] Gaussian(Random rng, int n, float scale)
    {
        var v = new float[n];
        for (var i = 0; i < n; i++)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = rng.NextDouble();
            v[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2) * scale / Math.Sqrt(n));
        }

        return v;
    }

    public static float[] Normalize(float[] v)
    {
        var norm = (float)Math.Sqrt(v.Sum(x => x * x));
        for (var i = 0; i < v.Length; i++)
        {
            v[i] /= norm;
        }

        return v;
    }

    /// <summary>Zipf-like split of <paramref name="total"/> chunks over the types: type 0 is the largest.</summary>
    public static int[] TypeSizes(int total)
    {
        var w = Enumerable.Range(0, TypesPerTenant).Select(i => 1.0 / Math.Pow(i + 1, 0.9)).ToArray();
        var sum = w.Sum();
        return w.Select(x => (int)(total * x / sum)).ToArray();
    }

    private static readonly string[] Parties = ["株式会社アオバ商事", "ABC株式会社", "東邦物流株式会社", "合同会社ミライ", "株式会社サクラ電機", "北斗建設株式会社"];

    private static readonly string[] Phrases =
    [
        "本契約は、甲と乙の間で締結される。", "乙は毎月末日までに賃料を支払うものとする。", "支払期限を過ぎた場合は遅延損害金が発生する。",
        "契約期間は締結日から一年間とし、自動更新される。", "秘密情報は第三者に開示してはならない。", "損害賠償の上限は契約金額とする。",
        "請求書の内容をご確認ください。", "振込手数料は貴社負担でお願いいたします。", "検収後三十日以内にお支払いください。",
        "サーバーの保守作業は夜間に実施する。", "障害発生時は直ちに担当者へ連絡すること。", "バックアップは毎日取得している。",
        "見積金額には消費税が含まれています。", "納期は発注日から二週間とする。", "仕様変更は書面により合意するものとする。",
        "物件の引渡日は別途定める。", "保証金は契約終了時に返還する。", "管理規約を遵守するものとする。",
        "従業員は就業規則に従わなければならない。", "有給休暇の申請は三日前までに行うこと。", "出張旅費は実費精算とする。",
        "本書に定めのない事項は協議により決定する。", "準拠法は日本法とし、東京地方裁判所を専属的合意管轄とする。", "反社会的勢力ではないことを表明する。"
    ];

    public static string ChunkText(Random rng, int docSeq, int ordinal)
    {
        var sb = new System.Text.StringBuilder();
        if (ordinal == 0)
        {
            sb.Append($"請求番号 INV-{docSeq:D6} 取引先 {Parties[docSeq % Parties.Length]}。");
        }

        var n = 4 + rng.Next(4);
        for (var i = 0; i < n; i++)
        {
            sb.Append(Phrases[rng.Next(Phrases.Length)]);
        }

        return sb.ToString();
    }
}

public static class Seed
{
    public static async Task RunAsync(string masterCs, string pocCs, int mainChunks, int otherChunks)
    {
        await Sql.ExecAsync(masterCs, """
            IF DB_ID('IndexPoc') IS NOT NULL BEGIN ALTER DATABASE IndexPoc SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE IndexPoc; END;
            CREATE DATABASE IndexPoc;
            ALTER DATABASE IndexPoc SET RECOVERY SIMPLE;
            """);

        await Sql.ExecAsync(pocCs, """
            CREATE TABLE dbo.VaultIndexChunks (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_VaultIndexChunks PRIMARY KEY CLUSTERED,
                TenantId uniqueidentifier NULL,
                DocumentId uniqueidentifier NOT NULL,
                DocumentTypeId uniqueidentifier NOT NULL,
                OwnerId uniqueidentifier NULL,
                Ordinal int NOT NULL,
                Text nvarchar(max) NOT NULL,
                Embedding vector(1024) NOT NULL);
            CREATE TABLE dbo.VaultIndexRuns (
                Id uniqueidentifier NOT NULL PRIMARY KEY, TenantId uniqueidentifier NULL, DocumentId uniqueidentifier NOT NULL,
                ChunkCount int NOT NULL, Succeeded bit NOT NULL, Error nvarchar(max) NULL, StartedAt datetime2 NOT NULL, FinishedAt datetime2 NOT NULL);
            CREATE TABLE dbo.VaultIndexDocumentFields (
                TenantId uniqueidentifier NULL, DocumentId uniqueidentifier NOT NULL, DocumentTypeId uniqueidentifier NOT NULL,
                Name nvarchar(64) NOT NULL, StringValue nvarchar(256) NULL, NumberValue decimal(28,8) NULL,
                CONSTRAINT PK_VaultIndexDocumentFields PRIMARY KEY (DocumentId, Name));
            """);

        var sw = Stopwatch.StartNew();
        var fields = new List<DocumentField>();
        await LoadTenantAsync(pocCs, Corpus.MainTenant, mainChunks, docSeqStart: 0, fields);
        await LoadTenantAsync(pocCs, Corpus.OtherTenant, otherChunks, docSeqStart: 1_000_000, fields);
        Console.WriteLine($"bulk load: {mainChunks + otherChunks} chunks in {sw.Elapsed.TotalSeconds:F1}s");

        await BulkFieldsAsync(pocCs, fields);

        sw.Restart();
        await Sql.ExecAsync(pocCs, """
            CREATE INDEX IX_Chunks_Scope ON dbo.VaultIndexChunks (TenantId, DocumentTypeId, OwnerId) INCLUDE (DocumentId);
            CREATE INDEX IX_Chunks_Document ON dbo.VaultIndexChunks (TenantId, DocumentId);
            CREATE INDEX IX_Fields_String ON dbo.VaultIndexDocumentFields (TenantId, DocumentTypeId, Name, StringValue) INCLUDE (DocumentId);
            CREATE INDEX IX_Fields_Number ON dbo.VaultIndexDocumentFields (TenantId, DocumentTypeId, Name, NumberValue) INCLUDE (DocumentId);
            """);
        Console.WriteLine($"b-tree indexes: {sw.Elapsed.TotalSeconds:F1}s");

        sw.Restart();
        await Sql.ExecAsync(pocCs, """
            CREATE FULLTEXT CATALOG ftc AS DEFAULT;
            CREATE FULLTEXT INDEX ON dbo.VaultIndexChunks (Text LANGUAGE 1041) KEY INDEX PK_VaultIndexChunks WITH CHANGE_TRACKING AUTO;
            """);
        await FullText.WaitForPopulationAsync(pocCs, "ftc");
        Console.WriteLine($"full-text initial population ({mainChunks + otherChunks} chunks): {sw.Elapsed.TotalSeconds:F1}s");

        var size = await Sql.ScalarAsync<string>(pocCs, """
            SELECT CONCAT(CAST(SUM(a.total_pages) * 8 / 1024 AS int), ' MB') FROM sys.partitions p JOIN sys.allocation_units a ON a.container_id = p.partition_id
            WHERE p.object_id = OBJECT_ID('dbo.VaultIndexChunks')
            """);
        Console.WriteLine($"chunk table size: {size}");
    }

    private static async Task LoadTenantAsync(string cs, Guid tenant, int chunks, int docSeqStart, List<DocumentField> fields)
    {
        var rng = new Random(tenant.GetHashCode());
        var sizes = Corpus.TypeSizes(chunks);
        var table = NewTable();
        var docSeq = docSeqStart;

        using var bulk = new SqlBulkCopy(cs) { DestinationTableName = "dbo.VaultIndexChunks", BatchSize = 5000, BulkCopyTimeout = 0 };
        foreach (DataColumn c in table.Columns)
        {
            bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
        }

        for (var type = 0; type < Corpus.TypesPerTenant; type++)
        {
            var typeId = Corpus.TypeId(tenant, type);
            var centroids = Enumerable.Range(0, Corpus.CentroidsPerType).Select(k => Corpus.Centroid(tenant, type, k)).ToArray();
            var remaining = sizes[type];
            while (remaining > 0)
            {
                var docId = Corpus.Det($"doc:{docSeq}");
                var owner = Corpus.OwnerId(tenant, rng.Next(Corpus.OwnersPerTenant));
                var centroid = centroids[rng.Next(centroids.Length)];
                var n = Math.Min(remaining, 5 + rng.Next(11));
                for (var o = 0; o < n; o++)
                {
                    table.Rows.Add(
                        Corpus.NextSequentialGuid(), tenant, docId, typeId, owner, o,
                        Corpus.ChunkText(rng, docSeq, o),
                        new SqlVector<float>(Corpus.Near(centroid, rng, 0.6f)));
                }

                if (type == 0)
                {
                    // Field values for the field-filter comparison (question 6): a Zipf-ish counterparty and an amount.
                    var party = (int)Math.Floor(Math.Pow(rng.NextDouble(), 2.5) * 300);
                    fields.Add(new DocumentField { TenantId = tenant, DocumentId = docId, DocumentTypeId = typeId, Name = "counterparty", StringValue = $"取引先{party:D3}" });
                    fields.Add(new DocumentField { TenantId = tenant, DocumentId = docId, DocumentTypeId = typeId, Name = "amount", NumberValue = rng.Next(1, 10_000_000) });
                }

                remaining -= n;
                docSeq++;
                if (table.Rows.Count >= 5000)
                {
                    await bulk.WriteToServerAsync(table);
                    // DataTable.Clear() throws on a SqlVector<float> column (it is stored as a UDT); start a new one.
                    table = NewTable();
                }
            }
        }

        if (table.Rows.Count > 0)
        {
            await bulk.WriteToServerAsync(table);
        }

        Console.WriteLine($"tenant {tenant}: {chunks} chunks, {docSeq - docSeqStart} documents, type sizes {string.Join("/", sizes)}");
    }

    private static DataTable NewTable()
    {
        var t = new DataTable();
        t.Columns.Add("Id", typeof(Guid));
        t.Columns.Add("TenantId", typeof(Guid));
        t.Columns.Add("DocumentId", typeof(Guid));
        t.Columns.Add("DocumentTypeId", typeof(Guid));
        t.Columns.Add("OwnerId", typeof(Guid));
        t.Columns.Add("Ordinal", typeof(int));
        t.Columns.Add("Text", typeof(string));
        t.Columns.Add("Embedding", typeof(SqlVector<float>));
        return t;
    }

    private static async Task BulkFieldsAsync(string cs, List<DocumentField> fields)
    {
        var t = new DataTable();
        t.Columns.Add("TenantId", typeof(Guid));
        t.Columns.Add("DocumentId", typeof(Guid));
        t.Columns.Add("DocumentTypeId", typeof(Guid));
        t.Columns.Add("Name", typeof(string));
        t.Columns.Add("StringValue", typeof(string));
        t.Columns.Add("NumberValue", typeof(decimal));
        foreach (var f in fields)
        {
            t.Rows.Add(f.TenantId, f.DocumentId, f.DocumentTypeId, f.Name, (object?)f.StringValue ?? DBNull.Value, (object?)f.NumberValue ?? DBNull.Value);
        }

        using var bulk = new SqlBulkCopy(cs) { DestinationTableName = "dbo.VaultIndexDocumentFields", BulkCopyTimeout = 0 };
        foreach (DataColumn c in t.Columns)
        {
            bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
        }

        await bulk.WriteToServerAsync(t);
        Console.WriteLine($"field rows: {fields.Count}");
    }
}

public static class Sql
{
    public static async Task ExecAsync(string cs, string sql)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        foreach (var batch in sql.Split("\nGO", StringSplitOptions.RemoveEmptyEntries))
        {
            await using var cmd = new SqlCommand("SET QUOTED_IDENTIFIER ON; " + batch, c) { CommandTimeout = 0 };
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public static async Task<T> ScalarAsync<T>(string cs, string sql, params SqlParameter[] ps)
    {
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 0 };
        cmd.Parameters.AddRange(ps);
        var r = await cmd.ExecuteScalarAsync();
        return (T)Convert.ChangeType(r!, typeof(T));
    }
}

public static class FullText
{
    public static async Task WaitForPopulationAsync(string cs, string catalog)
    {
        // PopulateStatus 0 = idle. Poll until the initial crawl is done.
        while (await Sql.ScalarAsync<int>(cs, $"SELECT CAST(FULLTEXTCATALOGPROPERTY('{catalog}', 'PopulateStatus') AS int)") != 0)
        {
            await Task.Delay(500);
        }
    }
}

public static class Stats
{
    public static string Summary(List<double> ms)
    {
        ms.Sort();
        double P(double p) => ms[Math.Min(ms.Count - 1, (int)Math.Ceiling(p * ms.Count) - 1)];
        return $"p50 {P(0.5),7:F1} ms  p95 {P(0.95),7:F1} ms  max {ms[^1],7:F1} ms";
    }
}
