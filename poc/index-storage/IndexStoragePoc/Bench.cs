using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;

namespace IndexStoragePoc;

/// <summary>Question 4: exact vector search under the read-scope filters, through EF Core 10, as Index would issue it.</summary>
public static class VectorBench
{
    private record ScopeCase(string Name, int[]? TypeIndexes, int? OwnerIndex);

    public static async Task RunAsync(string cs)
    {
        var t = Corpus.MainTenant;
        var cases = new ScopeCase[]
        {
            new("1 small type (#15)", [15], null),
            new("1 large type (#0)", [0], null),
            new("5 types (#0-4)", [0, 1, 2, 3, 4], null),
            new("5 small types OR owner", [10, 11, 12, 13, 14], 3),
            new("whole tenant (ReadAll)", null, null),
        };

        var rng = new Random(7);
        foreach (var c in cases)
        {
            await using var db = PocDbContext.Create(cs, t);
            var rows = await Scope(db, c).CountAsync();
            var times = new List<double>();
            var distanceFloor = double.MaxValue;
            for (var i = 0; i < 23; i++)
            {
                var typeIdx = c.TypeIndexes?[rng.Next(c.TypeIndexes.Length)] ?? rng.Next(Corpus.TypesPerTenant);
                var qv = new SqlVector<float>(Corpus.Near(Corpus.Centroid(t, typeIdx, rng.Next(Corpus.CentroidsPerType)), rng, 0.6f));
                var sw = Stopwatch.StartNew();
                var top = await Scope(db, c)
                    .OrderBy(x => EF.Functions.VectorDistance("cosine", x.Embedding, qv))
                    .Take(10)
                    .Select(x => new { x.Id, Distance = EF.Functions.VectorDistance("cosine", x.Embedding, qv) })
                    .ToListAsync();
                sw.Stop();
                if (i >= 3)
                {
                    times.Add(sw.Elapsed.TotalMilliseconds);
                }

                distanceFloor = Math.Min(distanceFloor, top[0].Distance);
            }

            Console.WriteLine($"{c.Name,-26} rows {rows,7}  {Stats.Summary(times)}  (best distance {distanceFloor:F3})");
        }

        await using (var db = PocDbContext.Create(cs, t))
        {
            var qv = new SqlVector<float>(Corpus.Centroid(t, 0, 0));
            Console.WriteLine("--- generated SQL (5 small types OR owner) ---");
            Console.WriteLine(Scope(db, cases[3]).OrderBy(x => EF.Functions.VectorDistance("cosine", x.Embedding, qv)).Take(10).Select(x => x.Id).ToQueryString());
        }

        await ConcurrencyAsync(cs, t, cases[1], seconds: 15, workers: 8);
        await ConcurrencyAsync(cs, t, cases[4], seconds: 15, workers: 8);
        await ColdAsync(cs, t, cases[4]);
    }

    private static IQueryable<Chunk> Scope(PocDbContext db, ScopeCase c)
    {
        if (c.TypeIndexes is null)
        {
            return db.Chunks;
        }

        var types = c.TypeIndexes.Select(i => Corpus.TypeId(Corpus.MainTenant, i)).ToList();
        if (c.OwnerIndex is { } o)
        {
            var owner = Corpus.OwnerId(Corpus.MainTenant, o);
            return db.Chunks.Where(x => types.Contains(x.DocumentTypeId) || x.OwnerId == owner);
        }

        return db.Chunks.Where(x => types.Contains(x.DocumentTypeId));
    }

    private static async Task ConcurrencyAsync(string cs, Guid t, ScopeCase c, int seconds, int workers)
    {
        var done = 0;
        var until = DateTime.UtcNow.AddSeconds(seconds);
        var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
        await Task.WhenAll(Enumerable.Range(0, workers).Select(async w =>
        {
            var rng = new Random(100 + w);
            await using var db = PocDbContext.Create(cs, t);
            while (DateTime.UtcNow < until)
            {
                var typeIdx = c.TypeIndexes?[rng.Next(c.TypeIndexes.Length)] ?? rng.Next(Corpus.TypesPerTenant);
                var qv = new SqlVector<float>(Corpus.Near(Corpus.Centroid(t, typeIdx, rng.Next(Corpus.CentroidsPerType)), rng, 0.6f));
                var sw = Stopwatch.StartNew();
                await Scope(db, c).OrderBy(x => EF.Functions.VectorDistance("cosine", x.Embedding, qv)).Take(10).Select(x => x.Id).ToListAsync();
                latencies.Add(sw.Elapsed.TotalMilliseconds);
                Interlocked.Increment(ref done);
            }
        }));
        Console.WriteLine($"{c.Name,-26} {workers} concurrent: {done / (double)seconds:F1} queries/s, {Stats.Summary(latencies.ToList())}");
    }

    private static async Task ColdAsync(string cs, Guid t, ScopeCase c)
    {
        await Sql.ExecAsync(cs, "CHECKPOINT; DBCC DROPCLEANBUFFERS;");
        await using var db = PocDbContext.Create(cs, t);
        var qv = new SqlVector<float>(Corpus.Centroid(t, 0, 0));
        var sw = Stopwatch.StartNew();
        await Scope(db, c).OrderBy(x => EF.Functions.VectorDistance("cosine", x.Embedding, qv)).Take(10).Select(x => x.Id).ToListAsync();
        Console.WriteLine($"{c.Name,-26} cold buffer pool: {sw.Elapsed.TotalMilliseconds:F0} ms");
    }
}

/// <summary>
/// Question 4: ranked full-text on EF Core 10, which has no FREETEXTTABLE / CONTAINSTABLE translation. The shape tried
/// here is SqlQuery for the ranked keys, joined in LINQ to the filtered DbSet, so the tenant query filter and the
/// read-scope predicate stay EF's job rather than strings in the raw SQL.
/// </summary>
public static class RankedFullTextBench
{
    public static async Task RunAsync(string cs)
    {
        var t = Corpus.MainTenant;
        var type0 = Corpus.TypeId(t, 0);
        var terms = new (string Label, string Condition)[]
        {
            ("selective: one invoice no.", "\"INV-000123\""),
            ("medium: 遅延損害金", "\"遅延損害金\""),
            ("common: 契約", "\"契約\""),
            ("two terms AND", "\"保証金\" AND \"返還\""),
        };

        await using (var db = PocDbContext.Create(cs, t))
        {
            var cond = terms[1].Condition;
            var q = from h in db.Database.SqlQuery<KeywordHit>($"SELECT [KEY] AS ChunkId, [RANK] AS Rank FROM CONTAINSTABLE(dbo.VaultIndexChunks, Text, {cond})")
                    join c in db.Chunks on h.ChunkId equals c.Id
                    where c.DocumentTypeId == type0
                    orderby h.Rank descending
                    select new { c.Id, h.Rank };
            var sql = q.Take(10).ToQueryString();
            Console.WriteLine("--- generated SQL ---");
            Console.WriteLine(sql);
            Console.WriteLine($"tenant filter present in SQL: {sql.Contains("[TenantId]")}");
        }

        foreach (var (label, cond) in terms)
        {
            foreach (var scoped in new[] { true, false })
            {
                await using var db = PocDbContext.Create(cs, t);
                var times = new List<double>();
                var matches = 0;
                for (var i = 0; i < 13; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var q = from h in db.Database.SqlQuery<KeywordHit>($"SELECT [KEY] AS ChunkId, [RANK] AS Rank FROM CONTAINSTABLE(dbo.VaultIndexChunks, Text, {cond})")
                            join c in db.Chunks on h.ChunkId equals c.Id
                            where !scoped || c.DocumentTypeId == type0
                            orderby h.Rank descending
                            select new { c.Id, h.Rank };
                    var top = await q.Take(10).ToListAsync();
                    sw.Stop();
                    if (i >= 3)
                    {
                        times.Add(sw.Elapsed.TotalMilliseconds);
                    }

                    matches = top.Count;
                }

                var total = await Sql.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM CONTAINSTABLE(dbo.VaultIndexChunks, Text, @c)", new SqlParameter("@c", cond));
                Console.WriteLine($"{label,-28} {(scoped ? "type #0" : "tenant "),-8} engine matches {total,7}  {Stats.Summary(times)}");
            }
        }
    }
}

/// <summary>Question 4: how long after the commit a written chunk is found by the keyword leg (full-text is asynchronous).</summary>
public static class FullTextLagBench
{
    public static async Task RunAsync(string cs)
    {
        var t = Corpus.MainTenant;
        var type0 = Corpus.TypeId(t, 0);
        var rng = new Random(11);

        var single = new List<double>();
        for (var trial = 0; trial < 20; trial++)
        {
            var token = $"PRB{trial:D2}X{rng.Next(100000, 999999)}";
            await InsertAsync(cs, t, type0, Guid.NewGuid(), [token], rng);
            single.Add(await WaitFoundAsync(cs, $"\"{token}\"", expected: 1));
        }

        Console.WriteLine($"1 chunk, time until CONTAINS finds it:          {Stats.Summary(single)}");

        var batch = new List<double>();
        for (var trial = 0; trial < 10; trial++)
        {
            var token = $"BAT{trial:D2}X{rng.Next(100000, 999999)}";
            await InsertAsync(cs, t, type0, Guid.NewGuid(), Enumerable.Repeat(token, 50).ToArray(), rng);
            batch.Add(await WaitFoundAsync(cs, $"\"{token}\"", expected: 50));
        }

        Console.WriteLine($"50 chunks (one document), until all 50 found:   {Stats.Summary(batch)}");

        // Replacement the way an index job does it: delete the document's chunks and insert the new ones in one transaction.
        var replace = new List<double>();
        for (var trial = 0; trial < 10; trial++)
        {
            var doc = Guid.NewGuid();
            var oldToken = $"OLD{trial:D2}X{rng.Next(100000, 999999)}";
            var newToken = $"NEW{trial:D2}X{rng.Next(100000, 999999)}";
            await InsertAsync(cs, t, type0, doc, Enumerable.Repeat(oldToken, 20).ToArray(), rng);
            await WaitFoundAsync(cs, $"\"{oldToken}\"", expected: 20);
            await InsertAsync(cs, t, type0, doc, Enumerable.Repeat(newToken, 20).ToArray(), rng, replaceDocument: true);
            var sw = Stopwatch.StartNew();
            while (true)
            {
                var oldCount = await Sql.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.VaultIndexChunks WHERE CONTAINS(Text, @c)", new SqlParameter("@c", $"\"{oldToken}\""));
                var newCount = await Sql.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.VaultIndexChunks WHERE CONTAINS(Text, @c)", new SqlParameter("@c", $"\"{newToken}\""));
                if (oldCount == 0 && newCount == 20)
                {
                    break;
                }

                await Task.Delay(10);
            }

            replace.Add(sw.Elapsed.TotalMilliseconds);
        }

        Console.WriteLine($"replace 20 chunks in one tx, until consistent:  {Stats.Summary(replace)}");
        Console.WriteLine("(the vector leg sees the same rows at commit; only the keyword leg lags)");
    }

    private static async Task<double> WaitFoundAsync(string cs, string condition, int expected)
    {
        var sw = Stopwatch.StartNew();
        while (await Sql.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM dbo.VaultIndexChunks WHERE CONTAINS(Text, @c)", new SqlParameter("@c", condition)) < expected)
        {
            await Task.Delay(10);
        }

        return sw.Elapsed.TotalMilliseconds;
    }

    private static async Task InsertAsync(string cs, Guid tenant, Guid type, Guid doc, string[] tokens, Random rng, bool replaceDocument = false)
    {
        await using var db = PocDbContext.Create(cs, tenant);
        await using var tx = await db.Database.BeginTransactionAsync();
        if (replaceDocument)
        {
            await db.Chunks.Where(c => c.DocumentId == doc).ExecuteDeleteAsync();
        }

        var centroid = Corpus.Centroid(tenant, 0, 0);
        for (var i = 0; i < tokens.Length; i++)
        {
            db.Chunks.Add(new Chunk
            {
                Id = Corpus.NextSequentialGuid(), TenantId = tenant, DocumentId = doc, DocumentTypeId = type, Ordinal = i,
                Text = Corpus.ChunkText(rng, 0, 1) + " 照合用 " + tokens[i],
                Embedding = new SqlVector<float>(Corpus.Near(centroid, rng, 0.6f))
            });
        }

        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }
}

/// <summary>
/// Question 6: field filters, two ways. A: Index keeps the reviewed field values in its own table and joins them.
/// B: Extract resolves the field condition to document ids first and Index searches within that id list.
/// </summary>
public static class FieldFilterBench
{
    public static async Task RunAsync(string cs)
    {
        var t = Corpus.MainTenant;
        var type0 = Corpus.TypeId(t, 0);

        var counts = new List<(string Value, int Docs)>();
        await using (var db = PocDbContext.Create(cs, t))
        {
            counts = (await db.DocumentFields.Where(f => f.Name == "counterparty").GroupBy(f => f.StringValue)
                    .Select(g => new { g.Key, N = g.Count() }).ToListAsync())
                .Select(x => (x.Key!, x.N)).OrderBy(x => x.N).ToList();
        }

        (string Value, int Docs) Pick(int target) => counts.MinBy(c => Math.Abs(c.Docs - target));
        var picks = new[] { Pick(3), Pick(30), Pick(300), counts[^1] };
        var rng = new Random(5);

        foreach (var (value, docs) in picks)
        {
            await RunCaseAsync(cs, t, type0, $"counterparty = {value}", docs,
                db => db.Chunks.Where(c => c.DocumentTypeId == type0 &&
                                           db.DocumentFields.Any(f => f.DocumentId == c.DocumentId && f.Name == "counterparty" && f.StringValue == value)),
                async db => await db.DocumentFields.Where(f => f.Name == "counterparty" && f.StringValue == value).Select(f => f.DocumentId).ToListAsync(),
                rng);
        }

        await RunCaseAsync(cs, t, type0, "amount > 5,000,000", -1,
            db => db.Chunks.Where(c => c.DocumentTypeId == type0 &&
                                       db.DocumentFields.Any(f => f.DocumentId == c.DocumentId && f.Name == "amount" && f.NumberValue > 5_000_000m)),
            async db => await db.DocumentFields.Where(f => f.Name == "amount" && f.NumberValue > 5_000_000m).Select(f => f.DocumentId).ToListAsync(),
            rng);
    }

    private static async Task RunCaseAsync(
        string cs, Guid t, Guid type0, string label, int docs,
        Func<PocDbContext, IQueryable<Chunk>> joined,
        Func<PocDbContext, Task<List<Guid>>> resolveIds,
        Random rng)
    {
        List<Guid> ids;
        await using (var db = PocDbContext.Create(cs, t))
        {
            ids = await resolveIds(db);
        }

        var a = new List<double>();
        var b = new List<double>();
        var agree = true;
        await using (var db = PocDbContext.Create(cs, t))
        {
            for (var i = 0; i < 23; i++)
            {
                var qv = new SqlVector<float>(Corpus.Near(Corpus.Centroid(t, 0, rng.Next(Corpus.CentroidsPerType)), rng, 0.6f));

                var sw = Stopwatch.StartNew();
                var ra = await joined(db).OrderBy(x => EF.Functions.VectorDistance("cosine", x.Embedding, qv)).Take(10).Select(x => x.Id).ToListAsync();
                var ta = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                var rb = await db.Chunks.Where(c => c.DocumentTypeId == type0 && EF.Parameter(ids).Contains(c.DocumentId))
                    .OrderBy(x => EF.Functions.VectorDistance("cosine", x.Embedding, qv)).Take(10).Select(x => x.Id).ToListAsync();
                var tb = sw.Elapsed.TotalMilliseconds;

                agree &= ra.SequenceEqual(rb);
                if (i >= 3)
                {
                    a.Add(ta);
                    b.Add(tb);
                }
            }
        }

        var chunkRows = await Sql.ScalarAsync<int>(cs,
            $"SELECT COUNT(*) FROM dbo.VaultIndexChunks c WHERE c.DocumentId IN (SELECT CAST(value AS uniqueidentifier) FROM OPENJSON(@ids))",
            new SqlParameter("@ids", System.Text.Json.JsonSerializer.Serialize(ids)));
        Console.WriteLine($"{label,-26} docs {ids.Count,5} chunks {chunkRows,6} | A join  {Stats.Summary(a)}");
        Console.WriteLine($"{"",-26} {"",-23} | B ids   {Stats.Summary(b)}  id list {ids.Count * 39 / 1024.0:F0} KB, same top-10: {agree}");
    }
}
