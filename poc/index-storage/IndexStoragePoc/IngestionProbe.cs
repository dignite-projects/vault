using System.Diagnostics;
using System.Text;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DataIngestion;
using Microsoft.ML.Tokenizers;

namespace IndexStoragePoc;

/// <summary>
/// The third PoC item: can DataIngestion carry Index's write side? Two ways are tried. (1) Its building blocks on the
/// Markdown string Extract hands over: MarkdownReader over a stream, HeaderChunker, then Index's own transactional
/// write. (2) Its IngestionPipeline with an IngestionChunkWriter that writes chunks and a run record in one
/// transaction, to see what the pipeline gives a writer: which document, which tenant, how many calls, what result.
/// </summary>
public static class IngestionProbe
{
    private const string SampleMarkdown = """
        # 賃貸借契約書

        本契約は、賃貸人（以下「甲」という。）と賃借人（以下「乙」という。）の間で締結される。

        ## 第1条（目的）

        甲は乙に対し、下記の物件を住居として賃貸し、乙はこれを賃借する。物件の引渡日は別途定める。

        ## 第2条（賃料）

        乙は毎月末日までに翌月分の賃料を甲の指定する口座に振り込む方法により支払うものとする。

        ### 2.1 遅延損害金

        支払期限を過ぎた場合、乙は年14.6%の割合による遅延損害金を支払う。

        ## 第3条（契約期間）

        契約期間は締結日から二年間とし、期間満了の三か月前までに双方から申し出がない場合は同一条件で更新される。

        | 項目 | 金額 |
        | --- | --- |
        | 賃料 | 120,000円 |
        | 共益費 | 8,000円 |
        | 保証金 | 240,000円 |
        """;

    private static readonly AsyncLocal<Guid?> AmbientTenant = new();

    public static async Task RunAsync(string cs)
    {
        var tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");
        var chunkerOptions = new IngestionChunkerOptions(tokenizer) { MaxTokensPerChunk = 120 };

        Console.WriteLine("--- (1) building blocks on the Markdown string ---");
        var reader = new MarkdownReader();
        var document = await reader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(SampleMarkdown)), "doc-1", "text/markdown");
        var chunker = new HeaderChunker(chunkerOptions);
        var n = 0;
        await foreach (var chunk in chunker.ProcessAsync(document))
        {
            n++;
            Console.WriteLine($"chunk {n}: {tokenizer.CountTokens(chunk.Content),3} tokens  context=\"{chunk.Context}\"  text=\"{Flat(chunk.Content, 40)}\"");
        }

        Console.WriteLine("--- (2) IngestionPipeline with an EF chunk writer ---");
        var dir = Directory.CreateTempSubdirectory("vault-index-poc-");
        var files = new List<FileInfo>();
        for (var i = 0; i < 3; i++)
        {
            var path = Path.Combine(dir.FullName, $"{Corpus.Det($"ingest:{i}")}.md");
            await File.WriteAllTextAsync(path, i == 2 ? "" : SampleMarkdown);
            files.Add(new FileInfo(path));
        }

        var writer = new EfChunkWriter(cs);
        using var pipeline = new IngestionPipeline<string>(reader, chunker, writer, new IngestionPipelineOptions(), null);
        AmbientTenant.Value = Corpus.MainTenant;
        var sw = Stopwatch.StartNew();
        await foreach (var result in pipeline.ProcessAsync(files))
        {
            Console.WriteLine($"result: id={Path.GetFileName(result.DocumentId)} succeeded={result.Succeeded} error={result.Exception?.GetType().Name}: {Flat(result.Exception?.Message ?? "", 80)}");
        }

        Console.WriteLine($"pipeline over {files.Count} files: {sw.Elapsed.TotalMilliseconds:F0} ms; writer calls: {writer.Calls}; tenant seen by writer: {string.Join(",", writer.TenantsSeen.Select(x => x?.ToString() ?? "null"))}");
        await using (var db = PocDbContext.Create(cs, Corpus.MainTenant))
        {
            var runs = await db.Runs.OrderBy(r => r.StartedAt).ToListAsync();
            foreach (var r in runs)
            {
                Console.WriteLine($"run row: doc={r.DocumentId} chunks={r.ChunkCount} ok={r.Succeeded}");
            }
        }

        dir.Delete(recursive: true);
    }

    private static string Flat(string s, int max)
    {
        s = s.Replace("\r", "").Replace("\n", "⏎");
        return s.Length <= max ? s : s[..max] + "…";
    }

    /// <summary>
    /// Writes one document's chunks and its run record in one transaction, replacing what the document had. The
    /// tenant comes from ambient state, because IngestionDocument carries an identifier and nothing else.
    /// </summary>
    private sealed class EfChunkWriter(string cs) : IngestionChunkWriter<string>
    {
        public int Calls;
        public List<Guid?> TenantsSeen { get; } = [];

        public override async Task WriteAsync(IAsyncEnumerable<IngestionChunk<string>> chunks, CancellationToken cancellationToken = default)
        {
            Calls++;
            var tenant = AmbientTenant.Value;
            TenantsSeen.Add(tenant);
            await using var db = PocDbContext.Create(cs, tenant);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            var started = DateTime.UtcNow;
            Guid? docId = null;
            var ordinal = 0;
            var rng = new Random(1);
            await foreach (var chunk in chunks.WithCancellation(cancellationToken))
            {
                docId ??= Guid.Parse(Path.GetFileNameWithoutExtension(chunk.Document.Identifier));
                if (ordinal == 0)
                {
                    await db.Chunks.Where(c => c.DocumentId == docId).ExecuteDeleteAsync(cancellationToken);
                }

                db.Chunks.Add(new Chunk
                {
                    Id = Corpus.NextSequentialGuid(), TenantId = tenant, DocumentId = docId.Value,
                    DocumentTypeId = Corpus.TypeId(Corpus.MainTenant, 0), Ordinal = ordinal++, Text = chunk.Content,
                    Embedding = new SqlVector<float>(Corpus.Near(Corpus.Centroid(Corpus.MainTenant, 0, 0), rng, 0.6f))
                });
            }

            if (docId is { } id)
            {
                db.Runs.Add(new IndexRun { Id = Guid.NewGuid(), TenantId = tenant, DocumentId = id, ChunkCount = ordinal, Succeeded = true, StartedAt = started, FinishedAt = DateTime.UtcNow });
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
    }
}
